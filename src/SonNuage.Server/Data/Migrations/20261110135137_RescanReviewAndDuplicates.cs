using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RescanReviewAndDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Fingerprints",
                columns: table => new
                {
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    FileModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ComputedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fingerprints", x => x.TrackId);
                    table.ForeignKey(
                        name: "FK_Fingerprints_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HiddenFiles",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    KeptTrackId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HiddenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    HiddenBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiddenFiles", x => x.Path);
                });

            migrationBuilder.CreateTable(
                name: "RescanProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SuiteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Candidate = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ArtistName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AlbumTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    RecordingId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReleaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RescanProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RescanProposals_JobSuites_SuiteId",
                        column: x => x.SuiteId,
                        principalTable: "JobSuites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RescanProposals_Tracks_TrackId",
                        column: x => x.TrackId,
                        principalTable: "Tracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RescanProposals_Status_Score",
                table: "RescanProposals",
                columns: new[] { "Status", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_RescanProposals_SuiteId",
                table: "RescanProposals",
                column: "SuiteId");

            migrationBuilder.CreateIndex(
                name: "IX_RescanProposals_TrackId_Signature",
                table: "RescanProposals",
                columns: new[] { "TrackId", "Signature" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Fingerprints");

            migrationBuilder.DropTable(
                name: "HiddenFiles");

            migrationBuilder.DropTable(
                name: "RescanProposals");
        }
    }
}
