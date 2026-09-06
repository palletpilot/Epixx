using Lagerkraft.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.Platform.Data;

public sealed class PlatformDbContext : IdentityUserContext<AppUser, Guid>
{
    private readonly IClock _clock;

    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, IClock clock)
        : base(options)
    {
        _clock = clock;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantTombstone> TenantTombstones => Set<TenantTombstone>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<EnrollmentCode> EnrollmentCodes => Set<EnrollmentCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();
    public DbSet<SignupRequest> SignupRequests => Set<SignupRequest>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<BillingAccount> BillingAccounts => Set<BillingAccount>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<LocationCounter> LocationCounters => Set<LocationCounter>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>().ToTable("asp_net_users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("asp_net_user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("asp_net_user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("asp_net_user_tokens");

        builder.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Slug).HasMaxLength(30);
            e.Property(t => t.CompanyName).HasMaxLength(200);
            e.Property(t => t.SignupChannel).HasMaxLength(32);
            e.Property(t => t.BillingStatus).HasMaxLength(32);
            e.Property(t => t.OperatingHours).HasColumnType("jsonb");
            e.Property(t => t.RefreshTokenHours).HasDefaultValue(12);
            e.Property(t => t.LifecycleState).HasConversion<string>().HasMaxLength(32);
            e.Property(t => t.TermsVersion).HasMaxLength(32);
            e.Property(t => t.DpaVersion).HasMaxLength(32);
            e.Property(t => t.SchemaVersion).HasMaxLength(128);
            e.Property(t => t.MigrationStatus).HasMaxLength(32).HasDefaultValue("pending");
        });

        builder.Entity<TenantTombstone>(e =>
        {
            e.HasKey(t => t.TenantId);
            e.Property(t => t.CompanyName).HasMaxLength(200);
            e.Property(t => t.CertificateHash).HasMaxLength(128);
        });

        builder.Entity<AuditLog>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(64);
            e.Property(a => a.FromState).HasMaxLength(32);
            e.Property(a => a.ToState).HasMaxLength(32);
            e.Property(a => a.Actor).HasMaxLength(64);
            e.Property(a => a.Detail).HasColumnType("jsonb");
        });

        builder.Entity<Membership>(e =>
        {
            e.HasIndex(m => new { m.UserId, m.TenantId }).IsUnique();
            e.HasOne(m => m.Tenant).WithMany().HasForeignKey(m => m.TenantId);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId);
        });

        builder.Entity<Role>(e =>
        {
            e.Property(r => r.InternalName).HasMaxLength(64);
            e.Property(r => r.DisplayNameSv).HasMaxLength(100);
            e.Property(r => r.DisplayNameEn).HasMaxLength(100);
            e.HasIndex(r => r.InternalName).IsUnique().HasFilter("tenant_id IS NULL");
            e.HasIndex(r => new { r.TenantId, r.InternalName }).IsUnique().HasFilter("tenant_id IS NOT NULL");
        });

        builder.Entity<RoleAssignment>(e =>
        {
            e.HasOne(a => a.Membership).WithMany(m => m.RoleAssignments).HasForeignKey(a => a.MembershipId);
            e.HasOne(a => a.Role).WithMany().HasForeignKey(a => a.RoleId);
        });

        builder.Entity<Device>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(100);
            e.Property(d => d.WarehouseIds).HasColumnType("uuid[]");
            e.Property(d => d.SecretHash).HasMaxLength(128);
        });

        builder.Entity<EnrollmentCode>(e =>
        {
            e.Property(c => c.CodeHash).HasMaxLength(128);
        });

        builder.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.Hash).HasMaxLength(128);
            e.HasIndex(t => t.Hash).IsUnique();
        });

        builder.Entity<IdentityProvider>(e =>
        {
            e.Property(p => p.Type).HasMaxLength(16);
            e.Property(p => p.Issuer).HasMaxLength(500);
            e.Property(p => p.ClientId).HasMaxLength(200);
            e.Property(p => p.DomainHint).HasMaxLength(200);
            e.Property(p => p.JitProvisioning).HasMaxLength(16);
            e.Property(p => p.DefaultRole).HasMaxLength(64);
            e.Property(p => p.RoleMappings).HasColumnType("jsonb");
            e.HasIndex(p => p.TenantId).IsUnique();
        });

        builder.Entity<SignupRequest>(e =>
        {
            e.Property(s => s.Email).HasMaxLength(256);
            e.Property(s => s.OrgNumber).HasMaxLength(32);
            e.Property(s => s.CompanyName).HasMaxLength(200);
            e.Property(s => s.Slug).HasMaxLength(30);
            e.Property(s => s.VerificationTokenHash).HasMaxLength(128);
        });

        builder.Entity<Invitation>(e =>
        {
            e.Property(i => i.Email).HasMaxLength(256);
            e.Property(i => i.TokenHash).HasMaxLength(128);
        });

        builder.Entity<BillingAccount>(e =>
        {
            e.HasIndex(b => b.TenantId).IsUnique();
            e.HasIndex(b => b.OrgNumber).IsUnique();
            e.Property(b => b.LegalName).HasMaxLength(200);
            e.Property(b => b.OrgNumber).HasMaxLength(32);
            e.Property(b => b.VatNumber).HasMaxLength(32);
            e.Property(b => b.VatTreatment).HasMaxLength(32);
            e.Property(b => b.BillingEmail).HasMaxLength(256);
            e.Property(b => b.PeppolId).HasMaxLength(64);
            e.Property(b => b.InvoiceDelivery).HasMaxLength(16);
            e.Property(b => b.AddressLine1).HasMaxLength(200);
            e.Property(b => b.AddressLine2).HasMaxLength(200);
            e.Property(b => b.PostalCode).HasMaxLength(16);
            e.Property(b => b.City).HasMaxLength(100);
            e.Property(b => b.Country).HasMaxLength(2);
            e.Property(b => b.PoReference).HasMaxLength(64);
            e.Property(b => b.Currency).HasMaxLength(3);
            e.Property(b => b.FortnoxCustomerNumber).HasMaxLength(32);
        });

        builder.Entity<Plan>(e =>
        {
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.Code).HasMaxLength(32);
            e.Property(p => p.Name).HasMaxLength(64);
            e.Property(p => p.Metric).HasMaxLength(32);
            e.Property(p => p.Features).HasColumnType("jsonb");
            e.Property(p => p.FortnoxArticleBase).HasMaxLength(32);
            e.Property(p => p.FortnoxArticleOverage).HasMaxLength(32);
        });

        builder.Entity<Subscription>(e =>
        {
            e.Property(s => s.Status).HasMaxLength(32);
            e.Property(s => s.BillingInterval).HasMaxLength(16);
            e.Property(s => s.CustomTerms).HasColumnType("jsonb");
            e.Property(s => s.DunningStage).HasMaxLength(32);
            e.Property(s => s.DunningPauseReason).HasMaxLength(200);
            e.HasOne(s => s.Plan).WithMany().HasForeignKey(s => s.PlanId);
            e.HasOne(s => s.Tenant).WithMany().HasForeignKey(s => s.TenantId);
        });

        builder.Entity<LocationCounter>(e =>
        {
            e.HasIndex(c => new { c.TenantId, c.WarehouseId }).IsUnique();
            e.Property(c => c.AreaM2).HasPrecision(18, 6);
        });

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

        builder.Entity<ImportJob>(e =>
        {
            e.Property(j => j.Status).HasMaxLength(32);
        });

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            var id = entityType.FindProperty("Id");
            if (id?.ClrType == typeof(Guid))
            {
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    public override int SaveChanges()
    {
        Stamp();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void Stamp()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                var id = entry.Metadata.FindProperty("Id");
                if (id?.ClrType == typeof(Guid)
                    && entry.Property("Id").CurrentValue is Guid guid
                    && guid == Guid.Empty)
                {
                    entry.Property("Id").CurrentValue = Ids.New();
                }

                if (entry.Metadata.FindProperty("CreatedAt") is not null
                    && entry.Property("CreatedAt").CurrentValue is DateTimeOffset created
                    && created == default)
                {
                    entry.Property("CreatedAt").CurrentValue = now;
                }

                if (entry.Metadata.FindProperty("UpdatedAt") is not null
                    && entry.Property("UpdatedAt").CurrentValue is DateTimeOffset updated
                    && updated == default)
                {
                    entry.Property("UpdatedAt").CurrentValue = now;
                }

                if (entry.Metadata.FindProperty("StateChangedAt") is not null
                    && entry.Property("StateChangedAt").CurrentValue is DateTimeOffset stateChanged
                    && stateChanged == default)
                {
                    entry.Property("StateChangedAt").CurrentValue = now;
                }
            }
            else if (entry.State == EntityState.Modified
                     && entry.Metadata.FindProperty("UpdatedAt") is not null)
            {
                entry.Property("UpdatedAt").CurrentValue = now;
            }
        }
    }
}
