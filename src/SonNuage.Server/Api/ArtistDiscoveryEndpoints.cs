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
            // Ni importés ni demandés ; parmi eux, les premiers déjà cherchés sur YouTube.
            var taken = await DiscoveryService.TakenAsync(db, VideoIds(artists.SelectMany(a => a.Tracks)), ct);
            var open = artists.ToDictionary(a => a.Id, a => DiscoveryService.Open(a, taken));
            var first = artists.ToDictionary(a => a.Id, a => open[a.Id].Where(t => t.MatchedAt != null).Take(shown).ToList());
            var dto = await TrackDtoAsync(db, first.Values.SelectMany(t => t), options.Value.DiscoveryPreviewPath, ct);
            var refreshing = await db.Jobs.AnyAsync(j => j.Kind == JobKinds.DiscoveryRefresh.Name
                && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.Pending), ct);

            return new DiscoveryPageDto(artists.Count == 0 ? null : artists.Max(a => a.UpdatedAt), refreshing, artists.Select(a =>
                new DiscoveryArtistDto(a.Id, a.Name, a.Comment, a.BecauseOf.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                    first[a.Id].Select(dto).ToList(), a.AddedAt, open[a.Id].Count - first[a.Id].Count)).ToList());
        }).RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Discovery");

        // Titres phares suivants d'un artiste, après les « skip » que le client affiche déjà. S'il n'en reste pas assez
        // en base, les titres phares de l'artiste sont relus tout de suite ; ceux pas encore cherchés sur YouTube le
        // sont maintenant (quelques secondes par titre, d'où un nombre de titres borné par appel).
        app.MapGet("/api/discovery/artists/{id:guid}/tracks", async (Guid id, int? skip, int? take, SonNuageDbContext db, DiscoveryService discovery,
            IOptions<SonNuageOptions> options, CancellationToken ct) =>
        {
            if (skip is < 0 || take is < 1 or > DiscoveryService.StoredTracks) return Results.BadRequest();
            var artist = await db.DiscoveryArtists.Include(a => a.Tracks).FirstOrDefaultAsync(a => a.Id == id, ct);
            if (artist is null) return Results.NotFound();
            var count = take ?? DiscoverySettingsDto.Defaults.TracksPerArtist;
            async Task<List<DiscoveryTrack>> AfterAsync() =>
                DiscoveryService.Open(artist, await DiscoveryService.TakenAsync(db, VideoIds(artist.Tracks), ct)).Skip(skip ?? 0).ToList();

            var after = await AfterAsync();
            if (after.Count < count)
            {
                await discovery.FetchMoreTracksAsync(db, artist, ct);
                after = await AfterAsync();
            }
            await discovery.MatchAsync(db, after.Take(count), ct);
            // Une vidéo tout juste trouvée peut être déjà importée ou demandée : son titre n'est pas proposé.
            after = await AfterAsync();
            var wanted = after.Take(count).ToList();
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

    private static List<string> VideoIds(IEnumerable<DiscoveryTrack> tracks) =>
        tracks.Where(t => t.VideoId != null).Select(t => t.VideoId!).Distinct().ToList();

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
