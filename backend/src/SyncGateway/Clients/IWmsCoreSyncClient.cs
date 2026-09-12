using System.Net;
using System.Text.Json;

namespace Lagerkraft.SyncGateway.Clients;

public interface IWmsCoreSyncClient
{
    Task<WmsCommandResponse> PostCommandsAsync(CommandBatchPayload batch, CancellationToken ct);
    Task<WmsChangesResponse> GetChangesAsync(Guid tenantId, Guid? warehouse, long? since, CancellationToken ct);
    Task<JsonElement> GetSnapshotAsync(Guid tenantId, Guid? warehouse, string? entity, int? page, CancellationToken ct);
    Task<CompatInfo> GetCompatAsync(CancellationToken ct);
}

public sealed record CommandBatchPayload(
    Guid TenantId,
    DateTimeOffset Now,
    IReadOnlyList<CommandEnvelopePayload> Commands);

public sealed record CommandEnvelopePayload(
    Guid Id,
    string Type,
    int V,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    Guid DeviceId,
    Guid UserId);

public sealed record WmsCommandResponse(HttpStatusCode StatusCode, JsonElement Body);

public sealed record WmsChangesResponse(
    HttpStatusCode StatusCode,
    Guid? FeedEpoch,
    JsonElement? Body);

public sealed record CompatInfo(
    IReadOnlyDictionary<string, int> MinCommandVersions,
    string LatestAppVersion,
    string? MinAppVersion);
