using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryAddedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AddedAt",
                table: "DiscoveryArtists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Propositions déjà là : ajoutées à leur dernière actualisation.
            migrationBuilder.Sql("UPDATE \"DiscoveryArtists\" SET \"AddedAt\" = \"UpdatedAt\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "DiscoveryArtists");
        }
    }
}
