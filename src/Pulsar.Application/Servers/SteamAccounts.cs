using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

/// <summary>Encrypts Steam account passwords for the database with the panel's Data Protection key ring (docs/architecture/08).</summary>
public interface ISteamAccountSecretProtector
{
    byte[] Protect(string value);

    /// <exception cref="CryptographicException">The key ring that encrypted it is gone.</exception>
    string Unprotect(byte[] protectedValue);
}

/// <param name="Password">Null on an update keeps the current password.</param>
public sealed record SteamAccountDefinition(string Name, string UserName, string? Password);

/// <param name="Servers">Servers that sign in with it.</param>
public sealed record SteamAccountView(Guid Id, string Name, string UserName, int Servers, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record SteamAccountList(IReadOnlyList<SteamAccountView> Accounts);

/// <summary>
/// Steam accounts for SteamCMD (Phase 13), for holders of <c>settings.manage</c>. The password goes in encrypted and
/// never comes back out; nodes get it only inside the install and update commands of servers that use the account.
/// </summary>
public sealed class SteamAccounts(IPulsarDbContext db, ISteamAccountSecretProtector secrets, IAuditLog audit, TimeProvider clock)
{
    public async Task<SteamAccountList> ListAsync(CancellationToken cancellationToken)
    {
        var accounts = await db.SteamAccounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(cancellationToken);
        var used = await db.Servers.AsNoTracking().Where(s => s.SteamAccountId != null).GroupBy(s => s.SteamAccountId)
            .Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return new SteamAccountList([.. accounts.Select(a => View(a, used.FirstOrDefault(u => u.Id == a.Id)?.Count ?? 0))]);
    }

    public async Task<Result<SteamAccountView>> CreateAsync(SteamAccountDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (await db.SteamAccounts.CountAsync(cancellationToken) >= SteamAccount.MaxAccounts)
        {
            return SteamAccountErrors.TooMany;
        }

        if (Password(definition) is { IsSuccess: false } invalid)
        {
            return invalid.Error!;
        }

        var created = SteamAccount.Create(definition.Name, definition.UserName, Protect(definition), clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error!;
        }

        var account = created.Value;
        if (await db.SteamAccounts.AnyAsync(a => a.Name == account.Name, cancellationToken))
        {
            return SteamAccountErrors.NameTaken;
        }

        db.SteamAccounts.Add(account);
        audit.Record(AuditActions.SteamAccount.Created, metadata: new { account.Name, account.UserName, passwordChanged = true })
            .WithTarget("steam_account", account.Id.ToString());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Someone took the name meanwhile (unique index).
            return SteamAccountErrors.NameTaken;
        }

        return View(account, 0);
    }

    public async Task<Result<SteamAccountView>> UpdateAsync(Guid id, SteamAccountDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var account = await db.SteamAccounts.SingleOrDefaultAsync(a => a.Id == new SteamAccountId(id), cancellationToken);
        if (account is null)
        {
            return SteamAccountErrors.NotFound;
        }

        if (Password(definition) is { IsSuccess: false } invalid)
        {
            return invalid.Error!;
        }

        var password = Protect(definition);
        var changed = account.Change(definition.Name, definition.UserName, password, clock.GetUtcNow());
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        if (await db.SteamAccounts.AnyAsync(a => a.Name == account.Name && a.Id != account.Id, cancellationToken))
        {
            return SteamAccountErrors.NameTaken;
        }

        audit.Record(AuditActions.SteamAccount.Updated, metadata: new { account.Name, account.UserName, passwordChanged = password is not null })
            .WithTarget("steam_account", account.Id.ToString());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            return SteamAccountErrors.NameTaken;
        }

        return View(account, await db.Servers.CountAsync(s => s.SteamAccountId == account.Id, cancellationToken));
    }

    /// <summary>Only an account no server signs in with.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await db.SteamAccounts.SingleOrDefaultAsync(a => a.Id == new SteamAccountId(id), cancellationToken);
        if (account is null)
        {
            return SteamAccountErrors.NotFound;
        }

        if (await db.Servers.AnyAsync(s => s.SteamAccountId == account.Id, cancellationToken))
        {
            return SteamAccountErrors.InUse;
        }

        db.SteamAccounts.Remove(account);
        audit.Record(AuditActions.SteamAccount.Deleted, metadata: new { account.Name, account.UserName }).WithTarget("steam_account", account.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// The login SteamCMD uses for <paramref name="server"/>: its account, or null (anonymous) when it has none and its
    /// game does not need one. Refused when the game needs an account the server lacks, the password cannot be read, or
    /// the node's agent cannot use it.
    /// </summary>
    public async Task<Result<SteamLogin?>> LoginForAsync(Server server, GameManifest manifest, AgentConnectionInfo? agent, string? guardCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(manifest);
        if (server.SteamAccountId is not { } accountId)
        {
            if (NeedsAccount(manifest))
            {
                return SteamAccountErrors.Required;
            }

            return (SteamLogin?)null;
        }

        var account = await db.SteamAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return SteamAccountErrors.NotFound;
        }

        if (agent is not null && !agent.Capabilities.Contains(AgentCapabilities.SteamAccounts))
        {
            return SteamAccountErrors.Unsupported;
        }

        try
        {
            return new SteamLogin(account.Id.Value, account.UserName, secrets.Unprotect(account.PasswordProtected), guardCode);
        }
        catch (CryptographicException)
        {
            return SteamAccountErrors.PasswordUnreadable;
        }
    }

    /// <summary>Whether the game's install or update signs in with an account SteamCMD's anonymous login cannot replace.</summary>
    public static bool NeedsAccount(GameManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []).OfType<SteamCmdStep>().Any(s => s.NeedsAccount);
    }

    /// <summary>A Steam Guard code: 5 letters or digits (mail and mobile app codes alike); null or blank means none.</summary>
    public static Result<string?> GuardCode(string? code)
    {
        var value = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return (string?)null;
        }

        if (value.Length != 5 || !value.All(char.IsAsciiLetterOrDigit))
        {
            return SteamAccountErrors.GuardCodeInvalid;
        }

        return value;
    }

    // SteamCMD splits its own command line on spaces and quotes, and reads an argument starting with + or - as a command
    // or an option: such a password would not reach Steam whole.
    private static Result Password(SteamAccountDefinition definition) =>
        definition.Password is not { Length: > 0 } password
            || (password.Length <= 64 && password[0] is not ('+' or '-') && !password.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"'))
            ? Result.Success()
            : SteamAccountErrors.PasswordInvalid;

    private byte[]? Protect(SteamAccountDefinition definition) => string.IsNullOrEmpty(definition.Password) ? null : secrets.Protect(definition.Password);

    private static SteamAccountView View(SteamAccount account, int servers) =>
        new(account.Id.Value, account.Name, account.UserName, servers, account.CreatedAt, account.UpdatedAt);
}

/// <summary>
/// Steam Guard codes given with a reinstall, held until the install that follows its provisioning is sent (a few
/// seconds later), then forgotten. A code is good for minutes at most: older ones are dropped.
/// </summary>
public sealed class SteamGuardCodes(TimeProvider clock)
{
    private static readonly TimeSpan s_keptFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<ServerId, (string Code, DateTimeOffset At)> _codes = new();

    public void Hold(ServerId server, string code) => _codes[server] = (code, clock.GetUtcNow());

    public string? Take(ServerId server) =>
        _codes.TryRemove(server, out var held) && clock.GetUtcNow() - held.At < s_keptFor ? held.Code : null;
}
