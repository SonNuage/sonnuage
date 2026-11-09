using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserLastSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastSeenAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSeenDevice",
                table: "Users",
                type: "TEXT",
                nullable: true);

            // Comptes existants : leur dernière session ouverte ou renouvelée (même format de date).
            migrationBuilder.Sql("""
                UPDATE "Users" SET
                    "LastSeenAt" = (SELECT r."CreatedAt" FROM "RefreshTokens" r WHERE r."UserId" = "Users"."Id" ORDER BY r."CreatedAt" DESC LIMIT 1),
                    "LastSeenDevice" = (SELECT r."DeviceName" FROM "RefreshTokens" r WHERE r."UserId" = "Users"."Id" ORDER BY r."CreatedAt" DESC LIMIT 1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastSeenDevice",
                table: "Users");
        }
    }
}
