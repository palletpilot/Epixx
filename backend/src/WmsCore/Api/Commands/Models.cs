using System.Text.Json;

namespace Lagerkraft.WmsCore.Api.Commands;

public sealed record CommandEnvelope(
    Guid Id,
    string Type,
    int V,
    JsonElement Payload,
    DateTimeOffset OccurredAt,
    Guid DeviceId,
    Guid UserId);

public sealed record CommandBatch(
    Guid TenantId,
    DateTimeOffset Now,
    IReadOnlyList<CommandEnvelope> Commands);

public enum CommandOutcome
{
    Applied,
    Rejected,
    Held,
    Unknown
}

public sealed record CommandResult(
    Guid CommandId,
    CommandOutcome Outcome,
    string? Code = null,
    string? Message = null,
    object? Details = null,
    int? ClockSkewMs = null);

public sealed record BatchResult(
    IReadOnlyList<CommandResult> Results,
    int? MinAppVersion = null,
    bool UpgradeRequired = false);

public sealed class CommandContext
{
    public required Guid TenantId { get; init; }
    public required Guid DeviceId { get; init; }
    public required Guid UserId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required DateTimeOffset BatchNow { get; init; }
    public int ClockSkewMs { get; init; }
    public required IReadOnlyList<MembershipAssignment> Memberships { get; init; }
}

public sealed record MembershipAssignment(
    Guid UserId,
    string Role,
    Guid? WarehouseId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo);

public interface ICommandHandler
{
    string Type { get; }
    int CurrentVersion { get; }
    Task<CommandResult> HandleAsync(CommandEnvelope command, CommandContext context, TenantDbAccessor db, CancellationToken ct);
}

public interface IUpcaster
{
    string Type { get; }
    int FromVersion { get; }
    int ToVersion { get; }
    JsonElement Upcast(JsonElement payload);
}

public sealed class TenantDbAccessor(Data.TenantDbContext db)
{
    public Data.TenantDbContext Db => db;
}
