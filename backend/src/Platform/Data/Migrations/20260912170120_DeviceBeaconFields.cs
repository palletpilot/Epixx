using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceBeaconFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid[]>(
                name: "warehouse_ids",
                table: "enrollment_codes",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_beacon_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_sync_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "oldest_pending_occurred_at",
                table: "devices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pending_count",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "warehouse_ids",
                table: "enrollment_codes");

            migrationBuilder.DropColumn(
                name: "last_beacon_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "last_sync_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "oldest_pending_occurred_at",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "pending_count",
                table: "devices");
        }
    }
}
