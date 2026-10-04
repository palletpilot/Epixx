using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    [Migration("20261004063800_LocationReservationAndStock")]
    public partial class LocationReservationAndStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET lock_timeout = '5s'");
            migrationBuilder.Sql("SET statement_timeout = '60s'");

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "handling_unit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lpn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_handling_unit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handling_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    article_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "location_reservation",
                columns: table => new
                {
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location_reservation", x => new { x.location_id, x.task_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_location_warehouse_id_code",
                table: "location",
                columns: new[] { "warehouse_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_handling_unit_warehouse_id_lpn",
                table: "handling_unit",
                columns: new[] { "warehouse_id", "lpn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_handling_unit_location_id",
                table: "handling_unit",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_location_id",
                table: "stock",
                column: "location_id",
                unique: true,
                filter: "handling_unit_id is not null");

            migrationBuilder.CreateIndex(
                name: "ix_location_reservation_expires_at",
                table: "location_reservation",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_location_reservation_location_id",
                table: "location_reservation",
                column: "location_id",
                unique: true,
                filter: "released = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "location_reservation");
            migrationBuilder.DropTable(name: "stock");
            migrationBuilder.DropTable(name: "handling_unit");
            migrationBuilder.DropTable(name: "location");
        }
    }
}
