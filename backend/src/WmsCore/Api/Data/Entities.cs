using Lagerkraft.Shared;

namespace Lagerkraft.WmsCore.Api.Data;

public sealed class TenantMeta
{
    public int Id { get; set; } = 1;
    public Guid FeedEpoch { get; set; } = Ids.New();
    public string SchemaVersion { get; set; } = "";
}

public sealed class ChangeLogRow
{
    public long Seq { get; set; }
    public string Entity { get; set; } = "";
    public Guid Id { get; set; }
    public string Op { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public Guid? CommandId { get; set; }
    public Guid? Actor { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class OutboxRow
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class ProcessedCommand
{
    public Guid CommandId { get; set; }
    public string Result { get; set; } = "{}";
    public DateTimeOffset AppliedAt { get; set; }
}

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class Deviation
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid? CommandId { get; set; }
    public string Detail { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}
