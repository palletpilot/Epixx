using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Lagerkraft.WmsCore.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "packaging_level",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    qty_in_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    height_mm = table.Column<int>(type: "integer", nullable: false),
                    width_mm = table.Column<int>(type: "integer", nullable: false),
                    depth_mm = table.Column<int>(type: "integer", nullable: false),
                    gross_weight_g = table.Column<int>(type: "integer", nullable: false),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pallet_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_breakable = table.Column<bool>(type: "boolean", nullable: false),
                    is_default_receiving = table.Column<bool>(type: "boolean", nullable: false),
                    is_default_shipping = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packaging_level", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "unit_of_measure",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    dimension = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    factor_to_dimension_base = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    display_name_sv = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    display_name_en = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_of_measure", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "article",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    gtin = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    base_uom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_precision = table.Column<int>(type: "integer", nullable: false),
                    quantity_step = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    allow_loose_pick = table.Column<bool>(type: "boolean", nullable: false),
                    order_multiple_level_id = table.Column<Guid>(type: "uuid", nullable: true),
                    weight_per_base_unit_g = table.Column<int>(type: "integer", nullable: true),
                    pick_instruction = table.Column<string>(type: "text", nullable: true),
                    catch_weight = table.Column<bool>(type: "boolean", nullable: false),
                    catch_weight_uom_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_article", x => x.id);
                    table.ForeignKey(
                        name: "fk_article_unit_of_measure_base_uom_id",
                        column: x => x.base_uom_id,
                        principalTable: "unit_of_measure",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "unit_of_measure",
                columns: new[] { "id", "code", "dimension", "display_name_en", "display_name_sv", "factor_to_dimension_base" },
                values: new object[,]
                {
                    { new Guid("01900000-0000-7000-8000-000000000101"), "st", "count", "st", "st", 1m },
                    { new Guid("01900000-0000-7000-8000-000000000102"), "kg", "mass", "kg", "kg", 1m },
                    { new Guid("01900000-0000-7000-8000-000000000103"), "g", "mass", "g", "g", 0.001m },
                    { new Guid("01900000-0000-7000-8000-000000000104"), "l", "volume", "l", "l", 1m },
                    { new Guid("01900000-0000-7000-8000-000000000105"), "ml", "volume", "ml", "ml", 0.001m },
                    { new Guid("01900000-0000-7000-8000-000000000106"), "m", "length", "m", "m", 1m },
                    { new Guid("01900000-0000-7000-8000-000000000107"), "cm", "length", "cm", "cm", 0.01m },
                    { new Guid("01900000-0000-7000-8000-000000000108"), "m2", "area", "m²", "m²", 1m },
                    { new Guid("01900000-0000-7000-8000-000000000109"), "m3", "volume", "m³", "m³", 1000m }
                });

            migrationBuilder.CreateIndex(
                name: "ix_article_base_uom_id",
                table: "article",
                column: "base_uom_id");

            migrationBuilder.CreateIndex(
                name: "ix_article_sku",
                table: "article",
                column: "sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_packaging_level_article_id_rank",
                table: "packaging_level",
                columns: new[] { "article_id", "rank" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "article");

            migrationBuilder.DropTable(
                name: "packaging_level");

            migrationBuilder.DropTable(
                name: "unit_of_measure");
        }
    }
}
