namespace Lagerkraft.SyncGateway.Realtime;

public interface ITenantEventBus
{
    Task<IAsyncDisposable> SubscribeAsync(
        Guid tenantId,
        Func<TenantBusMessage, CancellationToken, Task> handler,
        CancellationToken ct);
}

public sealed record TenantBusMessage(
    string Subject,
    string Payload,
    long? Seq = null,
    string? Entity = null,
    string? RequiredPermission = null,
    DateTimeOffset? RecordedAt = null,
    bool IsReconnectHint = false);
