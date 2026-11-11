using SonNuage.Contracts;
using SonNuage.Server.Radio;

namespace SonNuage.Server.Tests;

/// <summary>Stations de saison, d'ambiance et de météo : qui choisit quels morceaux.</summary>
public sealed class RadioCatalogTests
{
    private static TrackProfile Track(string title, string? album = null, string? genre = null, AudioFeel? feel = null, string[]? tags = null,
        Guid? artist = null) =>
        TrackProfile.Create(Guid.NewGuid(), artist ?? Guid.NewGuid(), Guid.NewGuid(), true, 200, title, album, genre, tags, feel);

    private static StationDefinition Station(string id) => StationCatalog.Find(id)!;

    private static bool Plays(string station, TrackProfile p) => Station(station).Score(p) >= Station(station).Threshold;

    // Valeurs mesurées par Essentia (emoMusic) : l'éveil et la valence restent entre 0,35 et 0,75 environ.
    [Theory]
    [InlineData(0.71, 0.85, 1.0)]
    [InlineData(0.34, 0.0, 0.1)]
    [InlineData(0.55, 0.45, 0.55)]
    [InlineData(0.1, 0.0, 0.0)]
    [InlineData(0.95, 1.0, 1.0)]
    public void Measured_energy_and_valence_are_stretched_to_the_whole_scale(double measured, double min, double max)
    {
        var feel = AudioFeel.FromAnalysis(Energy: measured, Valence: measured);
        Assert.InRange(feel.Energy!.Value, min, max);
        Assert.InRange(feel.Valence!.Value, min, max);
    }

    [Fact]
    public void A_measured_dark_and_tense_song_reaches_halloween() =>
        Assert.True(Plays("halloween", Track("Song", feel: AudioFeel.FromAnalysis(Valence: 0.33, Aggressive: 0.9, Energy: 0.72))));

    [Theory]
    [InlineData(10, 31, true)]
    [InlineData(11, 2, true)]
    [InlineData(11, 3, false)]
    [InlineData(10, 9, false)]
    public void Halloween_shows_from_mid_october_to_early_november(int month, int day, bool shown) =>
        Assert.Equal(shown, Station("halloween").Window!.Contains(new DateOnly(2026, month, day)));

    [Theory]
    [InlineData(12, 24, true)]
    [InlineData(1, 6, true)]
    [InlineData(1, 7, false)]
    [InlineData(11, 30, false)]
    public void Christmas_spans_the_new_year(int month, int day, bool shown) =>
        Assert.Equal(shown, Station("christmas").Window!.Contains(new DateOnly(2026, month, day)));

    [Fact]
    public void Christmas_songs_are_found_by_title_album_or_musicbrainz_tag()
    {
        Assert.True(Plays("christmas", Track("Petit Papa Noël")));
        Assert.True(Plays("christmas", Track("Last Christmas")));
        Assert.True(Plays("christmas", Track("Song", album: "A Very Special Xmas")));
        Assert.True(Plays("christmas", Track("All I Want for You", tags: ["christmas music"])));
        Assert.False(Plays("christmas", Track("Noelle's Song")));
        Assert.False(Plays("christmas", Track("Highway to Hell", genre: "Hard Rock")));
    }

    [Fact]
    public void Halloween_takes_spooky_titles_and_dark_intense_sounds()
    {
        Assert.True(Plays("halloween", Track("Thriller")));
        Assert.True(Plays("halloween", Track("Ghost Town")));
        Assert.True(Plays("halloween", Track("Song", feel: new AudioFeel(Valence: 0.1, Aggressive: 0.9, Energy: 0.9))));
        Assert.False(Plays("halloween", Track("Song", feel: new AudioFeel(Valence: 0.8, Aggressive: 0.9, Energy: 0.9))));
        Assert.False(Plays("halloween", Track("Ghostwriter")));
    }

    [Fact]
    public void Good_mood_pop_takes_happy_songs_once_they_are_analyzed()
    {
        // Sans analyse, le genre seul ne dit rien de l'humeur : une ballade triste est aussi de la « pop ».
        Assert.False(Plays("good-mood-pop", Track("Song", genre: "Pop")));
        Assert.True(Plays("good-mood-pop", Track("Song", genre: "Pop", feel: new AudioFeel(Happy: 0.8, Valence: 0.7, Danceability: 0.6))));
        Assert.False(Plays("good-mood-pop", Track("Song", genre: "Pop", feel: new AudioFeel(Happy: 0.05, Sad: 0.9, Valence: 0.1))));
        Assert.False(Plays("good-mood-pop", Track("Song", genre: "Metal")));
    }

    [Fact]
    public void Mood_stations_wait_for_the_audio_analysis()
    {
        var unknown = Track("Song", genre: "Ambient");
        Assert.False(Plays("chill", unknown));
        Assert.True(Plays("chill", Track("Song", feel: new AudioFeel(Relaxed: 0.9, Energy: 0.1, Acoustic: 0.8, Aggressive: 0.05, Sad: 0.1))));
        Assert.False(Plays("chill", Track("Song", feel: new AudioFeel(Relaxed: 0.1, Energy: 0.95, Aggressive: 0.9))));
        Assert.True(Plays("energy", Track("Song", feel: new AudioFeel(Energy: 0.9, Danceability: 0.8, Bpm: 128))));
        Assert.True(Plays("focus", Track("Song", feel: new AudioFeel(Instrumental: 0.95, Relaxed: 0.7, Energy: 0.3, Aggressive: 0.1))));
        Assert.True(Plays("melancholy", Track("Song", feel: new AudioFeel(Sad: 0.9, Valence: 0.1, Energy: 0.2, Happy: 0.1, Party: 0.1))));
    }

    // Les classifieurs « calme », « triste » et « acoustique » vont ensemble : sans ces écarts, détente, mélancolie et
    // automne jouaient presque la même liste.
    [Fact]
    public void Chill_is_calm_but_not_sad()
    {
        Assert.True(Plays("chill", Track("Song", feel: new AudioFeel(Relaxed: 0.8, Energy: 0.2, Aggressive: 0.05, Sad: 0.2))));
        Assert.False(Plays("chill", Track("Song", feel: new AudioFeel(Relaxed: 0.9, Energy: 0.1, Acoustic: 0.9, Aggressive: 0.05, Sad: 0.9))));
    }

    [Fact]
    public void Melancholy_leaves_out_what_also_sounds_happy_and_festive()
    {
        Assert.False(Plays("melancholy", Track("Song", feel: new AudioFeel(Sad: 0.7, Valence: 0.3, Energy: 0.2, Happy: 0.8, Party: 0.8))));
    }

    [Fact]
    public void Autumn_is_acoustic_and_warm_rather_than_just_sad()
    {
        Assert.True(Plays("autumn", Track("Song", feel: new AudioFeel(Acoustic: 0.9, Relaxed: 0.8, Energy: 0.2, Valence: 0.6, Sad: 0.5))));
        Assert.False(Plays("autumn", Track("Song", feel: new AudioFeel(Acoustic: 0.2, Relaxed: 0.9, Energy: 0.1, Valence: 0.1, Sad: 0.95))));
    }

    [Fact]
    public void Of_two_equally_happy_pop_songs_the_less_sad_one_ranks_first()
    {
        var bright = Track("A", genre: "Pop", feel: new AudioFeel(Happy: 0.7, Valence: 0.6, Danceability: 0.6, Sad: 0.1));
        var bittersweet = Track("B", genre: "Pop", feel: new AudioFeel(Happy: 0.7, Valence: 0.6, Danceability: 0.6, Sad: 0.9));

        Assert.True(Station("good-mood-pop").Score(bright) > Station("good-mood-pop").Score(bittersweet));
    }

    [Theory]
    [InlineData(0, 25, true, WeatherCategory.Sunny)]
    [InlineData(0, 8, true, WeatherCategory.Bright)]
    [InlineData(0, 25, false, WeatherCategory.Bright)]
    [InlineData(3, 12, true, WeatherCategory.Cloudy)]
    [InlineData(61, 9, true, WeatherCategory.Rain)]
    [InlineData(81, 9, true, WeatherCategory.Rain)]
    [InlineData(73, -2, true, WeatherCategory.Snow)]
    [InlineData(95, 20, true, WeatherCategory.Storm)]
    [InlineData(45, 5, true, WeatherCategory.Fog)]
    public void Weather_codes_map_to_a_mood(int code, double temp, bool day, WeatherCategory expected) =>
        Assert.Equal(expected, StationCatalog.Category(new WeatherNow(code, temp, day, "Lyon")));

    [Fact]
    public void Rainy_weather_plays_calm_acoustic_songs_and_rain_songs()
    {
        var rain = StationCatalog.ForWeather(new WeatherNow(61, 9, true, "Lyon"));
        Assert.Equal(RadioStationKinds.Weather, rain.Kind);
        Assert.True(rain.Score(Track("Song", feel: new AudioFeel(Acoustic: 0.9, Relaxed: 0.9, Sad: 0.6, Energy: 0.1))) >= rain.Threshold);
        Assert.True(rain.Score(Track("Song", feel: new AudioFeel(Acoustic: 0.5, Relaxed: 0.6, Energy: 0.3))) <
                    rain.Score(Track("Purple Rain", feel: new AudioFeel(Acoustic: 0.5, Relaxed: 0.6, Energy: 0.3))));
        Assert.True(rain.Score(Track("Song", feel: new AudioFeel(Acoustic: 0.05, Relaxed: 0.1, Energy: 0.95, Aggressive: 0.9))) < rain.Threshold);
    }

    [Fact]
    public void Picking_spreads_artists_and_skips_excluded_tracks()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var candidates = Enumerable.Range(0, 6).Select(i => new RadioStations.Candidate(Track($"A{i}", artist: a), 0.9))
            .Concat(Enumerable.Range(0, 6).Select(i => new RadioStations.Candidate(Track($"B{i}", artist: b), 0.9)))
            .Concat(Enumerable.Range(0, 6).Select(i => new RadioStations.Candidate(Track($"C{i}"), 0.7)))
            .ToList();
        var excluded = candidates.Take(3).Select(c => c.Profile.Id).ToHashSet();

        var ids = RadioStations.Pick(candidates, excluded, 6, new Random(1));

        Assert.Equal(6, ids.Distinct().Count());
        Assert.DoesNotContain(ids, excluded.Contains);
        var artists = ids.Select(id => candidates.First(c => c.Profile.Id == id).Profile.ArtistId).ToList();
        Assert.True(artists.Count(x => x == a) <= 2 && artists.Count(x => x == b) <= 2);
        Assert.All(artists.Zip(artists.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void Picking_starts_over_when_everything_was_played()
    {
        var candidates = Enumerable.Range(0, 3).Select(i => new RadioStations.Candidate(Track($"S{i}"), 0.8)).ToList();

        var ids = RadioStations.Pick(candidates, candidates.Select(c => c.Profile.Id).ToHashSet(), 3, new Random(2));

        Assert.Equal(3, ids.Count);
    }

    private static List<TrackProfile> SadSongs(int count) => Enumerable.Range(0, count)
        .Select(i => Track($"S{i}", feel: new AudioFeel(Sad: 0.7 + 0.3 * i / count, Valence: 0.1, Energy: 0.1, Happy: 0.1, Party: 0.1))).ToList();

    [Fact]
    public void A_station_keeps_only_the_best_share_of_the_songs_it_can_judge()
    {
        // Tous passent le seuil ; sans coupe la station serait la moitié de la bibliothèque.
        var songs = SadSongs(1000);

        var kept = RadioStations.Candidates(Station("melancholy"), songs, RadioStations.Personal.None);

        Assert.Equal(120, kept.Count);
        Assert.Equal(songs.Skip(880).Select(p => p.Id).Order(), kept.Select(c => c.Profile.Id).Order());
    }

    [Fact]
    public void A_small_library_keeps_enough_songs_for_a_station()
    {
        var kept = RadioStations.Candidates(Station("melancholy"), SadSongs(200), RadioStations.Personal.None);

        Assert.Equal(60, kept.Count);
    }

    [Fact]
    public void Songs_that_match_equally_all_stay()
    {
        // Un titre de Noël en est un : la coupe ne tire pas au sort parmi des notes égales.
        var songs = Enumerable.Range(0, 300).Select(i => Track($"Christmas {i}")).ToList();

        Assert.Equal(300, RadioStations.Candidates(Station("christmas"), songs, RadioStations.Personal.None).Count);
    }

    [Theory]
    [InlineData("Surrender", "Surrender (Ultamix)")]
    [InlineData("Golden Slumbers", "Golden Slumbers (2019 stereo mix)")]
    [InlineData("Yo sí", "Yo sí - Dave Audé Remix")]
    [InlineData("La soledad", "La soledad (2013)")]
    [InlineData("Song", "Song [Live at Wembley]")]
    [InlineData("Song", "Song (feat. Someone)")]
    [InlineData("Song", "SONG (Version acoustique)")]
    public void Versions_of_a_song_are_the_same_song(string song, string version) =>
        Assert.Equal(TrackProfile.SongOf(song), TrackProfile.SongOf(version));

    [Theory]
    [InlineData("Peace: A Theme", "Peace: An End")]
    [InlineData("Larks' Tongues in Aspic (Part I)", "Larks' Tongues in Aspic (Part II)")]
    [InlineData("Moi sì (Io sì)", "Yo sí (Io sì)")]
    [InlineData("Sgt. Pepper's Lonely Hearts Club Band", "Sgt. Pepper's Lonely Hearts Club Band (reprise)")]
    [InlineData("Up - Down", "Up")]
    [InlineData("(Live)", "(Remix)")]
    public void Other_songs_stay_apart(string one, string other) =>
        Assert.NotEqual(TrackProfile.SongOf(one), TrackProfile.SongOf(other));

    [Fact]
    public void A_station_plays_one_version_of_a_song_the_best_match()
    {
        var artist = Guid.NewGuid();
        var sad = new AudioFeel(Sad: 0.8, Valence: 0.1, Energy: 0.1, Happy: 0.1, Party: 0.1);
        var album = Track("Dark Vacay", feel: sad, artist: artist);
        var live = Track("Dark Vacay (live)", feel: sad with { Sad = 0.95 }, artist: artist);
        var remix = Track("Dark Vacay - Night Remix", feel: sad with { Sad = 0.7 }, artist: artist);
        var cover = Track("Dark Vacay", feel: sad);

        var kept = RadioStations.Candidates(Station("melancholy"), [album, live, remix, cover], RadioStations.Personal.None);

        Assert.Equal(new[] { live.Id, cover.Id }.Order(), kept.Select(c => c.Profile.Id).Order());
    }

    [Fact]
    public void Account_ratings_hide_disliked_songs_and_favor_liked_ones()
    {
        var happy = new AudioFeel(Happy: 0.8, Valence: 0.7, Danceability: 0.6);
        var liked = Track("Song", genre: "Pop", feel: happy);
        var disliked = Track("Song", genre: "Pop", feel: happy);
        var albumDisliked = Track("Song", genre: "Pop", feel: happy);
        var personal = new RadioStations.Personal(new HashSet<Guid> { liked.Id },
            new Dictionary<Guid, int> { [disliked.Id] = 1, [albumDisliked.AlbumId] = 2 });

        var candidates = RadioStations.Candidates(Station("good-mood-pop"), [liked, disliked, albumDisliked], personal);

        var only = Assert.Single(candidates);
        Assert.Equal(liked.Id, only.Profile.Id);
        Assert.True(only.Score > Station("good-mood-pop").Score(liked));
    }
}
