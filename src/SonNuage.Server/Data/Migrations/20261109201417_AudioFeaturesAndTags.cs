using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AudioFeaturesAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TagsFetchedAt",
                table: "Tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AudioFeatures",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalyzedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FileModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Bpm = table.Column<double>(type: "REAL", nullable: true),
                    Danceability = table.Column<double>(type: "REAL", nullable: true),
                    Energy = table.Column<double>(type: "REAL", nullable: true),
                    Valence = table.Column<double>(type: "REAL", nullable: true),
                    Happy = table.Column<double>(type: "REAL", nullable: true),
                    Sad = table.Column<double>(type: "REAL", nullable: true),
                    Relaxed = table.Column<double>(type: "REAL", nullable: true),
                    Aggressive = table.Column<double>(type: "REAL", nullable: true),
                    Party = table.Column<double>(type: "REAL", nullable: true),
                    Acoustic = table.Column<double>(type: "REAL", nullable: true),
                    Electronic = table.Column<double>(type: "REAL", nullable: true),
                    Instrumental = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioFeatures", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_AudioFeatures_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrackTags",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackTags", x => new { x.TrackId, x.Name });
                    table.ForeignKey(
                        name: "FK_TrackTags_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackTags_Name",
                table: "TrackTags",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioFeatures");

            migrationBuilder.DropTable(
                name: "TrackTags");

            migrationBuilder.DropColumn(
                name: "TagsFetchedAt",
                table: "Tracks");
        }
    }
}
