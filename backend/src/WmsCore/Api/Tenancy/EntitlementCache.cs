namespace Lagerkraft.WmsCore.Api.Tenancy;

public sealed record TenantEntitlement(
    Guid TenantId,
    string LifecycleState,
    bool Maintenance,
    string PlanCode,
    int? HardCapUnits);

public interface IEntitlementCache
{
    Task<TenantEntitlement?> GetAsync(Guid tenantId, CancellationToken ct);
    void Invalidate(Guid tenantId);
}

public sealed class EntitlementCache(IPlatformTenantClient platform) : IEntitlementCache
{
    private readonly Dictionary<Guid, TenantEntitlement> _cache = new();
    private readonly object _gate = new();

    public async Task<TenantEntitlement?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(tenantId, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var value = await platform.GetEntitlementAsync(tenantId, ct);
            if (value is not null)
            {
                lock (_gate)
                {
                    _cache[tenantId] = value;
                }
            }

            return value;
        }
        catch
        {
            lock (_gate)
            {
                return _cache.GetValueOrDefault(tenantId);
            }
        }
    }

    public void Invalidate(Guid tenantId)
    {
        lock (_gate)
        {
            _cache.Remove(tenantId);
        }
    }
}
