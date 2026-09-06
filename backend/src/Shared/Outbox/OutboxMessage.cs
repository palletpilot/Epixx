namespace Lagerkraft.Shared.Outbox;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public string Type { get; init; } = "";
    public string Payload { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
