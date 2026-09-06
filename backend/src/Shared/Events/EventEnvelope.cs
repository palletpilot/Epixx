namespace Lagerkraft.Shared.Events;

public sealed record EventEnvelope(Guid TenantId, string Module, string EventName, CloudEvent CloudEvent);
