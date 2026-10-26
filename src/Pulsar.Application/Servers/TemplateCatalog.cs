using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Application.Servers;

public sealed class TemplateOptions
{
    /// <summary>Directory with the bundled manifests (<c>games/&lt;key&gt;/manifest.json</c>), copied next to the panel.</summary>
    public string BundledDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "games");

    /// <summary>
    /// Offers Pulsar's test game (<c>games/_test</c>): development and CI only. It runs on agents started with the test
    /// game server, which production agents never are.
    /// </summary>
    public bool EnableTestGame { get; set; }
}

/// <summary>Registers the manifests shipped with the panel (at startup, after the database is ready).</summary>
public interface IBundledTemplates
{
    Task RegisterBundledAsync(CancellationToken cancellationToken);
}

public sealed record TemplateView(Guid Id, string Key, string Version, string Name, string? Description, bool IsTestGame, TemplateSource Source);

public sealed record TemplateOption(string Value, string Label);

/// <summary>A manifest variable as forms need it (docs/architecture/04, "Variables").</summary>
public sealed record TemplateVariable(
    string Key,
    string Label,
    VariableType Type,
    string? Default,
    bool Required,
    bool Secret,
    bool UserEditable,
    string? Pattern,
    int? MaxLength,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<TemplateOption> Options,
    string? Description,
    TemplateWorkshop? Workshop = null);

/// <summary>The variable lists Steam Workshop items of <paramref name="AppId"/>, separated by <paramref name="Separator"/>.</summary>
public sealed record TemplateWorkshop(long AppId, string Separator);

/// <param name="Follows">The port this one comes with (its port number plus <paramref name="Offset"/>): not chosen on its own.</param>
public sealed record TemplatePort(string Name, string Label, IReadOnlyList<PortProtocol> Protocols, int Default, bool Required, string? Description,
    string? Follows = null, int? Offset = null);

/// <summary>Everything a user needs to create a server from a template, and what importing it would allow.</summary>
public sealed record TemplateDetails(
    TemplateView Template,
    int? MinDiskGb,
    int? MinMemoryMb,
    IReadOnlyList<TemplateVariable> Variables,
    IReadOnlyList<TemplatePort> Ports,
    bool AllowExtraArguments,
    bool CanUpdate,
    TemplateCapabilities Capabilities);

/// <summary>What a manifest makes every node do (docs/architecture/04, "Trust model"): shown before importing it.</summary>
public sealed record TemplateCapabilities(
    string Executable,
    IReadOnlyList<int> SteamAppIds,
    IReadOnlyList<string> ConfigFiles,
    ManifestConsoleMode ConsoleMode,
    IReadOnlyList<string>? AddonSources = null);

/// <summary>A manifest about to be imported: its errors, or what it is and how it differs from the latest version.</summary>
public sealed record TemplateImportPreview(
    bool Valid,
    IReadOnlyList<ManifestError> Errors,
    TemplateDetails? Details,
    bool AlreadyImported,
    string? PreviousVersion,
    IReadOnlyList<string> ChangedSections);

/// <summary>Game templates (docs/architecture/04): the bundled ones, registered at startup, and imported ones.</summary>
public sealed partial class TemplateCatalog(IPulsarDbContext db, TemplateOptions options, IAuditLog audit, TimeProvider clock,
    ILogger<TemplateCatalog> logger) : IBundledTemplates
{
    public async Task<IReadOnlyList<TemplateView>> ListAsync(CancellationToken cancellationToken)
    {
        var templates = await db.GameTemplates.AsNoTracking().OrderBy(t => t.Name).ThenBy(t => t.Version).ToListAsync(cancellationToken);
        return [.. templates.Where(IsOffered).Select(View)];
    }

    public async Task<Result<TemplateDetails>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await FindOfferedAsync(id, cancellationToken);
        return template is null ? ServerErrors.TemplateNotFound : Details(template, ServerSpecs.Parse(template));
    }

    /// <summary>Checks a manifest without importing it: the administrator reviews what it would allow first.</summary>
    public async Task<TemplateImportPreview> PreviewAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(content));
        if (!result.IsValid)
        {
            return new TemplateImportPreview(false, result.Errors, null, false, null, []);
        }

        var manifest = result.Manifest!;
        var versions = await db.GameTemplates.AsNoTracking().Where(t => t.Key == manifest.Key).ToListAsync(cancellationToken);
        var exists = versions.Any(t => t.Version == manifest.Version);
        var previous = versions.Where(t => t.Version != manifest.Version).MaxBy(t => t.CreatedAt);
        var preview = GameTemplate.Create(manifest.Key, manifest.Version, manifest.SchemaVersion, manifest.Name, manifest.Description,
            TemplateSource.Imported, content, result.Sha256, clock.GetUtcNow());
        return new TemplateImportPreview(true, [], Details(preview, manifest), exists, previous?.Version,
            previous is null ? [] : ChangedSections(previous.Content, content));
    }

    /// <summary>Imports a manifest (<c>template.manage</c>): a new key, or a new version of one; versions never change.</summary>
    public async Task<Result<TemplateDetails>> ImportAsync(string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var result = ManifestParser.Parse(bytes);
        if (!result.IsValid)
        {
            return ServerErrors.InvalidManifest(string.Join("; ", result.Errors));
        }

        var manifest = result.Manifest!;
        if (manifest.Key == ManifestValidator.TestGameKey)
        {
            return ServerErrors.InvalidManifest($"'{ManifestValidator.TestGameKey}' is reserved for Pulsar's test game.");
        }

        if (await db.GameTemplates.AnyAsync(t => t.Key == manifest.Key && t.Version == manifest.Version, cancellationToken))
        {
            return ServerErrors.TemplateVersionExists;
        }

        var template = GameTemplate.Create(manifest.Key, manifest.Version, manifest.SchemaVersion, manifest.Name, manifest.Description,
            TemplateSource.Imported, content, result.Sha256, clock.GetUtcNow());
        db.GameTemplates.Add(template);
        audit.Record(AuditActions.Template.Imported, metadata: new
        {
            manifest.Key,
            manifest.Version,
            sha256 = Convert.ToHexStringLower(result.Sha256),
            executable = manifest.Runtime.GetValueOrDefault(ManifestValidator.WindowsX64)?.Executable,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ServerErrors.TemplateVersionExists;
        }

        LogImported(manifest.Key, manifest.Version);
        return Details(template, manifest);
    }

    /// <summary>A template servers can be created from (the test game only while it is enabled).</summary>
    public async Task<GameTemplate?> FindOfferedAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await db.GameTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == new Domain.Common.TemplateId(id), cancellationToken);
        return template is not null && IsOffered(template) ? template : null;
    }

    /// <summary>
    /// Registers every valid bundled manifest that is not registered yet. A registered key + version is immutable: if the
    /// file changed without a new version, the stored one is kept and the mismatch is logged.
    /// </summary>
    public async Task RegisterBundledAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.BundledDirectory))
        {
            LogNoBundledDirectory(options.BundledDirectory);
            return;
        }

        var existing = await db.GameTemplates.ToListAsync(cancellationToken);
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(options.BundledDirectory, "manifest.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            var result = ManifestParser.Parse(bytes);
            if (!result.IsValid)
            {
                LogInvalidManifest(file, string.Join("; ", result.Errors));
                continue;
            }

            var manifest = result.Manifest!;
            if (manifest.Key == ManifestValidator.TestGameKey && !options.EnableTestGame)
            {
                continue;
            }

            var stored = existing.FirstOrDefault(t => t.Key == manifest.Key && t.Version == manifest.Version);
            if (stored is not null)
            {
                if (!stored.ContentSha256.AsSpan().SequenceEqual(result.Sha256))
                {
                    LogChangedWithoutVersion(manifest.Key, manifest.Version);
                }

                continue;
            }

            db.GameTemplates.Add(GameTemplate.Create(manifest.Key, manifest.Version, manifest.SchemaVersion, manifest.Name, manifest.Description,
                TemplateSource.Bundled, Encoding.UTF8.GetString(bytes), result.Sha256, clock.GetUtcNow()));
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            LogRegistered(added);
        }
    }

    private bool IsOffered(GameTemplate template) => Offers(template.Key);

    /// <summary>Whether this panel offers the game <paramref name="key"/> (the test game only where it is enabled).</summary>
    public bool Offers(string key) => key != ManifestValidator.TestGameKey || options.EnableTestGame;

    internal static TemplateView View(GameTemplate t) =>
        new(t.Id.Value, t.Key, t.Version, t.Name, t.Description, t.Key == ManifestValidator.TestGameKey, t.Source);

    internal static TemplateDetails Details(GameTemplate template, GameManifest manifest)
    {
        var runtime = manifest.Runtime[ManifestValidator.WindowsX64];
        var steps = (manifest.Install?.Steps ?? []).Concat(manifest.Update?.Steps ?? []);
        return new TemplateDetails(View(template), manifest.Requirements?.MinDiskGb, manifest.Requirements?.MinMemoryMb,
            [.. manifest.VariableList.Select(Variable)],
            [.. manifest.PortList.Select(p => new TemplatePort(p.Name, p.Label, p.Protocols, p.Default, p.Required, p.Description, p.Follows?.Port, p.Follows?.Offset))],
            runtime.AllowExtraArguments, manifest.Update is not null,
            new TemplateCapabilities(runtime.Executable, [.. steps.OfType<SteamCmdStep>().Select(s => s.AppId).Distinct()],
                [.. manifest.ConfigFileList.Select(f => f.Path)], runtime.Console.Mode,
                [.. manifest.AddonList.Select(a => a.Source is GitHubReleaseSource github ? $"github.com/{github.Repo}" : a.Key).Distinct()]));
    }

    internal static TemplateVariable Variable(VariableDefinition v) => new(v.Key, v.Label, v.Type,
        v.Default is { } value ? VariableValues.FromJson(value) : null, v.Required, v.Secret, v.UserEditable, v.Pattern, v.MaxLength, v.Min, v.Max,
        [.. (v.Options ?? []).Select(o => new TemplateOption(o.Value, o.Label))], v.Description,
        v.Workshop is { } workshop ? new TemplateWorkshop(workshop.AppId, workshop.Separator) : null);

    /// <summary>Top-level manifest sections whose content differs (what an administrator should look at first).</summary>
    private static IReadOnlyList<string> ChangedSections(string before, string after)
    {
        var old = System.Text.Json.Nodes.JsonNode.Parse(before)!.AsObject();
        var now = System.Text.Json.Nodes.JsonNode.Parse(after)!.AsObject();
        return [.. old.Select(p => p.Key).Union(now.Select(p => p.Key)).Where(k => k is not ("version" or "$schema"))
            .Where(k => !System.Text.Json.Nodes.JsonNode.DeepEquals(old[k], now[k])).Order(StringComparer.Ordinal)];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No bundled game manifests found in {Directory}")]
    private partial void LogNoBundledDirectory(string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bundled manifest {File} is invalid and was skipped: {Errors}")]
    private partial void LogInvalidManifest(string file, string errors);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Bundled manifest {Key} {Version} differs from the registered one; templates are immutable, so bump its version")]
    private partial void LogChangedWithoutVersion(string key, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Game template {Key} {Version} imported")]
    private partial void LogImported(string key, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Count} bundled game templates")]
    private partial void LogRegistered(int count);
}
