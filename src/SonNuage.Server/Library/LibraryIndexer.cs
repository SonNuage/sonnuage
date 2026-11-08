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
    private readonly Dictionary<Guid, HashSet<Guid>> _guests = new();
    private bool _loaded;

    public HashSet<Guid> TouchedAlbums { get; } = [];

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        foreach (var a in await db.Artists.ToListAsync(ct)) _artists[a.NormalizedName] = a;
        foreach (var al in await db.Albums.ToListAsync(ct)) _albums[(al.ArtistId, al.NormalizedTitle)] = al;
        foreach (var g in await db.TrackGuests.AsNoTracking().ToListAsync(ct)) GuestsOf(g.TrackId).Add(g.ArtistId);
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
        var album = GetOrCreateAlbum(albumArtist, track.AlbumTitle);

        if (track.AlbumId != album.Id && track.AlbumId != Guid.Empty) TouchedAlbums.Add(track.AlbumId);
        track.Album = album;
        track.AlbumId = album.Id;
        LinkCredits(track);
        track.SearchKey = $"{NameNormalizer.Key(track.Title)} {NameNormalizer.Key(track.ArtistName)} {album.NormalizedTitle} {albumArtist.NormalizedName}";
        if (album.Year is null && track.Year is not null) album.Year = track.Year;
        album.MusicBrainzReleaseId ??= track.MusicBrainzReleaseId;
        TouchedAlbums.Add(album.Id);
    }

    /// <summary>
    /// Rattache le morceau à l'artiste principal de son crédit et enregistre ses invités (voir <see cref="ArtistCredit"/>).
    /// Ne change rien si c'est déjà fait : rejouable sur toute la bibliothèque.
    /// </summary>
    public void LinkCredits(Track track)
    {
        var credit = ArtistCredit.Parse(track.ArtistName, track.AlbumArtistName, track.Title);
        var main = GetOrCreateArtist(credit.Main);
        if (track.ArtistId != main.Id)
        {
            track.Artist = main;
            track.ArtistId = main.Id;
        }

        var wanted = credit.Guests.Select(g => GetOrCreateArtist(g).Id).ToHashSet();
        var current = GuestsOf(track.Id);
        foreach (var gone in current.Except(wanted).ToList())
        {
            // Déjà suivie (ajoutée dans ce lot) ou seulement connue en base : on supprime la même instance.
            var row = db.TrackGuests.Local.FirstOrDefault(g => g.TrackId == track.Id && g.ArtistId == gone)
                      ?? new TrackGuest { TrackId = track.Id, ArtistId = gone };
            db.TrackGuests.Remove(row);
            current.Remove(gone);
        }
        foreach (var added in wanted.Except(current).ToList())
        {
            db.TrackGuests.Add(new TrackGuest { TrackId = track.Id, ArtistId = added });
            current.Add(added);
        }
    }

    /// <summary>Rattache aux bons artistes les morceaux indexés avant (scan incrémental : leur fichier n'est pas relu).</summary>
    public async Task RelinkCreditsAsync(CancellationToken ct)
    {
        foreach (var track in await db.Tracks.ToListAsync(ct)) LinkCredits(track);
    }

    private HashSet<Guid> GuestsOf(Guid trackId) =>
        _guests.TryGetValue(trackId, out var set) ? set : _guests[trackId] = [];

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

    /// <summary>Supprime les albums sans titre, puis les artistes sans album, sans titre et invités nulle part. Renvoie le nombre supprimé.</summary>
    public static async Task<int> PruneAsync(SonNuageDbContext db, CancellationToken ct)
    {
        var albums = await db.Albums.Where(a => !a.Tracks.Any()).ExecuteDeleteAsync(ct);
        var artists = await db.Artists
            .Where(ar => !ar.Albums.Any() && !ar.Tracks.Any() && !db.TrackGuests.Any(g => g.ArtistId == ar.Id))
            .ExecuteDeleteAsync(ct);
        return albums + artists;
    }
}
