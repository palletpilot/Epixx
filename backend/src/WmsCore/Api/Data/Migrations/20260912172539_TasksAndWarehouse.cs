using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TasksAndWarehouse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suggested_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    picked_qty_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    from_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_handling_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    suggested_breakdown = table.Column<string>(type: "jsonb", nullable: true),
                    tolerance_pct = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_line", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "warehouse",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    code_pattern = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    operating_hours = table.Column<string>(type: "jsonb", nullable: true),
                    night_shift = table.Column<bool>(type: "boolean", nullable: false),
                    claim_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    blind_count = table.Column<bool>(type: "boolean", nullable: false),
                    count_auto_adjust_threshold = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    pack_step = table.Column<bool>(type: "boolean", nullable: false),
                    zone_picking = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warehouse", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_assigned_until",
                table: "task",
                column: "assigned_until");

            migrationBuilder.CreateIndex(
                name: "ix_task_warehouse_id_status",
                table: "task",
                columns: new[] { "warehouse_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_task_line_task_id",
                table: "task_line",
                column: "task_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task");

            migrationBuilder.DropTable(
                name: "task_line");

            migrationBuilder.DropTable(
                name: "warehouse");
        }
    }
}
