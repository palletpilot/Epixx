using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;

namespace Lagerkraft.WmsCore.Api.Layout;

public static class SystemLocations
{
    public static Location New(Guid warehouseId, string code) => new()
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
}
