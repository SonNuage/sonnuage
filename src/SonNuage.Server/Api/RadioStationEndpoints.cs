using Microsoft.EntityFrameworkCore;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;
using SonNuage.Server.Auth;
using SonNuage.Server.Data;
using SonNuage.Server.Radio;

namespace SonNuage.Server.Api;

/// <summary>Radios thématiques (saisons, ambiances, météo) et leur réglage dans l'admin.</summary>
public static class RadioStationEndpoints
{
    public static void MapRadioStationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/radio/stations").RequireAuthorization().WithTags("Radio");

        g.MapGet("", async (HttpContext http, SonNuageDbContext db, RadioStations stations, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await stations.ListAsync(db, userId, await LanguageAsync(http, db, userId, ct), ct);
        });

        g.MapPost("/{id}/next", async (string id, StationNextRequest req, HttpContext http, SonNuageDbContext db, RadioStations stations, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (await stations.NextAsync(db, userId, id, req, ct) is not { } ids) return Results.NotFound();
            var tracks = await db.Tracks.Where(t => ids.Contains(t.Id)).Select(Projections.Track(db, userId)).ToDictionaryAsync(t => t.Id, ct);
            return Results.Ok(ids.Where(tracks.ContainsKey).Select(i => tracks[i]).ToList());
        });

        var admin = app.MapGroup("/api/admin/radio").RequireAuthorization(AuthConstants.AdminPolicy).WithTags("Admin");

        admin.MapGet("", (RadioSettingsStore settings) => settings.Settings);

        admin.MapPut("", async (RadioSettingsRequest req, HttpContext http, SonNuageDbContext db, RadioSettingsStore settings, IWeatherSource weather,
            CancellationToken ct) =>
        {
            var current = settings.Settings;
            RadioSettingsDto next;
            if (string.IsNullOrWhiteSpace(req.Place)) next = new RadioSettingsDto(null, null, null, req.WeatherEnabled);
            else if (req.Place.Trim() == current.Place) next = current with { WeatherEnabled = req.WeatherEnabled };
            else
            {
                WeatherPlace? place;
                try
                {
                    place = await weather.FindPlaceAsync(req.Place, await LanguageAsync(http, db, http.User.UserId(), ct), ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    return Results.Problem(Tx.Key("errors.radio.weatherUnavailable"), statusCode: StatusCodes.Status502BadGateway);
                }
                if (place is null) return Results.Problem(Tx.Key("errors.radio.placeNotFound", ("place", req.Place.Trim())), statusCode: StatusCodes.Status400BadRequest);
                next = new RadioSettingsDto(place.Name, place.Latitude, place.Longitude, req.WeatherEnabled);
            }
            settings.Save(next);
            return Results.Ok(next);
        });
    }

    private static async Task<string> LanguageAsync(HttpContext http, SonNuageDbContext db, Guid userId, CancellationToken ct)
    {
        var mine = await db.Users.Where(u => u.Id == userId).Select(u => u.Language).FirstOrDefaultAsync(ct);
        return Translations.Instance.Resolve(mine, http.Request.Headers.AcceptLanguage.ToString());
    }
}
