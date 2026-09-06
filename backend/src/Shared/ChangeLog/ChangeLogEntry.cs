namespace Lagerkraft.Shared.ChangeLog;

public sealed class ChangeLogEntry
{
    public long Seq { get; init; }
    public string Entity { get; init; } = "";
    public Guid Id { get; init; }
    public string Op { get; init; } = "";
    public string Payload { get; init; } = "";
    public Guid? CommandId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public Guid? Actor { get; init; }
}
