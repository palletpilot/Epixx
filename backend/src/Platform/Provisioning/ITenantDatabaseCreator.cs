namespace Lagerkraft.Platform.Provisioning;

public interface ITenantDatabaseCreator
{
    Task CreateAsync(Guid tenantId, string databaseName, CancellationToken ct);
}
