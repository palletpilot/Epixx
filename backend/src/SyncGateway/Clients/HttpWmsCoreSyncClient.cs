using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Lagerkraft.SyncGateway.Clients;

public sealed class HttpWmsCoreSyncClient(HttpClient http) : IWmsCoreSyncClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<WmsCommandResponse> PostCommandsAsync(CommandBatchPayload batch, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("internal/commands", batch, Json, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return new WmsCommandResponse(response.StatusCode, doc.RootElement.Clone());
    }

    public async Task<WmsChangesResponse> GetChangesAsync(Guid tenantId, Guid? warehouse, long? since, CancellationToken ct)
    {
        var qs = $"tenantId={tenantId}";
        if (warehouse is { } wh)
        {
            qs += $"&warehouse={wh}";
        }

        if (since is { } s)
        {
            qs += $"&since={s}";
        }

        using var response = await http.GetAsync($"internal/changes?{qs}", ct);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new WmsChangesResponse(HttpStatusCode.Gone, null, null);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        Guid? epoch = null;
        if (doc.RootElement.TryGetProperty("feed_epoch", out var ep) && ep.ValueKind == JsonValueKind.String
            && Guid.TryParse(ep.GetString(), out var g))
        {
            epoch = g;
        }

        return new WmsChangesResponse(response.StatusCode, epoch, doc.RootElement.Clone());
    }

    public async Task<JsonElement> GetSnapshotAsync(
        Guid tenantId, Guid? warehouse, string? entity, int? page, CancellationToken ct)
    {
        var qs = $"tenantId={tenantId}";
        if (warehouse is { } wh)
        {
            qs += $"&warehouse={wh}";
        }

        if (!string.IsNullOrWhiteSpace(entity))
        {
            qs += $"&entity={Uri.EscapeDataString(entity)}";
        }

        if (page is { } p)
        {
            qs += $"&page={p}";
        }

        using var response = await http.GetAsync($"internal/snapshot?{qs}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task<CompatInfo> GetCompatAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("internal/compat", ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CompatDto>(Json, ct)
            ?? new CompatDto(new Dictionary<string, int>(), "0.0.1", null);
        return new CompatInfo(dto.MinCommandVersions, dto.LatestAppVersion, dto.MinAppVersion);
    }

    private sealed record CompatDto(
        Dictionary<string, int> MinCommandVersions,
        string LatestAppVersion,
        string? MinAppVersion);
}
