using SonNuage.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace SonNuage.Server.Library;

/// <summary>
/// Rattache les pistes aux albums/artistes. Garde un cache des entités déjà chargées
/// pour qu'un scan de milliers de fichiers ne fasse pas une requête par piste.
/// </summary>
public sealed class LibraryIndexer(SonNuageDbContext db)
{
    private readonly Dictionary<string, Artist> _artists = new();
    private readonly Dictionary<(Guid, string), Album> _albums = new();
    private bool _loaded;

    public HashSet<Guid> TouchedAlbums { get; } = [];

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        foreach (var a in await db.Artists.ToListAsync(ct)) _artists[a.NormalizedName] = a;
        foreach (var al in await db.Albums.ToListAsync(ct)) _albums[(al.ArtistId, al.NormalizedTitle)] = al;
        _loaded = true;
    }

    public void ApplyMetadata(Track track, AudioMetadata meta)
    {
        track.DurationSeconds = meta.DurationSeconds;
        track.BitrateKbps = meta.BitrateKbps;
        track.SampleRate = meta.SampleRate;
        // Un identifiant reconnu par AcoustID (fichier en lecture seule, tags non réécrits) reste si les tags n'en ont pas.
        track.MusicBrainzRecordingId = meta.RecordingId ?? track.MusicBrainzRecordingId;
        track.MusicBrainzReleaseId = meta.ReleaseId ?? track.MusicBrainzReleaseId;
        if (track.MetadataLocked) return;

        track.Title = meta.Title;
        track.ArtistName = meta.Artist;
        track.AlbumArtistName = meta.AlbumArtist;
        track.AlbumTitle = meta.Album;
        track.TrackNumber = meta.TrackNumber;
        track.DiscNumber = meta.DiscNumber;
        track.Year = meta.Year;
        track.Genre = meta.Genre;
    }

    /// <summary>Place la piste dans le bon album (créé au besoin) selon AlbumArtistName + AlbumTitle.</summary>
    public void Assign(Track track)
    {
        var albumArtist = GetOrCreateArtist(track.AlbumArtistName);
        var trackArtist = GetOrCreateArtist(track.ArtistName);
        var album = GetOrCreateAlbum(albumArtist, track.AlbumTitle);

        if (track.AlbumId != album.Id && track.AlbumId != Guid.Empty) TouchedAlbums.Add(track.AlbumId);
        track.Album = album;
        track.AlbumId = album.Id;
        track.Artist = trackArtist;
        track.ArtistId = trackArtist.Id;
        track.SearchKey = $"{NameNormalizer.Key(track.Title)} {NameNormalizer.Key(track.ArtistName)} {album.NormalizedTitle} {albumArtist.NormalizedName}";
        if (album.Year is null && track.Year is not null) album.Year = track.Year;
        album.MusicBrainzReleaseId ??= track.MusicBrainzReleaseId;
        TouchedAlbums.Add(album.Id);
    }

    public Artist GetOrCreateArtist(string name)
    {
        var key = NameNormalizer.Key(name);
        if (_artists.TryGetValue(key, out var artist)) return artist;
        artist = new Artist { Name = name.Trim(), NormalizedName = key, SortName = NameNormalizer.SortName(name.Trim()) };
        db.Artists.Add(artist);
        _artists[key] = artist;
        return artist;
    }

    private Album GetOrCreateAlbum(Artist artist, string title)
    {
        var key = NameNormalizer.Key(title);
        if (_albums.TryGetValue((artist.Id, key), out var album)) return album;
        album = new Album { Title = title.Trim(), NormalizedTitle = key, ArtistId = artist.Id, Artist = artist };
        db.Albums.Add(album);
        _albums[(artist.Id, key)] = album;
        return album;
    }

    /// <summary>Supprime albums et artistes devenus vides.</summary>
    /// <summary>Supprime les albums sans titre, puis les artistes sans album ni titre. Renvoie le nombre supprimé.</summary>
    public static async Task<int> PruneAsync(SonNuageDbContext db, CancellationToken ct)
    {
        var albums = await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        var artists = await db.Artists
            .Where(ar => !ar.Albums.Any() && !db.Tracks.Any(t => t.ArtistId == ar.Id))
            .ExecuteDeleteAsync(ct);
        return albums + artists;
    }
}
