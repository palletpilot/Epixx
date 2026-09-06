namespace Lagerkraft.Shared.Tenancy;

public enum LifecycleState
{
    Provisioning,
    ProvisioningFailed,
    Trialing,
    Active,
    PastDue,
    Restricted,
    Suspended,
    TrialExpired,
    CancelPending,
    Offboarding,
    Deleting,
    Deleted
}

public sealed class TenantContext
{
    public Guid TenantId { get; init; }
    public Slug? Slug { get; init; }
    public int SchemaVersion { get; init; }
    public LifecycleState LifecycleState { get; init; }
    public bool Maintenance { get; init; }
}
