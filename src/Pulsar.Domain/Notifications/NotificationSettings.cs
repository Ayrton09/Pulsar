using Pulsar.Domain.Common;

namespace Pulsar.Domain.Notifications;

public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (port 587); refused if the server does not offer it.</summary>
    StartTls,

    /// <summary>TLS from the first byte (port 465).</summary>
    Tls,

    /// <summary>No encryption: only for a mail relay on the same machine or network.</summary>
    None,
}

/// <summary>
/// The panel's notification settings (one row): its address for links in messages, the SMTP server email channels use
/// (password encrypted by the application), and the thresholds of the node events.
/// </summary>
public sealed class NotificationSettings
{
    public const int SingletonId = 1;
    public const int MaxUrlLength = 255;
    public const int MaxHostLength = 255;
    public const int MaxUserNameLength = 255;
    public const int MaxFromNameLength = 64;
    public const int DefaultDiskLowPercent = 10;
    public const int DefaultNodeOfflineMinutes = 3;

    private NotificationSettings()
    {
        // For EF Core materialization.
    }

    public int Id { get; private set; }

    /// <summary>How users reach the panel (e.g. https://panel.example.com:8443), for links in messages; none without it.</summary>
    public string? PanelUrl { get; private set; }

    /// <summary>Null: email is not set up.</summary>
    public string? SmtpHost { get; private set; }

    public int SmtpPort { get; private set; }

    public SmtpSecurity SmtpSecurity { get; private set; }

    /// <summary>Null: the server takes mail without signing in.</summary>
    public string? SmtpUserName { get; private set; }

    public byte[]? SmtpPasswordProtected { get; private set; }

    public string? FromAddress { get; private set; }

    public string? FromName { get; private set; }

    /// <summary>A drive with less free space than this share of its size is low.</summary>
    public int DiskLowPercent { get; private set; }

    /// <summary>A node is offline (and notified) after this long without a connection.</summary>
    public int NodeOfflineMinutes { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool EmailConfigured => SmtpHost is not null && FromAddress is not null;

    public TimeSpan NodeOfflineAfter => TimeSpan.FromMinutes(NodeOfflineMinutes);

    public static NotificationSettings Default(DateTimeOffset now) => new()
    {
        Id = SingletonId,
        SmtpPort = 587,
        SmtpSecurity = SmtpSecurity.StartTls,
        DiskLowPercent = DefaultDiskLowPercent,
        NodeOfflineMinutes = DefaultNodeOfflineMinutes,
        UpdatedAt = now,
    };

    public Result ChangeGeneral(string? panelUrl, int diskLowPercent, int nodeOfflineMinutes, DateTimeOffset now)
    {
        var url = Clean(panelUrl)?.TrimEnd('/');
        if (url is not null && (url.Length > MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
        {
            return NotificationErrors.PanelUrlInvalid;
        }

        if (diskLowPercent is < 1 or > 50 || nodeOfflineMinutes is < 1 or > 1440)
        {
            return NotificationErrors.ThresholdsInvalid;
        }

        PanelUrl = url;
        DiskLowPercent = diskLowPercent;
        NodeOfflineMinutes = nodeOfflineMinutes;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Sets the SMTP server; a null <paramref name="host"/> turns email off. A null <paramref name="passwordProtected"/>
    /// keeps the current password unless <paramref name="clearPassword"/>.
    /// </summary>
    public Result ChangeSmtp(string? host, int port, SmtpSecurity security, string? userName, byte[]? passwordProtected, bool clearPassword,
        string? fromAddress, string? fromName, DateTimeOffset now)
    {
        var smtpHost = Clean(host);
        if (smtpHost is null)
        {
            SmtpHost = null;
            SmtpUserName = null;
            SmtpPasswordProtected = null;
            FromAddress = null;
            FromName = null;
            UpdatedAt = now;
            return Result.Success();
        }

        if (smtpHost.Length > MaxHostLength || Uri.CheckHostName(smtpHost) == UriHostNameType.Unknown)
        {
            return NotificationErrors.SmtpHostInvalid;
        }

        if (port is < 1 or > 65535 || !Enum.IsDefined(security))
        {
            return NotificationErrors.SmtpPortInvalid;
        }

        var user = Clean(userName);
        if (user is { Length: > MaxUserNameLength } || (user?.Any(char.IsControl) ?? false))
        {
            return NotificationErrors.SmtpUserInvalid;
        }

        var from = Clean(fromAddress);
        if (!NotificationChannel.IsEmailAddress(from))
        {
            return NotificationErrors.FromInvalid;
        }

        var name = Clean(fromName);
        if (name is { Length: > MaxFromNameLength } || (name?.Any(char.IsControl) ?? false))
        {
            return NotificationErrors.FromInvalid;
        }

        SmtpHost = smtpHost;
        SmtpPort = port;
        SmtpSecurity = security;
        SmtpUserName = user;
        if (user is null || clearPassword)
        {
            SmtpPasswordProtected = null;
        }
        else if (passwordProtected is not null)
        {
            SmtpPasswordProtected = passwordProtected;
        }

        FromAddress = from;
        FromName = name;
        UpdatedAt = now;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
