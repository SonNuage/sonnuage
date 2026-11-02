using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class CrashReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrashReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    App = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Os = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Headline = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Report = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrashReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrashReports_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrashReports_ReceivedAt",
                table: "CrashReports",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CrashReports_UserId_ReceivedAt",
                table: "CrashReports",
                columns: new[] { "UserId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrashReports");
        }
    }
}
