using System.Text.Json;

namespace Lagerkraft.SyncGateway.Realtime;

public sealed record ChangeEvent(
    long Seq,
    string Entity,
    Guid Id,
    string Op,
    JsonElement Payload,
    Guid? CommandId,
    string? Actor,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? RequiredPermission = null);
