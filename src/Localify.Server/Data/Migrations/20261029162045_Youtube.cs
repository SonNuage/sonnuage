using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Localify.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Youtube : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YoutubeSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAlbum = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSyncedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YoutubeSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "YoutubeItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VideoId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AlbumHint = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    NeedsReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    MatchConfidence = table.Column<double>(type: "REAL", nullable: true),
                    MatchSource = table.Column<string>(type: "TEXT", nullable: true),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YoutubeItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_YoutubeItems_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_YoutubeItems_YoutubeSubscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "YoutubeSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YoutubeItems_Status_CreatedAt",
                table: "YoutubeItems",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_YoutubeItems_SubscriptionId",
                table: "YoutubeItems",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_YoutubeItems_TrackId",
                table: "YoutubeItems",
                column: "TrackId");

            migrationBuilder.CreateIndex(
                name: "IX_YoutubeItems_VideoId",
                table: "YoutubeItems",
                column: "VideoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YoutubeSubscriptions_Url",
                table: "YoutubeSubscriptions",
                column: "Url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YoutubeItems");

            migrationBuilder.DropTable(
                name: "YoutubeSubscriptions");
        }
    }
}
