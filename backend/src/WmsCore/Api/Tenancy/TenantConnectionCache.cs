using System.Collections.Concurrent;

namespace Lagerkraft.WmsCore.Api.Tenancy;

public interface ITenantConnectionCache
{
    Task<string?> GetConnectionStringAsync(Guid tenantId, CancellationToken ct);
    void Invalidate(Guid tenantId);
}

public sealed class TenantConnectionCache(IPlatformTenantClient platform) : ITenantConnectionCache
{
    private readonly ConcurrentDictionary<Guid, string> _cache = new();

    public async Task<string?> GetConnectionStringAsync(Guid tenantId, CancellationToken ct)
    {
        if (_cache.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var connection = await platform.GetConnectionAsync(tenantId, ct);
        if (connection is null)
        {
            return null;
        }

        _cache[tenantId] = connection;
        return connection;
    }

    public void Invalidate(Guid tenantId) => _cache.TryRemove(tenantId, out _);
}
