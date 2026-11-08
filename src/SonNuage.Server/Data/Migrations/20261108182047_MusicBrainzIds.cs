using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class MusicBrainzIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MusicBrainzRecordingId",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicBrainzReleaseId",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MusicBrainzReleaseId",
                table: "Albums",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MusicBrainzRecordingId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzReleaseId",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "MusicBrainzReleaseId",
                table: "Albums");
        }
    }
}
