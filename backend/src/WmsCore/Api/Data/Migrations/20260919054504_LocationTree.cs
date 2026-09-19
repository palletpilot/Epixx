using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocationTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:ltree", ",,");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "activated_at",
                table: "warehouse",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_depth_mm",
                table: "warehouse",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_height_mm",
                table: "warehouse",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_max_weight_g",
                table: "warehouse",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_width_mm",
                table: "warehouse",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    warehouse_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    path = table.Column<string>(type: "ltree", nullable: false),
                    height_mm = table.Column<int>(type: "integer", nullable: true),
                    width_mm = table.Column<int>(type: "integer", nullable: true),
                    depth_mm = table.Column<int>(type: "integer", nullable: true),
                    max_weight_g = table.Column<int>(type: "integer", nullable: true),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_location", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_location_warehouse_id_code",
                table: "location",
                columns: new[] { "warehouse_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_location_warehouse_id_parent_id",
                table: "location",
                columns: new[] { "warehouse_id", "parent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location");

            migrationBuilder.DropColumn(
                name: "activated_at",
                table: "warehouse");

            migrationBuilder.DropColumn(
                name: "default_depth_mm",
                table: "warehouse");

            migrationBuilder.DropColumn(
                name: "default_height_mm",
                table: "warehouse");

            migrationBuilder.DropColumn(
                name: "default_max_weight_g",
                table: "warehouse");

            migrationBuilder.DropColumn(
                name: "default_width_mm",
                table: "warehouse");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:ltree", ",,");
        }
    }
}
