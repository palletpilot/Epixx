using System.Security.Claims;
using System.Text.Json;

namespace Lagerkraft.Shared.Auth;

public sealed record RoleAssignment(string Role, IReadOnlyList<Guid> WarehouseIds)
{
    public bool AllWarehouses => WarehouseIds.Count == 0;

    public static IReadOnlyList<RoleAssignment> Parse(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(LagerkraftClaims.RoleAssignments)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<RoleAssignment>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var role = item.GetProperty("r").GetString();
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var warehouses = ParseWarehouses(item);
            list.Add(new RoleAssignment(role, warehouses));
        }

        return list;
    }

    private static IReadOnlyList<Guid> ParseWarehouses(JsonElement item)
    {
        if (!item.TryGetProperty("w", out var w))
        {
            return [];
        }

        if (w.ValueKind == JsonValueKind.String && w.GetString() == "*")
        {
            return [];
        }

        if (w.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var value in w.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
