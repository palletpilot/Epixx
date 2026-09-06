namespace Lagerkraft.Shared.Outbox;

public interface IOutbox
{
    Task Add(OutboxMessage message, CancellationToken cancellationToken);
}
