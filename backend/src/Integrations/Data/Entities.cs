namespace Lagerkraft.Integrations.Data;

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class WebhookEndpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string[] Events { get; set; } = [];
    public bool Active { get; set; } = true;
}

public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid WebhookEndpointId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ImportJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
