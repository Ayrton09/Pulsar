using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pulsar.Application.Abstractions;
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

public sealed record TemplateView(Guid Id, string Key, string Version, string Name, string? Description, bool IsTestGame);

/// <summary>Game templates (docs/architecture/04). Phase 3: the bundled ones, registered at startup.</summary>
public sealed partial class TemplateCatalog(IPulsarDbContext db, TemplateOptions options, TimeProvider clock, ILogger<TemplateCatalog> logger)
    : IBundledTemplates
{
    public async Task<IReadOnlyList<TemplateView>> ListAsync(CancellationToken cancellationToken)
    {
        var templates = await db.GameTemplates.AsNoTracking().OrderBy(t => t.Name).ThenBy(t => t.Version).ToListAsync(cancellationToken);
        return [.. templates.Where(IsOffered).Select(View)];
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

    private bool IsOffered(GameTemplate template) => template.Key != ManifestValidator.TestGameKey || options.EnableTestGame;

    private static TemplateView View(GameTemplate t) =>
        new(t.Id.Value, t.Key, t.Version, t.Name, t.Description, t.Key == ManifestValidator.TestGameKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No bundled game manifests found in {Directory}")]
    private partial void LogNoBundledDirectory(string directory);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bundled manifest {File} is invalid and was skipped: {Errors}")]
    private partial void LogInvalidManifest(string file, string errors);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Bundled manifest {Key} {Version} differs from the registered one; templates are immutable, so bump its version")]
    private partial void LogChangedWithoutVersion(string key, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Count} bundled game templates")]
    private partial void LogRegistered(int count);
}
