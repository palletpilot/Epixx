using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Lagerkraft.Shared;
using Lagerkraft.WmsCore.Api.Commands.Inventory;
using Lagerkraft.WmsCore.Api.Data;
using Lagerkraft.WmsCore.Inventory.Contracts;
using Lagerkraft.WmsCore.Outbound.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Lagerkraft.WmsCore.Api.Commands.Outbound;

public sealed record ShipAsPickedPayload(Guid OrderId, Guid ToteId);

public sealed class ShipAsPickedValidator : AbstractValidator<ShipAsPickedPayload>
{
    public ShipAsPickedValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.ToteId).NotEmpty();
    }
}

public sealed class ShipAsPickedHandler(
    IClock clock,
    IValidator<ShipAsPickedPayload> validator) : ICommandHandler
{
    public string Type => "ShipAsPicked";
    public int CurrentVersion => 1;

    public async Task<CommandResult> HandleAsync(
        CommandEnvelope command,
        CommandContext context,
        TenantDbAccessor db,
        CancellationToken ct)
    {
        var payload = command.Payload.Deserialize<ShipAsPickedPayload>(InventoryJson.Options)
            ?? throw new InvalidOperationException("invalid ShipAsPicked payload");
        var validation = await validator.ValidateAsync(payload, ct);
        if (!validation.IsValid)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "validation", validation.ToString());
        }

        var order = await db.Db.OutboundOrders.FirstOrDefaultAsync(o => o.Id == payload.OrderId, ct);
        if (order is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_order", "order not found");
        }

        if (order.Status != "picked")
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "invalid_status", order.Status);
        }

        var warehouse = await db.Db.Warehouses.AsNoTracking().FirstAsync(w => w.Id == order.WarehouseId, ct);
        if (warehouse.PackStep)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "pack_required", "pack_step is required");
        }

        if (!HasPermission(context, order.WarehouseId, Permissions.OutboundShip))
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "forbidden", "outbound.ship required");
        }

        var picking = await db.Db.Locations.FirstOrDefaultAsync(
            l => l.WarehouseId == order.WarehouseId && l.Code == "PICKING" && l.IsSystem, ct);
        if (picking is null)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "unknown_location", "PICKING not found");
        }

        var toteBalances = await db.Db.StockBalances
            .Where(b => b.LocationId == picking.Id && b.HandlingUnitId == payload.ToteId && b.QtyBase > 0)
            .ToListAsync(ct);
        if (toteBalances.Count == 0)
        {
            return new CommandResult(command.Id, CommandOutcome.Rejected, "hu_moved", "tote not at PICKING");
        }

        var now = clock.UtcNow;
        // ponytail: payload has no shipment_id; server mints. Upgrade: client-generated shipment id on the command.
        var shipment = new Shipment
        {
            Id = Ids.New(),
            OrderId = order.Id,
            ShippedAt = context.OccurredAt
        };
        db.Db.Shipments.Add(shipment);
        db.Db.ShipmentHandlingUnits.Add(new ShipmentHandlingUnit
        {
            ShipmentId = shipment.Id,
            HandlingUnitId = payload.ToteId
        });

        foreach (var balance in toteBalances)
        {
            var article = await db.Db.Articles.AsNoTracking().FirstAsync(a => a.Id == balance.ArticleId, ct);
            var uom = await db.Db.UnitsOfMeasure.AsNoTracking().FirstAsync(u => u.Id == article.BaseUomId, ct);
            var content = await db.Db.HandlingUnitContents.FirstOrDefaultAsync(
                c => c.HandlingUnitId == payload.ToteId && c.ArticleId == balance.ArticleId, ct);
            db.Db.StockMovements.Add(new StockMovement
            {
                Id = Ids.New(),
                OccurredAt = context.OccurredAt,
                RecordedAt = now,
                ActorUserId = context.UserId,
                DeviceId = context.DeviceId,
                CommandId = command.Id,
                ArticleId = balance.ArticleId,
                FromLocationId = picking.Id,
                ToLocationId = null,
                FromHandlingUnitId = payload.ToteId,
                ToHandlingUnitId = null,
                QtyBase = balance.QtyBase,
                BaseUomCode = uom.Code,
                EnteredQty = balance.QtyBase,
                EnteredLevelId = content?.PackagingLevelId,
                EnteredUomId = article.BaseUomId,
                Reason = "ship",
                ReferenceType = "order",
                ReferenceId = order.Id
            });
            db.Db.StockBalances.Remove(balance);
            WriteLog(db, command, context, now, "stock_balance", payload.ToteId, "delete", JsonSerializer.Serialize(
                new StockBalanceDto(picking.Id, balance.ArticleId, payload.ToteId, "0", "0"),
                InventoryJson.Options));
        }

        order.Status = "shipped";
        var lines = await db.Db.OutboundOrderLines.Where(l => l.OrderId == order.Id).ToListAsync(ct);
        var dto = new OrderDto(
            order.Id,
            order.WarehouseId,
            order.Source,
            order.ExternalRef,
            order.DestinationName,
            order.RequestedShipDate,
            order.Status,
            order.Notes,
            lines.Select(l => new OrderLineDto(
                l.Id,
                l.OrderId,
                l.ArticleId,
                DecimalString(l.RequestedQtyBase),
                l.RequestedLevelId,
                DecimalString(l.AllocatedQtyBase),
                DecimalString(l.TolerancePct),
                l.Status)).ToArray());
        var orderJson = JsonSerializer.Serialize(dto, InventoryJson.Options);
        WriteLog(db, command, context, now, "order", order.Id, "upsert", orderJson);
        db.Db.Outbox.Add(new OutboxRow
        {
            Id = Ids.New(),
            Type = "outbound.shipped",
            Payload = JsonSerializer.Serialize(new ShipmentDto(
                shipment.Id,
                shipment.OrderId,
                shipment.DockLocationId,
                shipment.CarrierRef,
                shipment.ConfirmationCode,
                shipment.ShippedAt,
                [payload.ToteId]), InventoryJson.Options),
            OccurredAt = context.OccurredAt
        });
        await db.Db.SaveChangesAsync(ct);
        return new CommandResult(command.Id, CommandOutcome.Applied);
    }

    private static void WriteLog(
        TenantDbAccessor db,
        CommandEnvelope command,
        CommandContext context,
        DateTimeOffset now,
        string entity,
        Guid id,
        string op,
        string payload)
    {
        db.Db.ChangeLog.Add(new ChangeLogRow
        {
            Entity = entity,
            Id = id,
            Op = op,
            Payload = payload,
            CommandId = command.Id,
            Actor = context.UserId,
            OccurredAt = context.OccurredAt,
            RecordedAt = now
        });
    }

    private static bool HasPermission(CommandContext context, Guid warehouseId, string permission) =>
        context.Memberships.Any(m =>
            m.UserId == context.UserId
            && m.ValidFrom <= context.OccurredAt
            && (m.ValidTo is null || m.ValidTo > context.OccurredAt)
            && Permissions.ForRole(m.Role).Contains(permission)
            && (m.WarehouseId is null || m.WarehouseId == warehouseId));

    private static string DecimalString(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
