using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubsonicSavedQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedQueues",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrentTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PositionMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedQueues", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_SavedQueues_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedQueueEntries",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedQueueEntries", x => new { x.UserId, x.Position });
                    table.ForeignKey(
                        name: "FK_SavedQueueEntries_SavedQueues_UserId",
                        column: x => x.UserId,
                        principalTable: "SavedQueues",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedQueueEntries_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueueEntries_TrackId",
                table: "SavedQueueEntries",
                column: "TrackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedQueueEntries");

            migrationBuilder.DropTable(
                name: "SavedQueues");
        }
    }
}
