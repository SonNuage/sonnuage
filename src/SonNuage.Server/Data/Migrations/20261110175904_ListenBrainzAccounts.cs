using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListenBrainzAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ListenBrainzSubmitSince",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListenBrainzToken",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListenBrainzUserName",
                table: "Users",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ListenBrainz",
                table: "PlayEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListenBrainzSubmitSince",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ListenBrainzToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ListenBrainzUserName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ListenBrainz",
                table: "PlayEvents");
        }
    }
}
