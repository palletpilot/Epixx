using Lagerkraft.WmsCore.Api.Data;

namespace Lagerkraft.WmsCore.Api.Relay;

public sealed class RecordingOutboxPublisher : IOutboxPublisher
{
    public List<(Guid TenantId, OutboxRow Message)> Published { get; } = new();

    public Task PublishAsync(Guid tenantId, OutboxRow message, CancellationToken ct)
    {
        Published.Add((tenantId, message));
        return Task.CompletedTask;
    }
}
