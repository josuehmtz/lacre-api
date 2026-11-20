using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Invitations.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuestArrivals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CheckInId",
                table: "Guests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Guests_CheckInId",
                table: "Guests",
                column: "CheckInId");

            migrationBuilder.AddForeignKey(
                name: "FK_Guests_CheckIns_CheckInId",
                table: "Guests",
                column: "CheckInId",
                principalTable: "CheckIns",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Guests_CheckIns_CheckInId",
                table: "Guests");

            migrationBuilder.DropIndex(
                name: "IX_Guests_CheckInId",
                table: "Guests");

            migrationBuilder.DropColumn(
                name: "CheckInId",
                table: "Guests");
        }
    }
}
