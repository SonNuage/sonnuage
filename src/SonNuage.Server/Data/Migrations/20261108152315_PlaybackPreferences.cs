using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlaybackPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CrossfadeSeconds",
                table: "Users",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "InfiniteAhead",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3); // Les comptes existants gardent l'avance par défaut.

            migrationBuilder.AddColumn<bool>(
                name: "InfinitePlay",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CrossfadeSeconds",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InfiniteAhead",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InfinitePlay",
                table: "Users");
        }
    }
}
