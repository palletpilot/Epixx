namespace Lagerkraft.Platform.Provisioning;

/// <summary>Used when Services:WmsCore is unset (unit/local without wms-core).</summary>
public sealed class NoopWmsCoreMigrateClient : IWmsCoreMigrateClient
{
    public Task MigrateAsync(Guid tenantId, CancellationToken ct) => Task.CompletedTask;

    public Task EnsureDevWarehouseAsync(Guid tenantId, Guid warehouseId, string name, CancellationToken ct) =>
        Task.CompletedTask;
}
