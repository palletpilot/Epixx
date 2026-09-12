using System.Collections.Concurrent;

namespace Lagerkraft.SyncGateway.Realtime;

public sealed class InMemoryTenantEventBus : ITenantEventBus
{
    private readonly ConcurrentDictionary<Guid, List<Func<TenantBusMessage, CancellationToken, Task>>> _handlers = new();

    public Task<IAsyncDisposable> SubscribeAsync(
        Guid tenantId,
        Func<TenantBusMessage, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var list = _handlers.GetOrAdd(tenantId, _ => []);
        lock (list)
        {
            list.Add(handler);
        }

        return Task.FromResult<IAsyncDisposable>(new Subscription(this, tenantId, handler));
    }

    public async Task PublishAsync(Guid tenantId, TenantBusMessage message, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(tenantId, out var list))
        {
            return;
        }

        Func<TenantBusMessage, CancellationToken, Task>[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }

        foreach (var handler in snapshot)
        {
            await handler(message, ct);
        }
    }

    private sealed class Subscription(
        InMemoryTenantEventBus bus,
        Guid tenantId,
        Func<TenantBusMessage, CancellationToken, Task> handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (bus._handlers.TryGetValue(tenantId, out var list))
            {
                lock (list)
                {
                    list.Remove(handler);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
