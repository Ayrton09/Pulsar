using Pulsar.Application.Updates;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Infrastructure;

/// <summary>Tests never ask GitHub for releases (the daily look runs in long-lived test hosts too): every look fails.</summary>
public sealed class OfflineUpdateSource : IUpdateSource
{
    public Task<(byte[] Manifest, byte[] Signature)> GetLatestAsync(CancellationToken cancellationToken) =>
        throw new UpdateDownloadException("The tests never ask GitHub.");
}
