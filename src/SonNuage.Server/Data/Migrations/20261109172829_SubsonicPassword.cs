using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubsonicPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubsonicKeyHash",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubsonicPassword",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SubsonicPasswordCreatedAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubsonicKeyHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubsonicPassword",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SubsonicPasswordCreatedAt",
                table: "Users");
        }
    }
}
