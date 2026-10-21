using System.Text.Json;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Layout.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Layout;

public static class SystemLocationEnsure
{
    public static async Task<int> RunAsync(TenantDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var warehouses = await db.Warehouses.AsNoTracking().Select(w => w.Id).ToListAsync(ct);
        if (warehouses.Count == 0)
        {
            return 0;
        }

        var existing = await db.Locations
            .Where(l => l.IsSystem && warehouses.Contains(l.WarehouseId))
            .Select(l => new { l.WarehouseId, l.Code })
            .ToListAsync(ct);
        var have = existing
            .Select(x => (x.WarehouseId, x.Code))
            .ToHashSet();

        var added = 0;
        foreach (var warehouseId in warehouses)
        {
            foreach (var code in LayoutDefaults.SystemLocationCodes)
            {
                if (have.Contains((warehouseId, code)))
                {
                    continue;
                }

                var location = new Location
                {
                    Id = Ids.New(),
                    WarehouseId = warehouseId,
                    Type = "staging",
                    Code = code,
                    Path = code,
                    Barcode = code,
                    Status = "active",
                    IsSystem = true
                };
                db.Locations.Add(location);
                db.ChangeLog.Add(new ChangeLogRow
                {
                    Entity = "location",
                    Id = location.Id,
                    Op = "upsert",
                    Payload = JsonSerializer.Serialize(new
                    {
                        id = location.Id,
                        warehouse_id = location.WarehouseId,
                        parent_id = location.ParentId,
                        type = location.Type,
                        code = location.Code,
                        path = location.Path,
                        height_mm = location.HeightMm,
                        width_mm = location.WidthMm,
                        depth_mm = location.DepthMm,
                        max_weight_g = location.MaxWeightG,
                        barcode = location.Barcode,
                        status = location.Status,
                        is_system = location.IsSystem
                    }, LayoutJson.Options),
                    CommandId = null,
                    Actor = null,
                    WarehouseId = warehouseId,
                    OccurredAt = now,
                    RecordedAt = now
                });
                added++;
            }
        }

        return added;
    }
}

file static class LayoutJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}
