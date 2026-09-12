using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Data;

namespace Lagerkraft.WmsCore.Api.Commands.Probe;

public sealed record ProbeCommand(Guid ProbeId, string Message);

public sealed class ProbeValidator : AbstractValidator<ProbeCommand>
{
    public ProbeValidator()
    {
        RuleFor(x => x.ProbeId).NotEmpty();
        RuleFor(x => x.Message).NotEmpty().MaximumLength(200);
    }
}

/// <summary>Pipeline probe until C3 Task commands land. Writes change_log + outbox.</summary>
public sealed class ProbeHandler(IClock clock, IValidator<ProbeCommand> validator) : ICommandHandler
{
    public string Type => "Probe";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var parsed = command.Payload.Deserialize<ProbeCommand>(JsonOptions.Snake)
            ?? throw new InvalidOperationException("invalid probe payload");
        var validation = await validator.ValidateAsync(parsed, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        if (parsed.Message == "reject-me")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forced_reject", "probe forced reject");
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = parsed.ProbeId,
            message = parsed.Message,
            status = "done"
        });

        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = "probe",
            Id = parsed.ProbeId,
            Op = "upsert",
            Payload = payload,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = clock.UtcNow
        });

        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "inventory.probe.applied",
            Payload = payload,
            OccurredAt = context.OccurredAt
        });

        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }
}

public sealed class ProbeV0ToV1Upcaster : IUpcaster
{
    public string Type => "Probe";
    public int FromVersion => 0;
    public int ToVersion => 1;

    public JsonElement Upcast(JsonElement payload)
    {
        // v0 used "text"; v1 uses "message"
        using var doc = JsonDocument.Parse(payload.GetRawText());
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("message", out _))
        {
            return root;
        }

        var probeId = root.GetProperty("probe_id").GetGuid();
        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        return JsonSerializer.SerializeToElement(new { probe_id = probeId, message = text }, JsonOptions.Snake);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };
}
