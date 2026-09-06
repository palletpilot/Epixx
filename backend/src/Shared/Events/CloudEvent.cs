namespace Lagerkraft.Shared.Events;

public sealed record CloudEvent
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Type { get; init; }
    public string SpecVersion { get; init; } = "1.0";
    public DateTimeOffset Time { get; init; }
    public string DataContentType { get; init; } = "application/json";
    public object? Data { get; init; }
}
