using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RematchOfficialChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La chaîne officielle de l'artiste passe désormais devant les chaînes de paroles : on refait les correspondances.
            migrationBuilder.Sql("UPDATE \"DiscoveryTracks\" SET \"MatchedAt\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
