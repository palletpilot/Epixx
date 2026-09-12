namespace Lagerkraft.Platform.Provisioning;

public interface IWmsCoreMigrateClient
{
    /// <summary>POST /internal/tenants/{id}/migrate. 200 = ready; throw on 5xx.</summary>
    Task MigrateAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Dev/Testing: ensure a fixed shell warehouse exists in the tenant DB via wms-core.
    /// </summary>
    Task EnsureDevWarehouseAsync(Guid tenantId, Guid warehouseId, string name, CancellationToken ct);
}
