using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Lagerkraft.SyncGateway.Realtime;

public sealed class ConnectionRegistry
{
    private static readonly Meter Meter = new("Lagerkraft.SyncGateway");
    private static readonly Counter<long> Connections = Meter.CreateCounter<long>("syncgateway_sse_connections");
    private static readonly Histogram<double> DeliveryLagMs =
        Meter.CreateHistogram<double>("syncgateway_sse_delivery_lag_ms");

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, SseConnection>> _byTenant = new();

    public SseConnection Register(Guid tenantId, Guid userId, int sessionVersion, Guid? warehouseId)
    {
        var conn = new SseConnection(Guid.CreateVersion7(), tenantId, userId, sessionVersion, warehouseId);
        var map = _byTenant.GetOrAdd(tenantId, _ => new ConcurrentDictionary<Guid, SseConnection>());
        map[conn.Id] = conn;
        Connections.Add(1, new KeyValuePair<string, object?>("tenant", tenantId.ToString("D")));
        return conn;
    }

    public void Unregister(SseConnection conn)
    {
        if (_byTenant.TryGetValue(conn.TenantId, out var map))
        {
            map.TryRemove(conn.Id, out _);
        }

        Connections.Add(-1, new KeyValuePair<string, object?>("tenant", conn.TenantId.ToString("D")));
    }

    public IReadOnlyList<SseConnection> ForTenant(Guid tenantId) =>
        _byTenant.TryGetValue(tenantId, out var map) ? map.Values.ToList() : [];

    public void CloseForUser(Guid tenantId, Guid userId, int? minSessionVersion = null)
    {
        foreach (var conn in ForTenant(tenantId).Where(c => c.UserId == userId))
        {
            if (minSessionVersion is { } min && conn.SessionVersion >= min)
            {
                continue;
            }

            conn.RequestClose("membership_changed");
        }
    }

    public static void ObserveDeliveryLag(DateTimeOffset recordedAt)
    {
        var lag = (DateTimeOffset.UtcNow - recordedAt).TotalMilliseconds;
        if (lag >= 0)
        {
            DeliveryLagMs.Record(lag);
        }
    }
}

public sealed class SseConnection(
    Guid id,
    Guid tenantId,
    Guid userId,
    int sessionVersion,
    Guid? warehouseId)
{
    private readonly CancellationTokenSource _close = new();

    public Guid Id { get; } = id;
    public Guid TenantId { get; } = tenantId;
    public Guid UserId { get; } = userId;
    public int SessionVersion { get; } = sessionVersion;
    public Guid? WarehouseId { get; } = warehouseId;
    public CancellationToken CloseToken => _close.Token;
    public string? CloseReason { get; private set; }

    public void RequestClose(string reason)
    {
        CloseReason = reason;
        _close.Cancel();
    }
}
