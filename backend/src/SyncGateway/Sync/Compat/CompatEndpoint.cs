using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Sync.Compat;

public static class CompatEndpoint
{
    public static RouteGroupBuilder MapSyncCompat(this RouteGroupBuilder sync)
    {
        sync.MapGet("/compat", GetCompat);
        return sync;
    }

    private static async Task<IResult> GetCompat(
        IWmsCoreSyncClient wms,
        IConfiguration config,
        HttpContext http,
        CancellationToken ct)
    {
        await CompatHeaders.ApplyCompatAsync(http.Response, wms, config, ct);
        var compat = await wms.GetCompatAsync(ct);
        return Results.Ok(new
        {
            min_command_versions = compat.MinCommandVersions,
            latest_app_version = compat.LatestAppVersion,
            min_app_version = compat.MinAppVersion
                ?? config["Compat:MinAppVersion"]
                ?? "0.0.1"
        });
    }
}
