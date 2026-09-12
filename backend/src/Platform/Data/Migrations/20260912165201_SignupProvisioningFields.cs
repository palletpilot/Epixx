using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lagerkraft.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class SignupProvisioningFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "signup_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "signup_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "signup_requests",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "provisioning_attempts",
                table: "signup_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_signup_requests_org_number",
                table: "signup_requests",
                column: "org_number");

            migrationBuilder.CreateIndex(
                name: "ix_signup_requests_slug",
                table: "signup_requests",
                column: "slug");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_signup_requests_org_number",
                table: "signup_requests");

            migrationBuilder.DropIndex(
                name: "ix_signup_requests_slug",
                table: "signup_requests");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "signup_requests");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "signup_requests");

            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "signup_requests");

            migrationBuilder.DropColumn(
                name: "provisioning_attempts",
                table: "signup_requests");
        }
    }
}
