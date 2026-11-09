using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonNuage.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RematchDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Les premiers titres phares (Deezer : homonymes, titres d'invité) et leurs correspondances YouTube
            // (reprises karaoké) étaient faux : on les oublie, la prochaine actualisation les refait.
            migrationBuilder.Sql("DELETE FROM \"DiscoveryTracks\";");
            migrationBuilder.Sql("UPDATE \"DiscoveryArtists\" SET \"TracksFetchedAt\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
