namespace Lagerkraft.Platform.Provisioning;

/// <summary>Stub until G2 UpCloud Managed Postgres spike.</summary>
public sealed class UpCloudApiCreator : ITenantDatabaseCreator
{
    public Task CreateAsync(Guid tenantId, string databaseName, CancellationToken ct) =>
        throw new NotImplementedException("UpCloudApiCreator waits on Task G2 spike.");
}
