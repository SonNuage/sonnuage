using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MusicBrainzCheckedAt",
                table: "Artists",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Artists",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DiscoveryArtists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MusicBrainzId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    BecauseOf = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TracksFetchedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryArtists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveryTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RecordingMbid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ReleaseName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    VideoId = table.Column<string>(type: "TEXT", maxLength: 11, nullable: true),
                    VideoTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    MatchScore = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveryTracks_DiscoveryArtists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "DiscoveryArtists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Artists_MusicBrainzId",
                table: "Artists",
                column: "MusicBrainzId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryArtists_MusicBrainzId",
                table: "DiscoveryArtists",
                column: "MusicBrainzId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryArtists_Score",
                table: "DiscoveryArtists",
                column: "Score");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryTracks_ArtistId",
                table: "DiscoveryTracks",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryTracks_VideoId",
                table: "DiscoveryTracks",
                column: "VideoId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryTracks");

            migrationBuilder.DropTable(
                name: "DiscoveryArtists");

            migrationBuilder.DropIndex(
                name: "IX_Artists_MusicBrainzId",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzCheckedAt",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Artists");
        }
    }
}
