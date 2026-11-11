using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Discovery;
using SonNuage.Server.Jobs;
using SonNuage.Server.Library;

namespace SonNuage.Server.Api;

/// <summary>
/// Nouveautés des artistes de la bibliothèque (couche 2 de la découverte). « Qui voit quoi » (9 novembre 2026) : un
/// utilisateur simple ne voit que les parutions déjà dans la bibliothèque ; contributeurs et admins voient aussi les
/// autres, avec de quoi les demander ou les importer.
/// </summary>
public static class ReleaseEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 200;
    /// <summary>Rubrique « Nouveautés de vos artistes » de l'accueil.</summary>
    public const int HomeCount = 20;

    public static void MapReleaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases", async (string? when, string? type, int? limit, HttpContext http, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (!TryParseWindow(when, out var window) || !TryParseKind(type, out var kind)) return Results.BadRequest();
            var presence = http.User.CanRequest() ? ArtistReleaseQueries.Presence.Any : ArtistReleaseQueries.Presence.InLibrary;
            return Results.Ok(await ArtistReleaseQueries.NewAsync(db, Today(time), window, kind, presence, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct));
        }).RequireAuthorization().WithTags("Discovery");

        app.MapGet("/api/artists/{id:guid}/releases", async (Guid id, SonNuageDbContext db, TimeProvider time, CancellationToken ct) =>
            await ArtistReleaseQueries.MissingForArtistAsync(db, id, Today(time), ct))
            .RequireAuthorization(AuthConstants.ContributorPolicy).WithTags("Discovery");

        // Seulement les parutions connues : le serveur ne relaie pas n'importe quelle pochette de l'archive.
        app.MapGet("/api/releases/{id:guid}/cover", async (Guid id, HttpContext http, SonNuageDbContext db, ReleaseCoverCache covers, CancellationToken ct) =>
        {
            if (!await db.ArtistReleases.AnyAsync(r => r.ReleaseGroupId == id, ct)) return Results.NotFound();
            if (await covers.GetAsync(id, ct) is not { } path) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=604800";
            return Results.File(path, LibraryFiles.ContentType(path));
        }).RequireAuthorization(AuthConstants.MediaPolicy).WithTags("Media");

        // Extrait de 30 s d'une parution connue, cherchée sur YouTube à la première écoute. Lu par un élément audio
        // (jeton média dans l'adresse) ; hors de la bibliothèque, donc contributeurs et admins.
        app.MapGet("/api/releases/{id:guid}/preview", async (Guid id, HttpContext http, SonNuageDbContext db, ReleasePreviews previews, CancellationToken ct) =>
        {
            var release = await db.ArtistReleases.AsNoTracking().FirstOrDefaultAsync(r => r.ReleaseGroupId == id, ct);
            if (release is null) return Results.NotFound();
            var artist = await db.Artists.Where(a => a.MusicBrainzId == release.ArtistMbid).Select(a => a.Name).FirstOrDefaultAsync(ct);
            if (artist is null) return Results.NotFound();
            try
            {
                if (await previews.GetAsync(id, artist, release.Title, ct) is not { } path)
                    return Results.Problem(Tx.Key("errors.preview.notFound"), statusCode: StatusCodes.Status404NotFound);
                http.Response.Headers.CacheControl = "private, max-age=3600";
                return Results.File(path, "audio/mpeg", enableRangeProcessing: true);
            }
            catch (Youtube.YtDlpException ex)
            {
                return Results.Problem(Tx.Key("errors.preview.failed", ("message", ex.Message)), statusCode: StatusCodes.Status502BadGateway);
            }
            catch (Storage.StorageFullException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status507InsufficientStorage);
            }
        }).RequireAuthorization(AuthConstants.ContributorMediaPolicy).WithTags("Discovery");

        app.MapPost("/api/admin/releases/refresh", async (IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await scopes.RequestAsync(ArtistReleasesJob.Plan(), ct);
            return Results.Accepted();
        }).RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");
    }

    /// <summary>Rubrique de l'accueil : sorties récentes absentes de la bibliothèque et sorties à venir ; pour un utilisateur simple, les récentes déjà là.</summary>
    public static Task<List<ArtistReleaseDto>> HomeAsync(SonNuageDbContext db, bool canRequest, TimeProvider time, CancellationToken ct) =>
        ArtistReleaseQueries.NewAsync(db, Today(time), canRequest ? null : ReleaseWindow.Recent, null,
            canRequest ? ArtistReleaseQueries.Presence.Missing : ArtistReleaseQueries.Presence.InLibrary, HomeCount, ct);

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static bool TryParseWindow(string? value, out ReleaseWindow? window)
    {
        window = value?.ToLowerInvariant() switch
        {
            null or "" => null,
            "recent" => ReleaseWindow.Recent,
            "upcoming" => ReleaseWindow.Upcoming,
            _ => (ReleaseWindow)(-1),
        };
        return window is null || Enum.IsDefined(window.Value);
    }

    private static bool TryParseKind(string? value, out ReleaseKind? kind)
    {
        kind = value?.ToLowerInvariant() switch
        {
            null or "" => null,
            "album" => ReleaseKind.Album,
            "ep" => ReleaseKind.Ep,
            "single" => ReleaseKind.Single,
            _ => (ReleaseKind)(-1),
        };
        return kind is null || Enum.IsDefined(kind.Value);
    }
}
