namespace Lagerkraft.Platform.Provisioning;

/// <summary>Temporary until C1 migrate stub is callable; same contract as HTTP client.</summary>
public sealed class NoopWmsCoreMigrateClient : IWmsCoreMigrateClient
{
    public Task MigrateAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;
}
