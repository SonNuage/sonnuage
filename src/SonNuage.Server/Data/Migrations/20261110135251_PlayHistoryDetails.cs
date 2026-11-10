using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayHistoryDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Client",
                table: "PlayEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientEventId",
                table: "PlayEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayEvents_UserId_ClientEventId",
                table: "PlayEvents",
                columns: new[] { "UserId", "ClientEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayEvents_UserId_ClientEventId",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "Client",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "ClientEventId",
                table: "PlayEvents");
        }
    }
}
