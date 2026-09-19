using System.Globalization;
using Lagerkraft.WmsCore.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Data;

public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options) : DbContext(options)
{
    public DbSet<TenantMeta> TenantMeta => Set<TenantMeta>();
    public DbSet<ChangeLogRow> ChangeLog => Set<ChangeLogRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<ProcessedCommand> ProcessedCommands => Set<ProcessedCommand>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<Deviation> Deviations => Set<Deviation>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<WarehouseTask> Tasks => Set<WarehouseTask>();
    public DbSet<TaskLine> TaskLines => Set<TaskLine>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<PackagingLevel> PackagingLevels => Set<PackagingLevel>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasPostgresExtension("ltree");

        builder.Entity<TenantMeta>(e =>
        {
            e.ToTable("tenant_meta");
            e.HasKey(x => x.Id);
            e.Property(x => x.SchemaVersion).HasMaxLength(128);
        });

        builder.Entity<ChangeLogRow>(e =>
        {
            e.ToTable("change_log");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.Property(x => x.Entity).HasMaxLength(128);
            e.Property(x => x.Op).HasMaxLength(32);
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });

        builder.Entity<OutboxRow>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(128);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.PublishedAt);
        });

        builder.Entity<ProcessedCommand>(e =>
        {
            e.ToTable("processed_commands");
            e.HasKey(x => x.CommandId);
            e.Property(x => x.Result).HasColumnType("jsonb");
        });

        builder.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => new { x.EventId, x.Consumer });
            e.Property(x => x.Consumer).HasMaxLength(64);
        });

        builder.Entity<Deviation>(e =>
        {
            e.ToTable("deviation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(64);
            e.Property(x => x.Detail).HasColumnType("jsonb");
        });

        builder.Entity<Warehouse>(e =>
        {
            e.ToTable("warehouse");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.CodePattern).HasMaxLength(64);
            e.Property(x => x.OperatingHours).HasColumnType("jsonb");
            e.Property(x => x.ClaimMinutes).HasDefaultValue(30);
            e.Property(x => x.CountAutoAdjustThreshold).HasPrecision(18, 6);
        });

        builder.Entity<Location>(e =>
        {
            e.ToTable("location");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Path).HasColumnType("ltree");
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => new { x.WarehouseId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.WarehouseId, x.ParentId });
        });

        builder.Entity<WarehouseTask>(e =>
        {
            e.ToTable("task");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => new { x.WarehouseId, x.Status });
            e.HasIndex(x => x.AssignedUntil);
        });

        builder.Entity<TaskLine>(e =>
        {
            e.ToTable("task_line");
            e.HasKey(x => x.Id);
            e.Property(x => x.RequestedQtyBase).HasPrecision(18, 6);
            e.Property(x => x.PickedQtyBase).HasPrecision(18, 6);
            e.Property(x => x.TolerancePct).HasPrecision(18, 6);
            e.Property(x => x.SuggestedBreakdown).HasColumnType("jsonb");
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => x.TaskId);
        });

        builder.Entity<UnitOfMeasure>(e =>
        {
            e.ToTable("unit_of_measure");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Dimension).HasMaxLength(16);
            e.Property(x => x.FactorToDimensionBase).HasPrecision(18, 6);
            e.Property(x => x.DisplayNameSv).HasMaxLength(16);
            e.Property(x => x.DisplayNameEn).HasMaxLength(16);
            e.HasData(CatalogDefaults.Units.Select(ToUnitOfMeasure));
        });

        builder.Entity<Article>(e =>
        {
            e.ToTable("article");
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(64);
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Gtin).HasMaxLength(14);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.QuantityStep).HasPrecision(18, 6);
            e.HasOne<UnitOfMeasure>().WithMany().HasForeignKey(x => x.BaseUomId);
        });

        builder.Entity<PackagingLevel>(e =>
        {
            e.ToTable("packaging_level");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.QtyInBase).HasPrecision(18, 6);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.HasIndex(x => new { x.ArticleId, x.Rank }).IsUnique();
        });
    }

    private static UnitOfMeasure ToUnitOfMeasure(UnitOfMeasureDto unit) => new()
    {
        Id = unit.Id,
        Code = unit.Code,
        Dimension = unit.Dimension,
        FactorToDimensionBase = decimal.Parse(unit.FactorToDimensionBase, CultureInfo.InvariantCulture),
        DisplayNameSv = unit.DisplayNameSv,
        DisplayNameEn = unit.DisplayNameEn
    };
}
