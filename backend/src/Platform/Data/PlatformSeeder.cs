using Lagerkraft.Shared;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Data;

public static class PlatformSeeder
{
    // Placeholder öre from the spec's example structure (Start 1,990 SEK / Pro 4,990 / Business 9,990).
    public const long StartMonthlyOre = 199_000;
    public const long ProMonthlyOre = 499_000;
    public const long BusinessMonthlyOre = 999_000;

    public const string StartFeatures =
        """{"sso":false,"webhooks":false,"integrations":false,"multi_warehouse":false,"priority_support":false}""";

    public const string ProFeatures =
        """{"sso":true,"webhooks":true,"integrations":true,"multi_warehouse":false,"priority_support":false}""";

    public const string BusinessFeatures =
        """{"sso":true,"webhooks":true,"integrations":true,"multi_warehouse":true,"priority_support":true}""";

    public static async Task SeedAsync(PlatformDbContext db, CancellationToken ct = default)
    {
        await EnsureRole(db, Permissions.TenantAdmin, "Administratör", "Tenant admin", ct);
        await EnsureRole(db, Permissions.WarehouseManager, "Lagerchef", "Warehouse manager", ct);
        await EnsureRole(db, Permissions.FloorWorker, "Lagerarbetare", "Floor worker", ct);
        await EnsureRole(db, Permissions.Viewer, "Granskare", "Viewer", ct);

        await EnsurePlan(db, "start", "Start", StartMonthlyOre, 500, 400, 500, StartFeatures, isPublic: true, ct);
        await EnsurePlan(db, "pro", "Pro", ProMonthlyOre, 2500, 300, 5000, ProFeatures, isPublic: true, ct);
        await EnsurePlan(db, "business", "Business", BusinessMonthlyOre, 10_000, 200, null, BusinessFeatures, isPublic: false, ct);

        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureRole(
        PlatformDbContext db,
        string internalName,
        string displayNameSv,
        string displayNameEn,
        CancellationToken ct)
    {
        if (await db.Roles.Where(r => r.TenantId == null && r.InternalName == internalName).AnyAsync(ct))
        {
            return;
        }

        db.Roles.Add(new Role
        {
            Id = Ids.New(),
            TenantId = null,
            InternalName = internalName,
            DisplayNameSv = displayNameSv,
            DisplayNameEn = displayNameEn,
            IsSystem = true
        });
    }

    private static async Task EnsurePlan(
        PlatformDbContext db,
        string code,
        string name,
        long monthlyOre,
        int includedUnits,
        long overageUnitPrice,
        int? hardCapUnits,
        string features,
        bool isPublic,
        CancellationToken ct)
    {
        if (await db.Plans.Where(p => p.Code == code).AnyAsync(ct))
        {
            return;
        }

        db.Plans.Add(new Plan
        {
            Id = Ids.New(),
            Code = code,
            Name = name,
            Metric = "locations",
            BaseFeeMonthly = monthlyOre,
            BaseFeeYearly = monthlyOre * 10,
            IncludedUnits = includedUnits,
            OverageUnitPrice = overageUnitPrice,
            HardCapUnits = hardCapUnits,
            Features = features,
            IsPublic = isPublic,
            Active = true
        });
    }
}
