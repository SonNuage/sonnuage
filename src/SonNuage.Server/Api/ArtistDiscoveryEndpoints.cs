using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SonNuage.Contracts;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;
using SonNuage.Server.Jobs;

namespace SonNuage.Server.Api;

/// <summary>
/// Découverte par artiste : artistes proches de ceux de la bibliothèque et leurs titres phares, prêts à écouter et à
/// demander. Hors de la bibliothèque : réservée aux contributeurs et aux admins (décidé le 9 novembre 2026).
/// La page donne les premiers titres de chaque artiste (réglage « titres par artiste ») ; les suivants se demandent
/// artiste par artiste (« voir plus », « voir tout ») et ne sont cherchés sur YouTube qu'à ce moment-là.
/// </summary>
public static class ArtistDiscoveryEndpoints
{
    public static void MapArtistDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/discovery/artists", async (SonNuageDbContext db, DiscoverySettingsStore settings, IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            var shown = settings.Settings.TracksPerArtist;
            var artists = await db.DiscoveryArtists.AsNoTracking().Include(a => a.Tracks)
                .OrderByDescending(a => a.AddedAt).ThenByDescending(a => a.Score).ToListAsync(ct);
            var first = artists.ToDictionary(a => a.Id, a => a.Tracks.OrderBy(t => t.Rank).Take(shown).ToList());
            var dto = await TrackDtoAsync(db, first.Values.SelectMany(t => t), options.Value.DiscoveryPreviewPath, ct);
            var refreshing = await db.Jobs.AnyAsync(j => j.Kind == JobKinds.DiscoveryRefresh.Name
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Pending), ct);

            return new DiscoveryPageDto(artists.Count == 0 ? null : artists.Max(a => a.UpdatedAt), refreshing, artists.Select(a =>
                new DiscoveryArtistDto(a.Id, a.Name, a.Comment, a.BecauseOf.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                    first[a.Id].Select(dto).ToList(), a.AddedAt, a.Tracks.Count - first[a.Id].Count)).ToList());
        }).RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Discovery");

        // Titres phares suivants d'un artiste. Ceux pas encore cherchés sur YouTube le sont maintenant : quelques
        // secondes par titre, d'où un nombre de titres borné par appel.
        app.MapGet("/api/discovery/artists/{id:guid}/tracks", async (Guid id, int? skip, int? take, SonNuageDbContext db, DiscoveryService discovery,
            IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            if (skip is < 0 || take is < 1 or > DiscoveryService.StoredTracks) return Results.BadRequest();
            var artist = await db.DiscoveryArtists.Include(a => a.Tracks).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (artist is null) return Results.NotFound();
            var after = artist.Tracks.OrderBy(t => t.Rank).Skip(skip ?? 0).ToList();
            var wanted = after.Take(take ?? DiscoverySettingsDto.Defaults.TracksPerArtist).ToList();
            await discovery.MatchAsync(db, wanted, ct);
            var dto = await TrackDtoAsync(db, wanted, options.Value.DiscoveryPreviewPath, ct);
            return Results.Ok(new DiscoveryMoreDto(wanted.Select(dto).ToList(), after.Count - wanted.Count));
        }).RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Discovery");

        var settings = app.MapGroup("/api/admin/discovery/settings").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");
        settings.MapGet("", (DiscoverySettingsStore store) => store.Settings);
        settings.MapPut("", (DiscoverySettingsDto s, DiscoverySettingsStore store) =>
            store.Save(s) is { } error ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest) : Results.Ok(store.Settings));

        app.MapPost("/api/admin/discovery/refresh", async (IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await scopes.RequestAsync(MaintenanceJobs.DiscoveryRefresh(), ct);
            return Results.Accepted();
        }).RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");
    }

    /// <summary>De quoi présenter ces titres : état de l'import de leur vidéo, extrait déjà téléchargé ou non.</summary>
    private static async Task<Func<DiscoveryTrack, DiscoveryTrackDto>> TrackDtoAsync(SonNuageDbContext db, IEnumerable<DiscoveryTrack> tracks,
        string previewDir, CancellationToken ct)
    {
        var videoIds = tracks.Where(t => t.VideoId != null).Select(t => t.VideoId!).Distinct().ToList();
        var imports = await db.YoutubeItems.Where(i => videoIds.Contains(i.VideoId)).Select(i => new { i.VideoId, i.Status }).ToListAsync(ct);
        var status = imports.GroupBy(i => i.VideoId).ToDictionary(g => g.Key, g => g.Max(i => i.Status).ToString());
        return t => new DiscoveryTrackDto(t.Id, t.Title, t.DurationSeconds, t.ReleaseName, t.VideoId, t.VideoTitle, t.Channel,
            t.VideoId is null ? null : $"https://i.ytimg.com/vi/{t.VideoId}/mqdefault.jpg",
            t.MatchScore,
            t.VideoId is not null && File.Exists(Path.Combine(previewDir, $"{t.VideoId}.mp3")),
            t.VideoId is null ? null : status.GetValueOrDefault(t.VideoId));
    }
}
