using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ArtistMusicBrainzSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MusicBrainzDerivedAt",
                table: "Artists",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MusicBrainzIdSource",
                table: "Artists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicBrainzReleaseGroupId",
                table: "Albums",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MusicBrainzDerivedAt",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzIdSource",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "MusicBrainzReleaseGroupId",
                table: "Albums");
        }
    }
}
