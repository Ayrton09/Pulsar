using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pulsar.Api.Auth;
using Pulsar.Api.Http;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Sftp;

/// <summary>
/// The SFTP sign-in (docs/guides/sftp.md): the panel user name and password, then, for a user who must use two-factor
/// authentication (their own choice or a role's), the authenticator code, through the same checks as the browser's
/// sign-in: Identity's lockout (each password and code check counts, not each connection), disabled users refused, the
/// per-address limit of <c>RateLimiting:Auth</c> (counted apart from the web's), and the audit of failed and successful
/// sign-ins with the client's address. Keyboard-interactive asks one thing per round ("Password: ", then
/// "Verification code: "); the <c>password</c> method never signs in a two-factor user. Unknown users get the same
/// prompts and the same cost (one password hash). Recovery codes are not accepted over SFTP: they are for getting back
/// into the panel, where a new authenticator is set up.
/// </summary>
internal sealed partial class SftpAuthenticator : IDisposable
{
    public const string PasswordPrompt = "Password: ";
    public const string CodePrompt = "Verification code: ";
    public const string Channel = "sftp";

    // Verified when the user name does not exist, so both paths cost one password hash (no user enumeration by timing).
    private static readonly string s_dummyPasswordHash =
        new PasswordHasher<PulsarUser>().HashPassword(new PulsarUser(), "pulsar-sftp-timing-equalizer");

    private readonly IServiceScopeFactory _scopes;
    private readonly SftpConnections _connections;
    private readonly FailedLoginBudget _failures;
    private readonly SftpOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SftpAuthenticator> _logger;
    private readonly PartitionedRateLimiter<string> _perAddress;

    public SftpAuthenticator(IServiceScopeFactory scopes, SftpConnections connections, FailedLoginBudget failures, IOptions<SftpOptions> options,
        IConfiguration configuration, TimeProvider clock, ILogger<SftpAuthenticator> logger)
    {
        _scopes = scopes;
        _connections = connections;
        _failures = failures;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        var limits = configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        _perAddress = PartitionedRateLimiter.Create<string, string>(partition => RateLimitPartition.GetFixedWindowLimiter(partition,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PermitLimit, Window = limits.Window, QueueLimit = 0 }));
    }

    private enum Outcome
    {
        Failed,
        NeedsCode,
        SignedIn,
    }

    public void Dispose() => _perAddress.Dispose();

    /// <summary>The library's authentication event for <paramref name="connection"/>.</summary>
    public void Handle(SftpConnection connection, SshAuthenticatingEventArgs e)
    {
        switch (e.AuthenticationType)
        {
            case SshAuthenticationType.ClientPassword:
                connection.Interactive = null;
                e.AuthenticationTask = PasswordAsync(connection, e.Username ?? string.Empty, e.Password ?? string.Empty, e.Cancellation);
                break;
            case SshAuthenticationType.ClientInteractive when e.InfoResponse is null:
                // A new keyboard-interactive attempt: the password first, whoever the user is.
                connection.Interactive = new SftpInteractiveState(e.Username ?? string.Empty, null, null);
                e.InfoRequest = Prompt(PasswordPrompt);
                break;
            case SshAuthenticationType.ClientInteractive:
                e.AuthenticationTask = InteractiveAsync(connection, e);
                break;
            default:
                // "none", public keys and host-based: never (SSH keys come in a later phase).
                break;
        }
    }

    private static AuthenticationInfoRequestMessage Prompt(string prompt)
    {
        var request = new AuthenticationInfoRequestMessage(string.Empty);
        request.AddPrompt(prompt, false);
        return request;
    }

    private async Task<ClaimsPrincipal?> PasswordAsync(SftpConnection connection, string login, string password, CancellationToken cancellationToken)
    {
        var (outcome, principal, _) = await CheckPasswordAsync(connection, login, password, twoFactorPossible: false, cancellationToken);
        return outcome == Outcome.SignedIn ? principal : null;
    }

    /// <summary>
    /// A keyboard-interactive answer. A right password of a two-factor user sets the next round's prompt on
    /// <paramref name="e"/> and answers no principal (the library then sends the prompt).
    /// </summary>
    private async Task<ClaimsPrincipal?> InteractiveAsync(SftpConnection connection, SshAuthenticatingEventArgs e)
    {
        var state = connection.Interactive;
        var responses = e.InfoResponse?.Responses;
        if (state is null || responses is not { Count: 1 } || responses[0] is not { } answer)
        {
            connection.Interactive = null;
            return null;
        }

        if (state.AwaitingCodeFor is not { } userId)
        {
            var (outcome, principal, pending) = await CheckPasswordAsync(connection, state.Login, answer, twoFactorPossible: true, e.Cancellation);
            switch (outcome)
            {
                case Outcome.SignedIn:
                    return principal;
                case Outcome.NeedsCode:
                    connection.Interactive = pending;
                    e.InfoRequest = Prompt(CodePrompt);
                    return null;
                default:
                    connection.Interactive = null;
                    return null;
            }
        }

        connection.Interactive = null;
        return await CheckCodeAsync(connection, userId, state.ServerShortId, answer, e.Cancellation);
    }

    private async Task<(Outcome Outcome, ClaimsPrincipal? Principal, SftpInteractiveState? Pending)> CheckPasswordAsync(
        SftpConnection connection, string login, string password, bool twoFactorPossible, CancellationToken cancellationToken)
    {
        if (++connection.PasswordChecks > _options.MaxPasswordAttemptsPerConnection)
        {
            connection.Close(SshDisconnectReason.NoMoreAuthMethodsAvailable, "Too many sign-in attempts.");
            return (Outcome.Failed, null, null);
        }

        // As the web's sign-in endpoints do per address (a refused attempt checks nothing and counts nowhere).
        using (var lease = _perAddress.AttemptAcquire(ClientAddress.PartitionOf(connection.Address)))
        {
            if (!lease.IsAcquired)
            {
                LogRateLimited(_logger, connection.Address);
                connection.Close(SshDisconnectReason.TooManyConnections, "Too many sign-in attempts. Try again in a minute.");
                return (Outcome.Failed, null, null);
            }
        }

        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var signIn = services.GetRequiredService<SignInManager<PulsarUser>>();
        var (user, shortId) = await ResolveAsync(services, users, login, cancellationToken);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new PulsarUser(), s_dummyPasswordHash, password);
            await FailAsync(services, null, "invalid_credentials", cancellationToken);
            return (Outcome.Failed, null, null);
        }

        var lockedOut = await users.IsLockedOutAsync(user);
        if (lockedOut || !await signIn.CanSignInAsync(user))
        {
            // Locked-out and disabled users get the answer a wrong password gets, after as long.
            users.PasswordHasher.VerifyHashedPassword(user, s_dummyPasswordHash, password);
            await FailAsync(services, user, lockedOut ? "locked_out" : "not_allowed", cancellationToken);
            return (Outcome.Failed, null, null);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await FailAsync(services, user, await users.IsLockedOutAsync(user) ? "locked_out" : "invalid_credentials", cancellationToken);
            return (Outcome.Failed, null, null);
        }

        if (user.TwoFactorEnabled)
        {
            if (twoFactorPossible)
            {
                // The sign-in (and its audit entry) waits for the second factor, as in the browser.
                return (Outcome.NeedsCode, null, new SftpInteractiveState(login, user.Id, shortId));
            }

            // The "password" method cannot ask for a code: never a way around two-factor authentication.
            await FailAsync(services, user, "two_factor_required", cancellationToken);
            return (Outcome.Failed, null, null);
        }

        if ((await services.GetRequiredService<IPermissionEvaluator>().GetAsync(user.Id, cancellationToken)).RequiresTwoFactor)
        {
            // A role requires two-factor authentication the user has not set up: that happens in the browser first.
            await FailAsync(services, user, "two_factor_setup_required", cancellationToken);
            return (Outcome.Failed, null, null);
        }

        var principal = await SignInAsync(connection, services, users, user, shortId, secondFactor: null, cancellationToken);
        return (principal is null ? Outcome.Failed : Outcome.SignedIn, principal, null);
    }

    private async Task<ClaimsPrincipal?> CheckCodeAsync(SftpConnection connection, Pulsar.Domain.Common.UserId userId, string? shortId, string answer,
        CancellationToken cancellationToken)
    {
        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.TwoFactorEnabled || !await services.GetRequiredService<SignInManager<PulsarUser>>().CanSignInAsync(user))
        {
            return null;
        }

        string? reason = null;
        if (await users.IsLockedOutAsync(user))
        {
            reason = "locked_out";
        }
        else
        {
            var code = PulsarAuthenticatorTokenProvider.Normalize(answer);
            if (code is null || !await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
            {
                // Wrong codes count towards the lockout, like wrong passwords.
                await users.AccessFailedAsync(user);
                reason = "invalid_code";
            }
        }

        if (reason is not null)
        {
            var audit = services.GetRequiredService<IAuditLog>();
            audit.Record(AuditActions.Auth.TwoFactorFailed, AuditOutcome.Failure, AuditActor.ForUser(user.Id, user.UserName),
                    new { method = "authenticator", reason, channel = Channel })
                .WithTarget("user", user.Id.ToString());
            await services.GetRequiredService<PulsarDbContext>().SaveChangesAsync(CancellationToken.None);
            return null;
        }

        return await SignInAsync(connection, services, users, user, shortId, secondFactor: "authenticator", cancellationToken);
    }

    /// <summary>The credentials were right: the session counts for its user (or the user has too many), and it is audited.</summary>
    private async Task<ClaimsPrincipal?> SignInAsync(SftpConnection connection, IServiceProvider services, UserManager<PulsarUser> users, PulsarUser user,
        string? shortId, string? secondFactor, CancellationToken cancellationToken)
    {
        var identity = new SftpIdentity(user.Id, user.UserName!, user.SecurityStamp, shortId);
        if (!_connections.TrySignIn(connection, identity))
        {
            if (!connection.IsClosing)
            {
                await FailAsync(services, user, "too_many_sessions", cancellationToken);
                connection.Close(SshDisconnectReason.TooManyConnections,
                    $"You have {_options.MaxSessionsPerUser} SFTP sessions open already. Close one and try again.");
            }

            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = _clock.GetUtcNow();
        object metadata = secondFactor is null ? new { channel = Channel, root = shortId } : new { channel = Channel, root = shortId, secondFactor };
        services.GetRequiredService<IAuditLog>().Record(AuditActions.Auth.Login, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName), metadata);
        await users.UpdateAsync(user); // also saves the audit entry (same unit of work)
        LogSignedIn(_logger, user.UserName!, connection.CorrelationId);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName!)], "pulsar-sftp"));
    }

    /// <summary>
    /// The user a login names: <c>name</c>, or <c>name.&lt;shortId&gt;</c> for one server's folder as the root when that user
    /// exists and may see that server's files. Anything else is the whole login as a user name (user names may hold dots).
    /// </summary>
    private static async Task<(PulsarUser? User, string? ShortId)> ResolveAsync(IServiceProvider services, UserManager<PulsarUser> users, string login,
        CancellationToken cancellationToken)
    {
        if (login.Length is 0 or > PulsarUser.MaxUserNameLength + 1 + Server.ShortIdLength)
        {
            return (null, null);
        }

        var dot = login.LastIndexOf('.');
        if (dot > 0 && Server.IsShortId(login[(dot + 1)..]) && await users.FindByNameAsync(login[..dot]) is { } owner)
        {
            var shortId = login[(dot + 1)..];
            var server = await services.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking()
                .Where(s => s.ShortId == shortId).Select(s => new { s.Id, s.NodeId }).SingleOrDefaultAsync(cancellationToken);
            if (server is not null && (await services.GetRequiredService<IPermissionEvaluator>().GetAsync(owner.Id, cancellationToken))
                .Has(Permissions.Server.FilesView, AccessTarget.ForServer(server.Id, server.NodeId)))
            {
                return (owner, shortId);
            }
        }

        return (login.Length <= PulsarUser.MaxUserNameLength ? await users.FindByNameAsync(login) : null, null);
    }

    private async Task FailAsync(IServiceProvider services, PulsarUser? known, string reason, CancellationToken cancellationToken)
    {
        if (!_failures.TryRecord(out var leftOut))
        {
            return;
        }

        // The name of an existing user only: what was typed may be a password typed in the wrong field.
        var userName = known?.UserName;
        var entry = services.GetRequiredService<IAuditLog>().Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure,
            leftOut == 0 ? new { userName, reason, channel = Channel } : new { userName, reason, channel = Channel, notRecordedBefore = leftOut });
        if (known is not null)
        {
            entry.WithTarget("user", known.Id.ToString());
        }

        await services.GetRequiredService<PulsarDbContext>().SaveChangesAsync(cancellationToken);
    }

    private AsyncServiceScope Scope(SftpConnection connection)
    {
        var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = connection.Context;
        return scope;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP sign-in of {UserName} ({CorrelationId})")]
    private static partial void LogSignedIn(ILogger logger, string userName, string correlationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP sign-in attempts from {Address} are over the per-address limit")]
    private static partial void LogRateLimited(ILogger logger, System.Net.IPAddress? address);
}
