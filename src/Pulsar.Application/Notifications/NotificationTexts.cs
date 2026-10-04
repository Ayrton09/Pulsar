using System.Globalization;
using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// The messages, in the channel's language (English or Spanish, as the panel's UI). Built when the event happens, so a
/// delivery retried later says what was true then.
/// </summary>
public static class NotificationTexts
{
    public static NotificationMessage ServerCrashed(string language, ServerFacts server, int? exitCode, bool crashLoop, string? link)
    {
        var es = IsSpanish(language);
        var code = exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
        return crashLoop
            ? new NotificationMessage(
                es ? $"{server.Name} se cae una y otra vez" : $"{server.Name} keeps crashing",
                es ? "Se cayó varias veces seguidas y Pulsar dejó de reiniciarlo: quedó detenido. Revisá su consola."
                    : "It crashed several times in a row and Pulsar stopped restarting it: it is stopped. Check its console.",
                NotificationSeverity.Error, ServerFields(es, server, (es ? "Código de salida" : "Exit code", code)), link)
            : new NotificationMessage(
                es ? $"{server.Name} se cayó" : $"{server.Name} crashed",
                es ? "El servidor se cerró sin que nadie lo pidiera. Si tiene el reinicio automático activado, Pulsar lo vuelve a iniciar."
                    : "The server exited without being asked to. If it restarts automatically, Pulsar starts it again.",
                NotificationSeverity.Error, ServerFields(es, server, (es ? "Código de salida" : "Exit code", code)), link);
    }

    public static NotificationMessage ServerStartFailed(string language, ServerFacts server, bool notReady, string? detail, string? link)
    {
        var es = IsSpanish(language);
        return notReady
            ? new NotificationMessage(
                es ? $"{server.Name} no quedó listo" : $"{server.Name} is not ready",
                es ? "Arrancó, pero no avisó que estaba listo a tiempo: puede estar trabado. Revisá su consola."
                    : "It started but did not report ready in time: it may be stuck. Check its console.",
                NotificationSeverity.Warning, ServerFields(es, server), link)
            : new NotificationMessage(
                es ? $"{server.Name} no arrancó" : $"{server.Name} did not start",
                es ? "Windows no pudo iniciarlo." : "Windows could not start it.",
                NotificationSeverity.Error, ServerFields(es, server, ("Error", detail)), link);
    }

    public static NotificationMessage BackupFailed(string language, ServerFacts server, string backupName, string? error, bool remoteCopy, string? link)
    {
        var es = IsSpanish(language);
        return new NotificationMessage(
            remoteCopy
                ? es ? $"Falló la copia remota de un backup de {server.Name}" : $"Remote copy of a backup of {server.Name} failed"
                : es ? $"Falló un backup de {server.Name}" : $"A backup of {server.Name} failed",
            remoteCopy
                ? es ? "El backup quedó en el nodo, pero no se pudo subir al almacenamiento remoto." : "The backup is on the node, but it could not be uploaded to remote storage."
                : es ? "El backup no se completó." : "The backup did not complete.",
            NotificationSeverity.Error, ServerFields(es, server, ("Backup", backupName), ("Error", error)), link);
    }

    public static NotificationMessage ScheduledTaskFailed(string language, ServerFacts server, string taskName, string? error, string? link)
    {
        var es = IsSpanish(language);
        return new NotificationMessage(
            es ? $"Falló la tarea programada “{taskName}” de {server.Name}" : $"Scheduled task “{taskName}” of {server.Name} failed",
            es ? "La tarea no pudo hacer su acción." : "The task could not do its action.",
            NotificationSeverity.Error, ServerFields(es, server, (es ? "Tarea" : "Task", taskName), ("Error", error)), link);
    }

    public static NotificationMessage NodeOffline(string language, string nodeName, DateTimeOffset since, string? link)
    {
        var es = IsSpanish(language);
        var at = since.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        return new NotificationMessage(
            es ? $"El nodo {nodeName} está desconectado" : $"Node {nodeName} is offline",
            es ? "Sus servidores siguen funcionando, pero el panel no puede controlarlos hasta que vuelva a conectarse."
                : "Its game servers keep running, but the panel cannot control them until it connects again.",
            NotificationSeverity.Warning, [new(es ? "Nodo" : "Node", nodeName), new(es ? "Visto por última vez" : "Last seen", at)], link);
    }

    public static NotificationMessage NodeOnline(string language, string nodeName, string? link)
    {
        var es = IsSpanish(language);
        return new NotificationMessage(
            es ? $"El nodo {nodeName} volvió a conectarse" : $"Node {nodeName} is back online",
            es ? "El panel vuelve a controlar sus servidores." : "The panel controls its game servers again.",
            NotificationSeverity.Recovered, [new(es ? "Nodo" : "Node", nodeName)], link);
    }

    public static NotificationMessage NodeDiskLow(string language, string nodeName, IReadOnlyList<(string Root, long Free, long Total)> drives, string? link)
    {
        var es = IsSpanish(language);
        var fields = new List<NotificationField> { new(es ? "Nodo" : "Node", nodeName) };
        foreach (var (root, free, total) in drives)
        {
            var percent = total > 0 ? (double)free / total * 100 : 0;
            fields.Add(new(root, es
                ? $"{Size(free, language)} libres de {Size(total, language)} ({percent.ToString("0.#", Culture(language))} %)"
                : $"{Size(free, language)} free of {Size(total, language)} ({percent.ToString("0.#", Culture(language))} %)"));
        }

        return new NotificationMessage(
            es ? $"Al nodo {nodeName} le queda poco espacio en disco" : $"Node {nodeName} is running out of disk space",
            es ? "Con el disco lleno fallan las actualizaciones, los backups y los mismos servidores. Liberá espacio o borrá backups viejos."
                : "A full drive breaks updates, backups and the game servers themselves. Free some space or delete old backups.",
            NotificationSeverity.Warning, fields, link);
    }

    public static NotificationMessage Test(string language, string? link)
    {
        var es = IsSpanish(language);
        return new NotificationMessage(
            es ? "Notificación de prueba de Pulsar" : "Test notification from Pulsar",
            es ? "Si leés esto, el canal funciona." : "If you can read this, the channel works.",
            NotificationSeverity.Recovered, [], link);
    }

    /// <summary>1 decimal, binary units, as the UI shows sizes, with the language's decimal separator.</summary>
    public static string Size(long bytes, string language = "en")
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.#", Culture(language))} {units[unit]}";
    }

    private static bool IsSpanish(string language) => language == "es";

    private static CultureInfo Culture(string language) => IsSpanish(language) ? CultureInfo.GetCultureInfo("es") : CultureInfo.InvariantCulture;

    private static NotificationField[] ServerFields(bool es, ServerFacts server, params (string Name, string? Value)[] extra) =>
    [
        new(es ? "Servidor" : "Server", server.Name),
        new(es ? "Juego" : "Game", server.Game),
        new(es ? "Nodo" : "Node", server.NodeName),
        .. extra.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => new NotificationField(f.Name, Clip(f.Value!))),
    ];

    private static string Clip(string value) => value.Length > 300 ? value[..300] + "…" : value;
}

/// <summary>What messages say about a server.</summary>
public sealed record ServerFacts(Guid Id, string Name, string Game, string NodeName);
