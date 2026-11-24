using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Invitations.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CeremonyLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CeremonyAddress",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CeremonyMapsUrl",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CeremonyName",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CeremonyStartsAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CeremonyAddress",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CeremonyMapsUrl",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CeremonyName",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CeremonyStartsAt",
                table: "Events");
        }
    }
}
