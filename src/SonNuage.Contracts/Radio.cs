namespace SonNuage.Contracts;

/// <summary>
/// Radio thématique tirée de la bibliothèque : saison (Halloween, Noël, été…), ambiance (pop bonne humeur, calme…)
/// ou météo du moment. Les radios de saison n'apparaissent qu'à leur période.
/// </summary>
/// <param name="Id">Identifiant stable : « halloween », « good-mood-pop », « weather »…</param>
/// <param name="Kind">« season », « mood » ou « weather ».</param>
/// <param name="Name">Nom dans la langue du compte.</param>
/// <param name="Description">Une ligne d'explication ; pour la météo, le temps qu'il fait (« Pluie, 9 °C à Lyon »).</param>
/// <param name="CoverAlbumIds">Jusqu'à quatre albums représentatifs, pour la vignette (pochettes en mosaïque).</param>
/// <param name="Color">Couleur de la vignette, « #rrggbb ».</param>
public sealed record RadioStationDto(string Id, string Kind, string Name, string Description, IReadOnlyList<Guid> CoverAlbumIds, string Color);

/// <summary>Suite d'une radio thématique : <paramref name="Count"/> morceaux, jamais un de <paramref name="ExcludeTrackIds"/>.</summary>
public sealed record StationNextRequest(IReadOnlyList<Guid> ExcludeTrackIds, int Count);

public static class RadioStationKinds
{
    public const string Season = "season";
    public const string Mood = "mood";
    public const string Weather = "weather";
}
