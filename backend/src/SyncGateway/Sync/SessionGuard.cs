using Lagerkraft.SyncGateway.Auth;
using Lagerkraft.SyncGateway.Clients;

namespace Lagerkraft.SyncGateway.Sync;

public static class SessionGuard
{
    public static async Task<bool> IsStaleAsync(
        SyncPrincipal principal,
        IPlatformSyncClient platform,
        CancellationToken ct)
    {
        var current = await platform.GetSessionVersionAsync(principal.TenantId, principal.UserId, ct);
        if (current is null)
        {
            return false;
        }

        return principal.SessionVersion < current.Value;
    }
}
