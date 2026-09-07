using Lagerkraft.Shared.Tenancy;

namespace Lagerkraft.Platform.Data;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string SignupChannel { get; set; } = "self_serve";
    public string BillingStatus { get; set; } = "ok";
    public string? OperatingHours { get; set; }
    public bool NightShift { get; set; }
    public Guid? OwnerUserId { get; set; }
    public int RefreshTokenHours { get; set; } = 12;
    public LifecycleState LifecycleState { get; set; } = LifecycleState.Provisioning;
    public DateTimeOffset StateChangedAt { get; set; }
    public bool Maintenance { get; set; }
    public DateTimeOffset? TrialExtendedUntil { get; set; }
    public string? TermsVersion { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public string? DpaVersion { get; set; }
    public DateTimeOffset? DpaAcceptedAt { get; set; }
    public string? SchemaVersion { get; set; }
    public string MigrationStatus { get; set; } = "pending";
    public string? LastError { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public byte[]? WrappedDek { get; set; }
    public byte[]? ConnectionCiphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TenantTombstone
{
    public Guid TenantId { get; set; }
    public string CompanyName { get; set; } = "";
    public DateTimeOffset OffboardedAt { get; set; }
    public DateTimeOffset? DeletionCompletedAt { get; set; }
    public DateTimeOffset? PitrExpiryAt { get; set; }
    public DateTimeOffset? LogExpiryAt { get; set; }
    public string? CertificateHash { get; set; }
}

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Kind { get; set; } = "";
    public string? FromState { get; set; }
    public string? ToState { get; set; }
    public string? Reason { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Detail { get; set; }
}

public sealed class Membership
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public bool IsOwner { get; set; }
    public int SessionVersion { get; set; }
    public string? PinHash { get; set; }
    public int PinFailedAttempts { get; set; }
    public DateTimeOffset? PinLockedUntil { get; set; }
    public bool PinFullLoginRequired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public AppUser User { get; set; } = null!;
    public ICollection<RoleAssignment> RoleAssignments { get; set; } = [];
}

public sealed class Role
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string InternalName { get; set; } = "";
    public string DisplayNameSv { get; set; } = "";
    public string DisplayNameEn { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
}

public sealed class RoleAssignment
{
    public Guid Id { get; set; }
    public Guid MembershipId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? WarehouseId { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public Membership Membership { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public Guid[] WarehouseIds { get; set; } = [];
    public string? SecretHash { get; set; }
    public Guid? EnrolledBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeviceSession
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid MembershipId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class EnrollmentCode
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string CodeHash { get; set; } = "";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? DeviceId { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public string Hash { get; set; } = "";
    public int SessionVersion { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? DeviceId { get; set; }
    public Guid MembershipId { get; set; }
}

public sealed class IdentityProvider
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Type { get; set; } = "oidc";
    public string Issuer { get; set; } = "";
    public string ClientId { get; set; } = "";
    public byte[]? ClientSecret { get; set; }
    public DateTimeOffset? ClientSecretExpiresAt { get; set; }
    public string? DomainHint { get; set; }
    public string JitProvisioning { get; set; } = "off";
    public string? DefaultRole { get; set; }
    public bool Enforced { get; set; }
    public string? RoleMappings { get; set; }
}

public sealed class SignupRequest
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string OrgNumber { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string Slug { get; set; } = "";
    public string VerificationTokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? JoinRequestForTenantId { get; set; }
    public DateTimeOffset? JoinDecidedAt { get; set; }
    public Guid? JoinDecidedBy { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public Guid RoleId { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid InvitedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedUserId { get; set; }
}

public sealed class BillingAccount
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string LegalName { get; set; } = "";
    public string OrgNumber { get; set; } = "";
    public string? VatNumber { get; set; }
    public string VatTreatment { get; set; } = "se_standard";
    public string? BillingEmail { get; set; }
    public string? PeppolId { get; set; }
    public string InvoiceDelivery { get; set; } = "email";
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public string? PoReference { get; set; }
    public string Currency { get; set; } = "SEK";
    public string? FortnoxCustomerNumber { get; set; }
    public bool LateFeesEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Plan
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Metric { get; set; } = "locations";
    public long BaseFeeMonthly { get; set; }
    public long BaseFeeYearly { get; set; }
    public int IncludedUnits { get; set; }
    public long OverageUnitPrice { get; set; }
    public int? HardCapUnits { get; set; }
    public string Features { get; set; } = "{}";
    public bool IsPublic { get; set; }
    public bool Active { get; set; } = true;
    public string? FortnoxArticleBase { get; set; }
    public string? FortnoxArticleOverage { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public string Status { get; set; } = "trialing";
    public string BillingInterval { get; set; } = "monthly";
    public DateTimeOffset? CurrentPeriodStart { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public DateTimeOffset? TrialEndsAt { get; set; }
    public bool OverageAllowed { get; set; }
    public string? CustomTerms { get; set; }
    public Guid? ScheduledPlanId { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public string? DunningStage { get; set; }
    public DateTimeOffset? DunningStageChangedAt { get; set; }
    public DateTimeOffset? DunningPausedUntil { get; set; }
    public string? DunningPauseReason { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Plan Plan { get; set; } = null!;
    public Tenant Tenant { get; set; } = null!;
}

public sealed class LocationCounter
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid WarehouseId { get; set; }
    public int ActiveLocations { get; set; }
    public decimal AreaM2 { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
    public int ReconciliationDelta { get; set; }
}

public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }
    public string Consumer { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class WebhookEndpoint
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string[] Events { get; set; } = [];
    public bool Active { get; set; } = true;
}

public sealed class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid WebhookEndpointId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Attempt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ImportJob
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
