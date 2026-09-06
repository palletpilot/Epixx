namespace Lagerkraft.Shared;

public static class Permissions
{
    public const int MapVersion = 1;

    public const string TenantAdmin = "tenant_admin";
    public const string WarehouseManager = "warehouse_manager";
    public const string FloorWorker = "floor_worker";
    public const string Viewer = "viewer";

    public const string TenantSettingsManage = "tenant.settings.manage";
    public const string TenantBillingManage = "tenant.billing.manage";
    public const string TenantAuditRead = "tenant.audit.read";
    public const string UsersRead = "users.read";
    public const string UsersInvite = "users.invite";
    public const string UsersManage = "users.manage";
    public const string SsoManage = "sso.manage";
    public const string DevicesEnroll = "devices.enroll";
    public const string DevicesManage = "devices.manage";
    public const string WarehousesCreate = "warehouses.create";
    public const string WarehousesManage = "warehouses.manage";
    public const string LayoutRead = "layout.read";
    public const string LayoutMap = "layout.map";
    public const string LayoutManage = "layout.manage";
    public const string CatalogRead = "catalog.read";
    public const string CatalogWrite = "catalog.write";
    public const string CatalogImport = "catalog.import";
    public const string AttributesManage = "attributes.manage";
    public const string InventoryRead = "inventory.read";
    public const string InventoryMove = "inventory.move";
    public const string InventoryCount = "inventory.count";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InboundManage = "inbound.manage";
    public const string InboundReceive = "inbound.receive";
    public const string InboundPutaway = "inbound.putaway";
    public const string OrdersManage = "orders.manage";
    public const string OutboundPick = "outbound.pick";
    public const string OutboundPack = "outbound.pack";
    public const string OutboundShip = "outbound.ship";
    public const string TasksReadOwn = "tasks.read_own";
    public const string TasksReadAll = "tasks.read_all";
    public const string TasksExecute = "tasks.execute";
    public const string TasksAssign = "tasks.assign";
    public const string DeviationsReport = "deviations.report";
    public const string DeviationsResolve = "deviations.resolve";
    public const string PrintingPrint = "printing.print";
    public const string PrintingManage = "printing.manage";
    public const string IntegrationsManage = "integrations.manage";
    public const string ReportsRead = "reports.read";
    public const string ExportReports = "export.reports";
    public const string ExportTenantData = "export.tenant_data";

    private static readonly Dictionary<string, IReadOnlySet<string>> RoleMap = new()
    {
        [TenantAdmin] = new HashSet<string>
        {
            TenantSettingsManage, TenantBillingManage, TenantAuditRead,
            UsersRead, UsersInvite, UsersManage, SsoManage,
            DevicesEnroll, DevicesManage,
            WarehousesCreate, WarehousesManage,
            LayoutRead, LayoutMap, LayoutManage,
            CatalogRead, CatalogWrite, CatalogImport, AttributesManage,
            InventoryRead, InventoryMove, InventoryCount, InventoryAdjust,
            InboundManage, InboundReceive, InboundPutaway,
            OrdersManage, OutboundPick, OutboundPack, OutboundShip,
            TasksReadOwn, TasksReadAll, TasksExecute, TasksAssign,
            DeviationsReport, DeviationsResolve,
            PrintingPrint, PrintingManage,
            IntegrationsManage,
            ReportsRead, ExportReports, ExportTenantData
        },
        [WarehouseManager] = new HashSet<string>
        {
            TenantAuditRead,
            UsersRead, UsersInvite, UsersManage,
            DevicesEnroll, DevicesManage,
            WarehousesManage,
            LayoutRead, LayoutMap, LayoutManage,
            CatalogRead, CatalogWrite, CatalogImport,
            InventoryRead, InventoryMove, InventoryCount, InventoryAdjust,
            InboundManage, InboundReceive, InboundPutaway,
            OrdersManage, OutboundPick, OutboundPack, OutboundShip,
            TasksReadOwn, TasksReadAll, TasksExecute, TasksAssign,
            DeviationsReport, DeviationsResolve,
            PrintingPrint, PrintingManage,
            ReportsRead, ExportReports
        },
        [FloorWorker] = new HashSet<string>
        {
            LayoutRead, LayoutMap,
            CatalogRead,
            InventoryRead, InventoryMove, InventoryCount,
            InboundReceive, InboundPutaway,
            OutboundPick, OutboundPack, OutboundShip,
            TasksReadOwn, TasksExecute,
            DeviationsReport,
            PrintingPrint
        },
        [Viewer] = new HashSet<string>
        {
            LayoutRead,
            CatalogRead,
            InventoryRead,
            TasksReadAll,
            ReportsRead, ExportReports
        }
    };

    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    public static IReadOnlySet<string> ForRole(string role) =>
        RoleMap.TryGetValue(role, out var set) ? set : None;

    public static bool Allows(IEnumerable<Auth.RoleAssignment> assignments, string permission, Guid? warehouseId)
    {
        foreach (var assignment in assignments)
        {
            if (!ForRole(assignment.Role).Contains(permission))
            {
                continue;
            }

            if (warehouseId is null
                || assignment.AllWarehouses
                || assignment.WarehouseIds.Contains(warehouseId.Value))
            {
                return true;
            }
        }

        return false;
    }
}
