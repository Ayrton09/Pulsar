using Pulsar.Application.Updates;
using Pulsar.Contracts;

namespace Pulsar.Infrastructure.Updates;

/// <summary>
/// The newest release's manifest and signature, from the project's GitHub releases (<see cref="UpdateRelease"/>): nothing
/// secret is sent, redirects are followed only to GitHub's release storage, and both files are bounded. GitHub answers 404
/// while the repository has no release, or is private.
/// </summary>
internal sealed class GitHubUpdateSource(IHttpClientFactory clients) : IUpdateSource
{
    public const string ClientName = "pulsar-updates";

    public async Task<(byte[] Manifest, byte[] Signature)> GetLatestAsync(CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(ClientName);
        var manifest = await UpdateDownloads.GetBytesAsync(client, UpdateRelease.LatestManifest, UpdateRelease.MaxManifestBytes, cancellationToken);
        var signature = await UpdateDownloads.GetBytesAsync(client, UpdateRelease.LatestSignature, UpdateRelease.MaxSignatureBytes, cancellationToken);
        return (manifest, signature);
    }
}
