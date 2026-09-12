using System.Security.Claims;
using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.Shared.Auth;

namespace Lagerkraft.SyncGateway.Realtime;

public static class ChangePermissions
{
    public static bool CanSee(ClaimsPrincipal user, string entity, string? requiredPermission, Guid? warehouseId)
    {
        var assignments = ParseAssignments(user);

        if (!string.IsNullOrWhiteSpace(requiredPermission))
        {
            return assignments.Count > 0 && Permissions.Allows(assignments, requiredPermission, warehouseId);
        }

        if (string.Equals(entity, "Task", StringComparison.OrdinalIgnoreCase))
        {
            if (assignments.Count == 0)
            {
                return true;
            }

            return Permissions.Allows(assignments, Permissions.TasksReadAll, warehouseId)
                   || Permissions.Allows(assignments, Permissions.TasksReadOwn, warehouseId);
        }

        return true;
    }

    private static IReadOnlyList<RoleAssignment> ParseAssignments(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(LagerkraftClaims.RoleAssignments)?.Value
                  ?? user.FindFirst("ra")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        raw = raw.Trim();
        for (var i = 0; i < 2; i++)
        {
            if (raw.StartsWith('"'))
            {
                try
                {
                    raw = JsonSerializer.Deserialize<string>(raw) ?? raw;
                }
                catch (JsonException)
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        var id = new ClaimsIdentity();
        id.AddClaim(new Claim(LagerkraftClaims.RoleAssignments, raw));
        return RoleAssignment.Parse(new ClaimsPrincipal(id));
    }
}