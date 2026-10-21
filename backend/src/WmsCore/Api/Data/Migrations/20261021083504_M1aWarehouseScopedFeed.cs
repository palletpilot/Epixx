using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1aWarehouseScopedFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "device_id",
                table: "stock_movement",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "deviation",
                type: "text",
                nullable: false,
                defaultValue: "open");

            migrationBuilder.AddColumn<Guid>(
                name: "warehouse_id",
                table: "deviation",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "warehouse_id",
                table: "change_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_deviation_status",
                table: "deviation",
                sql: "status IN ('open', 'pending_approval', 'resolved', 'dismissed')");

            migrationBuilder.CreateIndex(
                name: "ix_change_log_warehouse_id_seq",
                table: "change_log",
                columns: new[] { "warehouse_id", "seq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_deviation_status",
                table: "deviation");

            migrationBuilder.DropIndex(
                name: "ix_change_log_warehouse_id_seq",
                table: "change_log");

            migrationBuilder.DropColumn(
                name: "status",
                table: "deviation");

            migrationBuilder.DropColumn(
                name: "warehouse_id",
                table: "deviation");

            migrationBuilder.DropColumn(
                name: "warehouse_id",
                table: "change_log");

            migrationBuilder.AlterColumn<Guid>(
                name: "device_id",
                table: "stock_movement",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
