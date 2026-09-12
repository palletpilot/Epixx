using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Sync;

public static class TenantHold
{
    /// <summary>
    /// Whole-batch operate holds at the gateway. Restricted still allows operate (no 402 on sync).
    /// </summary>
    public static IResult? Evaluate(TenantEntitlement? entitlement)
    {
        if (entitlement is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "entitlement_unavailable");
        }

        if (entitlement.Maintenance)
        {
            return Results.Json(
                new { error = "maintenance" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return entitlement.LifecycleState switch
        {
            "Suspended" or "TrialExpired" => Results.Json(
                new { error = "tenant_locked", state = entitlement.LifecycleState },
                statusCode: StatusCodes.Status423Locked),
            "Deleting" or "Deleted" => Results.StatusCode(StatusCodes.Status410Gone),
            _ => null
        };
    }

    public static void ApplyStateHeaders(HttpResponse response, TenantEntitlement entitlement)
    {
        response.Headers[CompatHeaders.TenantState] = entitlement.LifecycleState;
        if (entitlement.Maintenance)
        {
            response.Headers[CompatHeaders.TenantMaintenance] = "true";
        }
    }
}
