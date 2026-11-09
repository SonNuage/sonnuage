using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Api;

/// <summary>
/// Découverte par artiste : artistes proches de ceux de la bibliothèque et leurs titres phares, prêts à écouter et à
/// demander. Hors de la bibliothèque : réservée aux contributeurs et aux admins (décidé le 9 novembre 2026).
/// </summary>
public static class ArtistDiscoveryEndpoints
{
    public static void MapArtistDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/discovery/artists", async (SonNuageDbContext db, IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            var artists = await db.DiscoveryArtists.AsNoTracking().Include(a => a.Tracks)
                .OrderByDescending(a => a.Score).ToListAsync(ct);
            var videoIds = artists.SelectMany(a => a.Tracks).Where(t => t.VideoId != null).Select(t => t.VideoId!).Distinct().ToList();
            var imports = await db.YoutubeItems.Where(i => videoIds.Contains(i.VideoId))
                .Select(i => new { i.VideoId, i.Status }).ToListAsync(ct);
            var status = imports.GroupBy(i => i.VideoId).ToDictionary(g => g.Key, g => g.Max(i => i.Status).ToString());
            var dir = options.Value.DiscoveryPreviewPath;
            var refreshing = await db.Jobs.AnyAsync(j => j.Kind == JobKinds.DiscoveryRefresh.Name
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Pending), ct);

            return new DiscoveryPageDto(artists.Count == 0 ? null : artists.Max(a => a.UpdatedAt), refreshing, artists.Select(a =>
                new DiscoveryArtistDto(a.Id, a.Name, a.Comment, a.BecauseOf.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                    a.Tracks.OrderBy(t => t.Rank).Select(t => new DiscoveryTrackDto(t.Id, t.Title, t.DurationSeconds, t.ReleaseName,
                        t.VideoId, t.VideoTitle, t.Channel,
                        t.VideoId is null ? null : $"https://i.ytimg.com/vi/{t.VideoId}/mqdefault.jpg",
                        t.MatchScore,
                        t.VideoId is not null && File.Exists(Path.Combine(dir, $"{t.VideoId}.mp3")),
                        t.VideoId is null ? null : status.GetValueOrDefault(t.VideoId))).ToList())).ToList());
        }).RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Discovery");

        app.MapPost("/api/admin/discovery/refresh", async (IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await scopes.RequestAsync(MaintenanceJobs.DiscoveryRefresh(), ct);
            return Results.Accepted();
        }).RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");
    }
}
