using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistReleasesCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtistReleases",
                columns: table => new
                {
                    ArtistMbid = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ReleaseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    PrimaryType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SecondaryTypes = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FirstReleaseDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistReleases", x => new { x.ArtistMbid, x.ReleaseGroupId });
                });

            migrationBuilder.CreateTable(
                name: "ExternalFetches",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalFetches", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistReleases_ReleaseDate",
                table: "ArtistReleases",
                column: "ReleaseDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistReleases");

            migrationBuilder.DropTable(
                name: "ExternalFetches");
        }
    }
}
