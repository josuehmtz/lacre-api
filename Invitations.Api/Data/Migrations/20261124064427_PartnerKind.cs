using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Invitations.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PartnerKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Venues",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Los convenios que ya existen son con salones.
                defaultValue: "Venue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Venues");
        }
    }
}
