using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
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
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Sftp;

/// <summary>
/// The SFTP sign-in (docs/guides/sftp.md): the panel user name and password, then, for a user who must use two-factor
/// authentication (their own choice or a role's), the authenticator code, through the same checks as the browser's
/// sign-in: Identity's lockout (each password and code check counts, not each connection, and one at a time per account:
/// <see cref="SignInLocks"/>), disabled users refused, the
/// per-address limit of <c>RateLimiting:Auth</c> (counted apart from the web's), and the audit of failed and successful
/// sign-ins with the client's address. Keyboard-interactive asks one thing per round ("Password: ", then
/// "Verification code: "); the <c>password</c> method never signs in a two-factor user. Unknown users get the same
/// prompts and the same cost (one password hash). Recovery codes are not accepted over SFTP: they are for getting back
/// into the panel, where a new authenticator is set up.
/// <para>
/// Or an SSH key the user added on the account page (<c>publickey</c>; "SSH keys" in the guide): the key alone signs in,
/// for a two-factor user too (adding it took the password and the code). The library checks the signature; this finds
/// the key among the user's and applies the password's account checks (locked out, disabled, a role's two-factor setup
/// missing), under the same account lock and per-address limit. Wrong keys are not wrong passwords: they never count
/// towards the lockout, and a key's sign-in leaves the failed count alone. A question whether a key would do (no
/// signature) is answered yes only for a key the user named has added, like OpenSSH does, whatever the state of the
/// account: it says nothing about a user name to whoever does not hold one of its public keys, and costs one database
/// query whether the user exists or not.
/// </para>
/// </summary>
internal sealed partial class SftpAuthenticator : IDisposable
{
    public const string PasswordPrompt = "Password: ";
    public const string CodePrompt = "Verification code: ";
    public const string Channel = "sftp";

    // Verified when the user name does not exist, so both paths cost one password hash (no user enumeration by timing).
    private static readonly string s_dummyPasswordHash =
        new PasswordHasher<PulsarUser>().HashPassword(new PulsarUser(), "pulsar-sftp-timing-equalizer");

    // The answer to a key question that is yes: the library sends "this key would do" and signs nobody in.
    private static readonly ClaimsPrincipal s_keyAccepted = new(new ClaimsIdentity());

    private readonly IServiceScopeFactory _scopes;
    private readonly SftpConnections _connections;
    private readonly FailedLoginBudget _failures;
    private readonly SignInLocks _locks;
    private readonly SftpOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SftpAuthenticator> _logger;
    private readonly PartitionedRateLimiter<string> _perAddress;
    private readonly PartitionedRateLimiter<string> _keyQuestions;

    public SftpAuthenticator(IServiceScopeFactory scopes, SftpConnections connections, FailedLoginBudget failures, SignInLocks locks,
        IOptions<SftpOptions> options, IConfiguration configuration, TimeProvider clock, ILogger<SftpAuthenticator> logger)
    {
        _scopes = scopes;
        _connections = connections;
        _failures = failures;
        _locks = locks;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        var limits = configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        _perAddress = PartitionedRateLimiter.Create<string, string>(partition => RateLimitPartition.GetFixedWindowLimiter(partition,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.PermitLimit, Window = limits.Window, QueueLimit = 0 }));

        // Questions about keys cost a query each: bounded per address too, by as many as the sign-ins' keys could ask.
        var questions = limits.PermitLimit * _options.MaxKeyAttemptsPerConnection;
        _keyQuestions = PartitionedRateLimiter.Create<string, string>(partition => RateLimitPartition.GetFixedWindowLimiter(partition,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = questions, Window = limits.Window, QueueLimit = 0 }));
    }

    private enum Outcome
    {
        Failed,
        NeedsCode,
        SignedIn,
    }

    public void Dispose()
    {
        _perAddress.Dispose();
        _keyQuestions.Dispose();
    }

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
            case SshAuthenticationType.ClientPublicKeyQuery:
                connection.Interactive = null;
                e.AuthenticationTask = KeyQuestionAsync(connection, e.Username ?? string.Empty, e.PublicKey, e.Cancellation);
                break;
            case SshAuthenticationType.ClientPublicKey:
                // The library has checked the signature with the presented key; whose key it is comes next.
                connection.Interactive = null;
                e.AuthenticationTask = KeyAsync(connection, e.Username ?? string.Empty, e.PublicKey, e.Cancellation);
                break;
            default:
                // "none" and host-based: never.
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
        if (!TryTakeTurn(connection, _perAddress))
        {
            return (Outcome.Failed, null, null);
        }

        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var signIn = services.GetRequiredService<SignInManager<PulsarUser>>();
        var (user, shortId) = await ResolveAsync(services, users, login, cancellationToken);

        // One check at a time per account, as in the browser (SignInLocks): names of no user wait the same.
        using var turn = await _locks.AcquireAsync(user?.NormalizedUserName ?? users.NormalizeName(login), cancellationToken);
        if (user is null)
        {
            users.PasswordHasher.VerifyHashedPassword(new PulsarUser(), s_dummyPasswordHash, password);
            await FailAsync(services, null, "invalid_credentials");
            return (Outcome.Failed, null, null);
        }

        await services.GetRequiredService<PulsarDbContext>().Entry(user).ReloadAsync(cancellationToken); // as it is now, after the wait

        var lockedOut = await users.IsLockedOutAsync(user);
        if (lockedOut || !await signIn.CanSignInAsync(user))
        {
            // Locked-out and disabled users get the answer a wrong password gets, after as long.
            users.PasswordHasher.VerifyHashedPassword(user, s_dummyPasswordHash, password);
            await FailAsync(services, user, lockedOut ? "locked_out" : "not_allowed");
            return (Outcome.Failed, null, null);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await SignInLocks.CountFailureAsync(users, services.GetRequiredService<PulsarDbContext>(), user);
            await FailAsync(services, user, await users.IsLockedOutAsync(user) ? "locked_out" : "invalid_credentials");
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
            await FailAsync(services, user, "two_factor_required");
            return (Outcome.Failed, null, null);
        }

        if ((await services.GetRequiredService<IPermissionEvaluator>().GetAsync(user.Id, cancellationToken)).RequiresTwoFactor)
        {
            // A role requires two-factor authentication the user has not set up: that happens in the browser first.
            await FailAsync(services, user, "two_factor_setup_required");
            return (Outcome.Failed, null, null);
        }

        var principal = await SignInAsync(connection, services, users, user, shortId, secondFactor: null, key: null);
        return (principal is null ? Outcome.Failed : Outcome.SignedIn, principal, null);
    }

    private async Task<ClaimsPrincipal?> CheckCodeAsync(SftpConnection connection, UserId userId, string? shortId, string answer,
        CancellationToken cancellationToken)
    {
        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var db = services.GetRequiredService<PulsarDbContext>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        using var turn = await _locks.AcquireAsync(user.NormalizedUserName!, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
        if (!user.TwoFactorEnabled || !await services.GetRequiredService<SignInManager<PulsarUser>>().CanSignInAsync(user))
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
                await SignInLocks.CountFailureAsync(users, db, user);
                reason = "invalid_code";
            }
        }

        if (reason is not null)
        {
            // A refused save of the code's replay guard (another request saved the user meanwhile) is not the entry's.
            await SignInLocks.DiscardUnsavedAsync(db);
            var audit = services.GetRequiredService<IAuditLog>();
            audit.Record(AuditActions.Auth.TwoFactorFailed, AuditOutcome.Failure, AuditActor.ForUser(user.Id, user.UserName),
                    new { method = "authenticator", reason, channel = Channel })
                .WithTarget("user", user.Id.ToString());
            await db.SaveChangesAsync(CancellationToken.None);
            return null;
        }

        return await SignInAsync(connection, services, users, user, shortId, secondFactor: "authenticator", key: null);
    }

    /// <summary>
    /// Whether a key would do (<c>publickey</c> without a signature): yes only when the user the login names has added it.
    /// Questions past <see cref="SftpOptions.MaxKeyAttemptsPerConnection"/> are answered no without a query.
    /// </summary>
    private async Task<ClaimsPrincipal?> KeyQuestionAsync(SftpConnection connection, string login, IKeyPair? presented,
        CancellationToken cancellationToken)
    {
        if (++connection.KeyQuestions > _options.MaxKeyAttemptsPerConnection || Presented(presented) is not { } key
            || !TryTakeTurn(connection, _keyQuestions))
        {
            return null;
        }

        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var candidates = await FindKeyAsync(services, services.GetRequiredService<UserManager<PulsarUser>>(), login, key, cancellationToken);
        return candidates.Any(c => c.Key is not null) ? s_keyAccepted : null;
    }

    /// <summary>A signed <c>publickey</c> attempt: the library verified the signature with <paramref name="presented"/>.</summary>
    private async Task<ClaimsPrincipal?> KeyAsync(SftpConnection connection, string login, IKeyPair? presented, CancellationToken cancellationToken)
    {
        if (++connection.KeyChecks > _options.MaxKeyAttemptsPerConnection)
        {
            connection.Close(SshDisconnectReason.NoMoreAuthMethodsAvailable, "Too many sign-in attempts.");
            return null;
        }

        // The per-address limit of the passwords: a key attempt takes a turn too (never a place in the lockout).
        if (!TryTakeTurn(connection, _perAddress))
        {
            return null;
        }

        await using var scope = Scope(connection);
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var key = Presented(presented);
        var candidates = key is null ? new List<KeyCandidate>() : await FindKeyAsync(services, users, login, key, cancellationToken);
        var (match, shortId) = await ChooseAsync(services, users, login, candidates, cancellationToken);
        var fingerprint = key?.Fingerprint ?? "unknown";

        // One check at a time per account, as for passwords: names of no user wait the same.
        using var turn = await _locks.AcquireAsync(match?.NormalizedUserName ?? users.NormalizeName(login), cancellationToken);
        if (match?.Key is not { } added)
        {
            // An unknown user and a key the user has not added get the same answer after the same work (the one query);
            // the entry names an existing user, from that query.
            var named = candidates.FirstOrDefault();
            await FailAsync(services, named is null ? null : (named.UserId, named.UserName), "invalid_key", fingerprint);
            return null;
        }

        var user = await users.FindByIdAsync(match.UserId.ToString());
        if (user is null)
        {
            await FailAsync(services, (PulsarUser?)null, "invalid_key", fingerprint);
            return null;
        }

        var lockedOut = await users.IsLockedOutAsync(user);
        if (lockedOut || !await services.GetRequiredService<SignInManager<PulsarUser>>().CanSignInAsync(user))
        {
            // Locked out (by wrong passwords) or disabled: as a wrong key; nothing counts towards the lockout.
            await FailAsync(services, user, lockedOut ? "locked_out" : "not_allowed", fingerprint);
            return null;
        }

        // The key is the second factor of a two-factor user; a role's two-factor setup still has to be done in the browser.
        if (!user.TwoFactorEnabled && (await services.GetRequiredService<IPermissionEvaluator>().GetAsync(user.Id, cancellationToken)).RequiresTwoFactor)
        {
            await FailAsync(services, user, "two_factor_setup_required", fingerprint);
            return null;
        }

        // Deleted while this attempt waited for its turn: the deletion wins.
        if (!await services.GetRequiredService<SshKeys>().ExistsAsync(user.Id, added.Id, cancellationToken))
        {
            await FailAsync(services, user, "invalid_key", fingerprint);
            return null;
        }

        return await SignInAsync(connection, services, users, user, shortId, secondFactor: null, key: added);
    }

    /// <summary>
    /// The users a login may name (the whole login; the part before its last dot when a short id follows) and whether
    /// each has added <paramref name="key"/>: one query, whoever exists.
    /// </summary>
    private static async Task<List<KeyCandidate>> FindKeyAsync(IServiceProvider services, UserManager<PulsarUser> users, string login,
        SshPublicKey key, CancellationToken cancellationToken)
    {
        List<string> names = [];
        if (login.Length is > 0 and <= PulsarUser.MaxUserNameLength)
        {
            names.Add(users.NormalizeName(login));
        }

        var dot = login.LastIndexOf('.');
        if (dot > 0 && dot <= PulsarUser.MaxUserNameLength && Server.IsShortId(login[(dot + 1)..]))
        {
            names.Add(users.NormalizeName(login[..dot]));
        }

        var db = services.GetRequiredService<PulsarDbContext>();
        var fingerprint = key.Fingerprint;
        var rows = await (
                from u in db.Users.AsNoTracking()
                where names.Contains(u.NormalizedUserName!)
                join k in db.SshKeys.AsNoTracking().Where(k => k.Fingerprint == fingerprint) on u.Id equals k.UserId into keys
                from k in keys.DefaultIfEmpty()
                select new { u.Id, u.UserName, u.NormalizedUserName, Key = k })
            .ToListAsync(cancellationToken);

        // The same blob, byte for byte (the fingerprint found it).
        return [.. rows.Select(r => new KeyCandidate(r.Id, r.UserName, r.NormalizedUserName!,
            r.Key is { } k && k.PublicKey.AsSpan().SequenceEqual(key.Blob) ? k : null))];
    }

    /// <summary>
    /// The user whose key it is and the root they asked for, as <see cref="ResolveAsync"/> reads a login: <c>name.shortId</c>
    /// when that user added the key and may see that server's files, else the whole login.
    /// </summary>
    private static async Task<(KeyCandidate? Match, string? ShortId)> ChooseAsync(IServiceProvider services, UserManager<PulsarUser> users,
        string login, List<KeyCandidate> candidates, CancellationToken cancellationToken)
    {
        var dot = login.LastIndexOf('.');
        if (dot > 0 && Server.IsShortId(login[(dot + 1)..])
            && candidates.FirstOrDefault(c => c.Key is not null && c.NormalizedUserName == users.NormalizeName(login[..dot])) is { } owner)
        {
            var shortId = login[(dot + 1)..];
            var server = await services.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking()
                .Where(s => s.ShortId == shortId).Select(s => new { s.Id, s.NodeId }).SingleOrDefaultAsync(cancellationToken);
            if (server is not null && (await services.GetRequiredService<IPermissionEvaluator>().GetAsync(owner.UserId, cancellationToken))
                .Has(Permissions.Server.FilesView, AccessTarget.ForServer(server.Id, server.NodeId)))
            {
                return (owner, shortId);
            }
        }

        var whole = login.Length <= PulsarUser.MaxUserNameLength ? users.NormalizeName(login) : null;
        return (candidates.FirstOrDefault(c => c.Key is not null && c.NormalizedUserName == whole), null);
    }

    /// <summary>
    /// The key a client presented, as the library read it, through the same strict reading as an added key (RSA keys
    /// under 2048 bits, among others, are refused here); null when it is none the panel takes.
    /// </summary>
    private static SshPublicKey? Presented(IKeyPair? presented)
    {
        if (presented is null)
        {
            return null;
        }

        try
        {
            // The key's own type ("ssh-rsa" whatever SHA-2 signature algorithm the client named).
            var parsed = SshPublicKey.FromBlob(presented.GetPublicKeyBytes(presented.KeyAlgorithmName).Span);
            return parsed.IsSuccess ? parsed.Value : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The credentials were right: the session counts for its user (or the user has too many), and it is audited.</summary>
    private async Task<ClaimsPrincipal?> SignInAsync(SftpConnection connection, IServiceProvider services, UserManager<PulsarUser> users, PulsarUser user,
        string? shortId, string? secondFactor, SshKey? key)
    {
        var identity = new SftpIdentity(user.Id, user.UserName!, user.SecurityStamp, shortId, key?.Id);
        if (!_connections.TrySignIn(connection, identity))
        {
            if (!connection.IsClosing)
            {
                await FailAsync(services, user, "too_many_sessions", key?.Fingerprint);
                connection.Close(SshDisconnectReason.TooManyConnections,
                    $"You have {_options.MaxSessionsPerUser} SFTP sessions open already. Close one and try again.");
            }

            return null;
        }

        // The user row first: the audit entry is saved on its own, whatever happened to the row. A key's sign-in leaves the
        // failed count alone: it says nothing about whoever may be guessing the password.
        var db = services.GetRequiredService<PulsarDbContext>();
        var now = _clock.GetUtcNow();
        await SignInLocks.UpdateAsync(users, db, user, u =>
        {
            if (key is null)
            {
                u.AccessFailedCount = 0;
            }

            u.LastLoginAt = now;
        });
        await SignInLocks.DiscardUnsavedAsync(db);
        if (key is not null)
        {
            await services.GetRequiredService<SshKeys>().RecordUseAsync(key.Id, CancellationToken.None);
        }

        object metadata = (key, secondFactor) switch
        {
            ({ } k, _) => new { channel = Channel, root = shortId, method = "publickey", key = k.Id.Value, keyName = k.Name, fingerprint = k.Fingerprint },
            (null, { } second) => new { channel = Channel, root = shortId, method = "password", secondFactor = second },
            _ => new { channel = Channel, root = shortId, method = "password" },
        };
        services.GetRequiredService<IAuditLog>().Record(AuditActions.Auth.Login, AuditOutcome.Success, AuditActor.ForUser(user.Id, user.UserName), metadata);
        await db.SaveChangesAsync(CancellationToken.None);
        LogSignedIn(_logger, user.UserName!, key is null ? "password" : "publickey", connection.CorrelationId);
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

    /// <summary>Takes a turn of <paramref name="limiter"/> for the client's address; none left closes the connection.</summary>
    private bool TryTakeTurn(SftpConnection connection, PartitionedRateLimiter<string> limiter)
    {
        // As the web's sign-in endpoints do per address (a refused attempt checks nothing and counts nowhere).
        using var lease = limiter.AttemptAcquire(ClientAddress.PartitionOf(connection.Address));
        if (lease.IsAcquired)
        {
            return true;
        }

        LogRateLimited(_logger, connection.Address);
        connection.Close(SshDisconnectReason.TooManyConnections, "Too many sign-in attempts. Try again in a minute.");
        return false;
    }

    private Task FailAsync(IServiceProvider services, PulsarUser? known, string reason, string? keyFingerprint = null) =>
        FailAsync(services, known is null ? null : (known.Id, known.UserName), reason, keyFingerprint);

    private async Task FailAsync(IServiceProvider services, (UserId Id, string? UserName)? known, string reason, string? keyFingerprint)
    {
        if (!_failures.TryRecord(out var leftOut))
        {
            return;
        }

        // A refused update of the user (another request saved it meanwhile) is not the entry's.
        var db = services.GetRequiredService<PulsarDbContext>();
        await SignInLocks.DiscardUnsavedAsync(db);

        // The name of an existing user only: what was typed may be a password typed in the wrong field.
        var metadata = new Dictionary<string, object?> { ["userName"] = known?.UserName, ["reason"] = reason, ["channel"] = Channel };
        if (keyFingerprint is not null)
        {
            metadata["method"] = "publickey";
            metadata["fingerprint"] = keyFingerprint;
        }

        if (leftOut != 0)
        {
            metadata["notRecordedBefore"] = leftOut;
        }

        var entry = services.GetRequiredService<IAuditLog>().Record(AuditActions.Auth.LoginFailed, AuditOutcome.Failure, metadata);
        if (known is { } user)
        {
            entry.WithTarget("user", user.Id.ToString());
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private AsyncServiceScope Scope(SftpConnection connection)
    {
        var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = connection.Context;
        return scope;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SFTP sign-in of {UserName} by {Method} ({CorrelationId})")]
    private static partial void LogSignedIn(ILogger logger, string userName, string method, string correlationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SFTP sign-in attempts from {Address} are over the per-address limit")]
    private static partial void LogRateLimited(ILogger logger, System.Net.IPAddress? address);

    /// <summary>A user a login may name, and their key with the presented fingerprint (null: not added).</summary>
    private sealed record KeyCandidate(UserId UserId, string? UserName, string NormalizedUserName, SshKey? Key);
}
