using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DownloadRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveRequests",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerDay",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: SonNuage.Contracts.DownloadRequestLimits.DefaultPerDay);

            migrationBuilder.CreateTable(
                name: "DownloadRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VideoId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Decision = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DenyReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AutoApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    YoutubeItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadRequests_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadRequests_YoutubeItems_YoutubeItemId",
                        column: x => x.YoutubeItemId,
                        principalTable: "YoutubeItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRequests_CreatedAt",
                table: "DownloadRequests",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRequests_UserId_CreatedAt",
                table: "DownloadRequests",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRequests_VideoId",
                table: "DownloadRequests",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRequests_YoutubeItemId",
                table: "DownloadRequests",
                column: "YoutubeItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadRequests");

            migrationBuilder.DropColumn(
                name: "AutoApproveRequests",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RequestsPerDay",
                table: "Users");
        }
    }
}
