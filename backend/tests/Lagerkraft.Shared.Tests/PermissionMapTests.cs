using Lagerkraft.Shared;

namespace Lagerkraft.Shared.Tests;

public sealed class PermissionMapTests
{
    // Copied from the spec Permission model table (A/M/W/V), inverted per role.
    // If this and Permissions.ForRole diverge, the product is authorizing the wrong thing.
    public static TheoryData<string, string[]> Roles => new()
    {
        { "tenant_admin", TenantAdmin },
        { "warehouse_manager", WarehouseManager },
        { "floor_worker", FloorWorker },
        { "viewer", Viewer }
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public void ForRole_SpecMatrix_Matches(string role, string[] expected)
    {
        Permissions.ForRole(role).OrderBy(p => p).ShouldBe(expected.OrderBy(p => p));
    }

    private static readonly string[] TenantAdmin =
    [
        "tenant.settings.manage", "tenant.billing.manage", "tenant.audit.read",
        "users.read", "users.invite", "users.manage", "sso.manage",
        "devices.enroll", "devices.manage",
        "warehouses.create", "warehouses.manage",
        "layout.read", "layout.map", "layout.manage",
        "catalog.read", "catalog.write", "catalog.import", "attributes.manage",
        "inventory.read", "inventory.move", "inventory.count", "inventory.adjust",
        "inbound.manage", "inbound.receive", "inbound.putaway",
        "orders.manage", "outbound.pick", "outbound.pack", "outbound.ship",
        "tasks.read_own", "tasks.read_all", "tasks.execute", "tasks.assign",
        "deviations.report", "deviations.resolve",
        "printing.print", "printing.manage",
        "integrations.manage",
        "reports.read", "export.reports", "export.tenant_data"
    ];

    private static readonly string[] WarehouseManager =
    [
        "tenant.audit.read",
        "users.read", "users.invite", "users.manage",
        "devices.enroll", "devices.manage",
        "warehouses.manage",
        "layout.read", "layout.map", "layout.manage",
        "catalog.read", "catalog.write", "catalog.import",
        "inventory.read", "inventory.move", "inventory.count", "inventory.adjust",
        "inbound.manage", "inbound.receive", "inbound.putaway",
        "orders.manage", "outbound.pick", "outbound.pack", "outbound.ship",
        "tasks.read_own", "tasks.read_all", "tasks.execute", "tasks.assign",
        "deviations.report", "deviations.resolve",
        "printing.print", "printing.manage",
        "reports.read", "export.reports"
    ];

    private static readonly string[] FloorWorker =
    [
        "layout.read", "layout.map",
        "catalog.read",
        "inventory.read", "inventory.move", "inventory.count",
        "inbound.receive", "inbound.putaway",
        "outbound.pick", "outbound.pack", "outbound.ship",
        "tasks.read_own", "tasks.execute",
        "deviations.report",
        "printing.print"
    ];

    private static readonly string[] Viewer =
    [
        "layout.read",
        "catalog.read",
        "inventory.read",
        "tasks.read_all",
        "reports.read", "export.reports"
    ];
}
