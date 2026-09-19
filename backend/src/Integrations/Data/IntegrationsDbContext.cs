using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Integrations.Data;

public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : DbContext(options)
{
    public const string ConsumerName = "integrations";

    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<ProcessedEvent>(e =>
        {
            e.HasKey(p => new { p.EventId, p.Consumer });
            e.Property(p => p.Consumer).HasMaxLength(64);
        });

        builder.Entity<WebhookEndpoint>(e =>
        {
            e.Property(w => w.Url).HasMaxLength(500);
            e.Property(w => w.Secret).HasMaxLength(128);
            e.Property(w => w.Events).HasColumnType("text[]");
        });

        builder.Entity<WebhookDelivery>(e =>
        {
            e.Property(d => d.EventType).HasMaxLength(128);
            e.Property(d => d.Status).HasMaxLength(32);
            e.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(d => d.WebhookEndpointId);
        });

        builder.Entity<ImportJob>(e => e.Property(j => j.Status).HasMaxLength(32));

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var id = entityType.FindProperty("Id");
            if (id?.ClrType == typeof(Guid))
            {
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }
}
