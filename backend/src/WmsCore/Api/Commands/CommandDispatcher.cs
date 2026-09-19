using System.Text.Json;
using System.Text.Json.Serialization;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Api.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands;

file static class CommandJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };
}

public interface IStaleCommandHook
{
    Task<CommandResult?> EvaluateAsync(CommandEnvelope command, CommandContext context, CancellationToken ct);
}

public sealed class DefaultStaleCommandHook : IStaleCommandHook
{
    public Task<CommandResult?> EvaluateAsync(CommandEnvelope command, CommandContext context, CancellationToken ct)
    {
        if (command.OccurredAt > context.BatchNow.AddSeconds(1))
        {
            return Task.FromResult<CommandResult?>(new CommandResult(
                command.Id, CommandOutcome.Rejected, "stale_command", "occurred_at is in the future"));
        }

        if (command.OccurredAt < context.BatchNow.AddHours(-24))
        {
            return Task.FromResult<CommandResult?>(new CommandResult(
                command.Id, CommandOutcome.Rejected, "stale_command", "occurred_at older than 24 hours"));
        }

        return Task.FromResult<CommandResult?>(null);
    }
}

public sealed class CommandDispatcher(
    CommandRegistry registry,
    ITenantConnectionCache connections,
    IEntitlementCache entitlements,
    IPlatformTenantClient platform,
    IStaleCommandHook staleHook,
    IClock clock,
    ILogger<CommandDispatcher> log)
{
    public async Task<BatchResult> DispatchAsync(CommandBatch batch, CancellationToken ct)
    {
        var connection = await connections.GetConnectionStringAsync(batch.TenantId, ct)
            ?? throw new InvalidOperationException($"Tenant {batch.TenantId} connection not found");

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new TenantDbContext(options);
        var results = new List<CommandResult>();
        int? minAppVersion = null;

        if (batch.Commands.Count == 0)
        {
            return new BatchResult(results);
        }

        var deviceId = batch.Commands[0].DeviceId;
        var memberships = await platform.GetMembershipHistoryAsync(batch.TenantId, null, ct);
        var entitlement = await entitlements.GetAsync(batch.TenantId, ct);
        var skew = ComputeSkewMs(batch);
        var batchNow = batch.Now == default ? clock.UtcNow : batch.Now;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", LockKey(deviceId));

        for (var i = 0; i < batch.Commands.Count; i++)
        {
            var envelope = batch.Commands[i];
            if (envelope.DeviceId != deviceId)
            {
                results.Add(new CommandResult(envelope.Id, CommandOutcome.Rejected, "device_mismatch", "batch must be single-device"));
                continue;
            }

            var existing = await db.ProcessedCommands.AsNoTracking()
                .FirstOrDefaultAsync(p => p.CommandId == envelope.Id, ct);
            if (existing is not null)
            {
                results.Add(DeserializeResult(existing.Result));
                continue;
            }

            if (!registry.TryGetHandler(envelope.Type, out var handler))
            {
                results.Add(new CommandResult(envelope.Id, CommandOutcome.Rejected, "unknown_type", envelope.Type));
                continue;
            }

            if (envelope.V > handler.CurrentVersion)
            {
                minAppVersion = handler.CurrentVersion;
                for (; i < batch.Commands.Count; i++)
                {
                    results.Add(new CommandResult(
                        batch.Commands[i].Id,
                        CommandOutcome.Unknown,
                        "upgrade_required",
                        $"command version {batch.Commands[i].V} unsupported"));
                }

                break;
            }

            if (entitlement is { LifecycleState: "TrialExpired" } || entitlement is { Maintenance: true })
            {
                results.Add(new CommandResult(
                    envelope.Id,
                    CommandOutcome.Held,
                    entitlement!.Maintenance ? "maintenance" : "trial_expired",
                    "tenant not accepting commands"));
                continue;
            }

            var clockSkewMs = 0;
            var occurredAt = envelope.OccurredAt;
            if (Math.Abs(skew) > TimeSpan.FromMinutes(5).TotalMilliseconds)
            {
                clockSkewMs = skew;
                occurredAt = envelope.OccurredAt.AddMilliseconds(-skew);
            }

            var context = new CommandContext
            {
                TenantId = batch.TenantId,
                DeviceId = envelope.DeviceId,
                UserId = envelope.UserId,
                OccurredAt = occurredAt,
                BatchNow = batchNow,
                ClockSkewMs = clockSkewMs,
                Memberships = memberships
            };

            var stale = await staleHook.EvaluateAsync(envelope with { OccurredAt = occurredAt }, context, ct);
            if (stale is not null)
            {
                await StoreAsync(db, stale, deviation: true, envelope, clock.UtcNow, ct);
                results.Add(stale);
                continue;
            }

            if (!IsAuthorized(envelope.UserId, occurredAt, memberships))
            {
                var forbidden = new CommandResult(envelope.Id, CommandOutcome.Rejected, "forbidden", "no membership at occurred_at");
                await StoreAsync(db, forbidden, deviation: true, envelope, clock.UtcNow, ct);
                results.Add(forbidden);
                continue;
            }

            JsonElement payload;
            try
            {
                payload = envelope.V == handler.CurrentVersion
                    ? envelope.Payload
                    : registry.Upcast(envelope.Type, envelope.V, envelope.Payload, handler.CurrentVersion);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Upcast failed for {CommandId}", envelope.Id);
                results.Add(new CommandResult(envelope.Id, CommandOutcome.Rejected, "upcast_failed", ex.Message));
                continue;
            }

            var upcastEnvelope = envelope with { Payload = payload, V = handler.CurrentVersion, OccurredAt = occurredAt };

            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", LockKey(batch.TenantId));
            try
            {
                var result = await handler.HandleAsync(upcastEnvelope, context, new TenantDbAccessor(db), ct);
                if (result.ClockSkewMs is null && clockSkewMs != 0)
                {
                    result = result with { ClockSkewMs = clockSkewMs };
                }

                // Held must not land in processed_commands: the device retries when the cap or tenant state lifts.
                if (result.Outcome != CommandOutcome.Held)
                {
                    await StoreAsync(db, result, deviation: result.Outcome == CommandOutcome.Rejected, envelope, clock.UtcNow, ct);
                }

                results.Add(result);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Command {CommandId} failed", envelope.Id);
                results.Add(new CommandResult(envelope.Id, CommandOutcome.Unknown, "transient", ex.Message));
            }
        }

        await tx.CommitAsync(ct);
        return new BatchResult(results, minAppVersion, UpgradeRequired: minAppVersion is not null);
    }

    private static async Task StoreAsync(
        TenantDbContext db,
        CommandResult result,
        bool deviation,
        CommandEnvelope envelope,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (deviation && result.Outcome == CommandOutcome.Rejected)
        {
            db.Deviations.Add(new Deviation
            {
                Id = Ids.New(),
                Kind = "rejected",
                CommandId = envelope.Id,
                Detail = JsonSerializer.Serialize(new { result.Code, result.Message }),
                CreatedAt = now
            });
        }

        db.ProcessedCommands.Add(new ProcessedCommand
        {
            CommandId = result.CommandId,
            Result = JsonSerializer.Serialize(result, CommandJson.Options),
            AppliedAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    private static bool IsAuthorized(Guid userId, DateTimeOffset occurredAt, IReadOnlyList<MembershipAssignment> memberships) =>
        memberships.Any(m =>
            m.UserId == userId
            && m.ValidFrom <= occurredAt
            && (m.ValidTo is null || m.ValidTo > occurredAt));

    private static int ComputeSkewMs(CommandBatch batch)
    {
        if (batch.Commands.Count == 0 || batch.Now == default)
        {
            return 0;
        }

        var maxOccurred = batch.Commands.Max(c => c.OccurredAt);
        return (int)(maxOccurred - batch.Now).TotalMilliseconds;
    }

    internal static long LockKey(Guid id)
    {
        var bytes = id.ToByteArray();
        return BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8);
    }

    private static CommandResult DeserializeResult(string json) =>
        JsonSerializer.Deserialize<CommandResult>(json, CommandJson.Options)
        ?? new CommandResult(Guid.Empty, CommandOutcome.Unknown, "corrupt", "stored result missing");
}



