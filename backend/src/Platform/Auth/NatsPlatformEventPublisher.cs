using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Events;
using NATS.Client.Core;

namespace Lagerkraft.Platform.Auth;

public interface IPlatformEventPublisher
{
    Task PublishAsync(Guid tenantId, string module, string eventName, object data, CancellationToken ct);
}

public sealed class NatsPlatformEventPublisher(INatsConnection nats, IClock clock) : IPlatformEventPublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(Guid tenantId, string module, string eventName, object data, CancellationToken ct)
    {
        var cloud = new CloudEvent
        {
            Id = Ids.New().ToString(),
            Source = "lagerkraft/platform",
            Type = $"{module}.{eventName}",
            Time = clock.UtcNow,
            Data = data
        };
        var subject = $"lagerkraft.{tenantId}.{module}.{eventName}";
        await nats.PublishAsync(subject, JsonSerializer.SerializeToUtf8Bytes(cloud, Json), cancellationToken: ct);
    }
}

public sealed class NoopPlatformEventPublisher : IPlatformEventPublisher
{
    public Task PublishAsync(Guid tenantId, string module, string eventName, object data, CancellationToken ct) =>
        Task.CompletedTask;
}
