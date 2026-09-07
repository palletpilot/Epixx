using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinLockout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "pin_failed_attempts",
                table: "memberships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "pin_full_login_required",
                table: "memberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pin_locked_until",
                table: "memberships",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pin_failed_attempts",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "pin_full_login_required",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "pin_locked_until",
                table: "memberships");
        }
    }
}
