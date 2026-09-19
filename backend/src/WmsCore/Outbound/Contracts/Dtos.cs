namespace Lagerkraft.WmsCore.Outbound.Contracts;

public sealed record OrderLineDto(
    Guid Id,
    Guid OrderId,
    Guid ArticleId,
    string RequestedQtyBase,
    Guid RequestedLevelId,
    string AllocatedQtyBase,
    string TolerancePct,
    string Status);

public sealed record OrderDto(
    Guid Id,
    Guid WarehouseId,
    string Source,
    string ExternalRef,
    string DestinationName,
    DateTimeOffset? RequestedShipDate,
    string Status,
    string? Notes,
    OrderLineDto[] Lines);

public sealed record ShipmentDto(
    Guid Id,
    Guid OrderId,
    Guid? DockLocationId,
    string? CarrierRef,
    string? ConfirmationCode,
    DateTimeOffset ShippedAt,
    Guid[] HandlingUnitIds);

public sealed record CreateOrderRequest(
    Guid Id,
    Guid WarehouseId,
    Guid ArticleId,
    string QtyBase,
    string? ExternalRef,
    string? DestinationName,
    Guid? RequestedLevelId);
