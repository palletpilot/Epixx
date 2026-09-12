using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Sync;

public static class CompatHeaders
{
    public const string MinAppVersion = "Lagerkraft-Min-App-Version";
    public const string LatestAppVersion = "Lagerkraft-Latest-App-Version";
    public const string FeedEpoch = "Lagerkraft-Feed-Epoch";
    public const string TenantState = "Lagerkraft-Tenant-State";
    public const string TenantMaintenance = "Lagerkraft-Tenant-Maintenance";
    public const string AppVersion = "X-Lagerkraft-App-Version";

    public static async Task ApplyCompatAsync(HttpResponse response, IWmsCoreSyncClient wms, IConfiguration config, CancellationToken ct)
    {
        var compat = await wms.GetCompatAsync(ct);
        var min = compat.MinAppVersion
            ?? config["Compat:MinAppVersion"]
            ?? "0.0.1";
        var latest = compat.LatestAppVersion
            ?? config["Compat:LatestAppVersion"]
            ?? "0.0.1";
        response.Headers[MinAppVersion] = min;
        response.Headers[LatestAppVersion] = latest;
    }
}
