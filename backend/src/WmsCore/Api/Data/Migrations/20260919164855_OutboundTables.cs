using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboundTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_order",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    external_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    requested_ship_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_order", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_order_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    requested_level_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocated_qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    tolerance_pct = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbound_order_line", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dock_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    carrier_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    confirmation_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shipment_handling_unit",
                columns: table => new
                {
                    shipment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handling_unit_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shipment_handling_unit", x => new { x.shipment_id, x.handling_unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbound_order_warehouse_id_source_external_ref",
                table: "outbound_order",
                columns: new[] { "warehouse_id", "source", "external_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbound_order_line_order_id",
                table: "outbound_order_line",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_shipment_order_id",
                table: "shipment",
                column: "order_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_order");

            migrationBuilder.DropTable(
                name: "outbound_order_line");

            migrationBuilder.DropTable(
                name: "shipment");

            migrationBuilder.DropTable(
                name: "shipment_handling_unit");
        }
    }
}
