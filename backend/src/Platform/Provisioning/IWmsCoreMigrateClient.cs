namespace Lagerkraft.Platform.Provisioning;

public interface IWmsCoreMigrateClient
{
    /// <summary>POST /internal/tenants/{id}/migrate. 200 = ready; throw on 5xx.</summary>
    Task MigrateAsync(Guid tenantId, CancellationToken ct);
}
