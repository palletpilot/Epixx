using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "handling_unit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lpn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    height_mm = table.Column<int>(type: "integer", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handling_unit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "handling_unit_content",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    handling_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    packaging_level_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handling_unit_content", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "location_reservation",
                columns: table => new
                {
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location_reservation", x => x.location_id);
                });

            migrationBuilder.CreateTable(
                name: "stock_balance",
                columns: table => new
                {
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handling_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    reserved_qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    secondary_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balance", x => new { x.location_id, x.article_id, x.handling_unit_id });
                });

            migrationBuilder.CreateTable(
                name: "stock_movement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_handling_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_handling_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    base_uom_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entered_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    entered_level_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entered_uom_id = table.Column<Guid>(type: "uuid", nullable: true),
                    secondary_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    secondary_uom_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tolerance_delta_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movement", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_handling_unit_warehouse_id_lpn",
                table: "handling_unit",
                columns: new[] { "warehouse_id", "lpn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_handling_unit_content_handling_unit_id_article_id",
                table: "handling_unit_content",
                columns: new[] { "handling_unit_id", "article_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_location_reservation_expires_at",
                table: "location_reservation",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_location_reservation_task_id",
                table: "location_reservation",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_command_id",
                table: "stock_movement",
                column: "command_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_occurred_at",
                table: "stock_movement",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "handling_unit");

            migrationBuilder.DropTable(
                name: "handling_unit_content");

            migrationBuilder.DropTable(
                name: "location_reservation");

            migrationBuilder.DropTable(
                name: "stock_balance");

            migrationBuilder.DropTable(
                name: "stock_movement");
        }
    }
}
