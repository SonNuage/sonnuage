using AndroidX.Media3.Common;
using Localify.Contracts;
using Localify.UI.Services;

namespace Localify.Mobile.Playback;

/// <summary>
/// Identifiants de l'arborescence Android Auto. Une piste listée dans un contexte porte ce contexte
/// (« track:{id}@album:{id} ») pour qu'un appui lance tout l'album à partir de cette piste.
/// </summary>
public static class BrowseIds
{
    public const string Root = "root";
    public const string Playlists = "playlists";
    public const string Albums = "albums";
    public const string Artists = "artists";
    public const string Recent = "recent";
    public const string Favorites = "favorites";
    public const string ShuffleAll = "shuffle";

    public static string Track(Guid id) => $"track:{id:N}";
    public static string Album(Guid id) => $"album:{id:N}";
    public static string Artist(Guid id) => $"artist:{id:N}";
    public static string Playlist(Guid id) => $"playlist:{id:N}";
    public static string InContext(Guid trackId, string context) => $"{Track(trackId)}@{context}";

    public static (string Kind, Guid Id) Parse(string id)
    {
        var colon = id.IndexOf(':');
        return colon < 0 ? (id, Guid.Empty) : (id[..colon], Guid.TryParse(id[(colon + 1)..], out var g) ? g : Guid.Empty);
    }
}

/// <summary>Arborescence de navigation Android Auto et résolution des identifiants en pistes à jouer.</summary>
public sealed class LibraryBrowser(ApiClient api, AuthSession auth)
{
    private const int ListLimit = 200;

    public async Task<bool> EnsureSignedInAsync()
    {
        await auth.EnsureLoadedAsync();
        return auth.IsAuthenticated;
    }

    public MediaItem RootItem() => Folder(BrowseIds.Root, "Localify", MediaMetadata.MediaTypeFolderMixed);

    public async Task<IList<MediaItem>> ChildrenAsync(string parentId)
    {
        if (!await EnsureSignedInAsync()) return [];
        var (kind, id) = BrowseIds.Parse(parentId);
        switch (kind)
        {
            case BrowseIds.Root:
                // Android Auto affiche les 4 premiers dossiers de la racine comme onglets.
                return
                [
                    Folder(BrowseIds.Playlists, "Playlists", MediaMetadata.MediaTypeFolderPlaylists),
                    Folder(BrowseIds.Albums, "Albums", MediaMetadata.MediaTypeFolderAlbums),
                    Folder(BrowseIds.Artists, "Artistes", MediaMetadata.MediaTypeFolderArtists),
                    Folder(BrowseIds.Recent, "Récents", MediaMetadata.MediaTypeFolderMixed),
                ];
            case BrowseIds.Playlists:
            {
                var items = new List<MediaItem>
                {
                    Playable(BrowseIds.ShuffleAll, "Lecture aléatoire", "Toute la bibliothèque", null, MediaMetadata.MediaTypePlaylist),
                    Container(BrowseIds.Favorites, "Titres likés", null, null, MediaMetadata.MediaTypePlaylist),
                };
                foreach (var p in await api.PlaylistsAsync() ?? [])
                {
                    items.Add(Container(BrowseIds.Playlist(p.Id), p.Name, $"{p.TrackCount} titres", api.CoverUrl(p.CoverAlbumId, 300), MediaMetadata.MediaTypePlaylist));
                }
                return items;
            }
            case BrowseIds.Albums:
            {
                var page = await api.AlbumsAsync("recent", 0, ListLimit);
                return (page?.Items ?? []).Select(AlbumItem).ToList();
            }
            case BrowseIds.Artists:
            {
                var page = await api.ArtistsAsync(0, ListLimit);
                return (page?.Items ?? []).Select(a => Container(BrowseIds.Artist(a.Id), a.Name, $"{a.AlbumCount} albums",
                    api.CoverUrl(a.CoverAlbumId, 300), MediaMetadata.MediaTypeArtist)).ToList();
            }
            case "artist":
            {
                var artist = await api.ArtistAsync(id);
                if (artist is null) return [];
                var items = new List<MediaItem> { Playable(parentId, "Tout lire", artist.Artist.Name, null, MediaMetadata.MediaTypePlaylist) };
                items.AddRange(artist.Albums.Select(AlbumItem));
                return items;
            }
            default:
                return (await TracksAsync(parentId)).Select(t => TrackItem(t, parentId)).ToList();
        }
    }

    public async Task<IList<MediaItem>> SearchAsync(string query)
    {
        if (!await EnsureSignedInAsync() || string.IsNullOrWhiteSpace(query)) return [];
        var result = await api.SearchAsync(query, 30);
        if (result is null) return [];
        var items = new List<MediaItem>();
        items.AddRange(result.Tracks.Select(t => TrackItem(t, null)));
        items.AddRange(result.Albums.Select(AlbumItem));
        items.AddRange(result.Artists.Select(a => Container(BrowseIds.Artist(a.Id), a.Name, "Artiste", api.CoverUrl(a.CoverAlbumId, 300), MediaMetadata.MediaTypeArtist)));
        return items;
    }

    /// <summary>
    /// Transforme ce que demande un contrôleur (Android Auto, Assistant, l'appli) en file jouable.
    /// Les éléments venant de l'appli portent déjà la piste complète ; les autres sont des identifiants à charger.
    /// </summary>
    public async Task<(List<MediaItem> Items, int StartIndex)> ResolveAsync(IList<MediaItem> requested, int startIndex)
    {
        if (!await EnsureSignedInAsync()) return ([], 0);
        if (requested.Count == 1 && TrackItems.ToTrack(requested[0]) is null)
        {
            var mediaId = requested[0].MediaId ?? "";
            var search = TrackItems.SearchQuery(requested[0]);
            if (mediaId.Length == 0 && search is not null)
            {
                // « Ok Google, mets <requête> sur Localify »
                var found = await api.SearchAsync(search, 50);
                var tracks = found?.Tracks.ToList() ?? [];
                if (tracks.Count == 0 && found?.Albums.FirstOrDefault() is { } album) tracks = await TracksAsync(BrowseIds.Album(album.Id));
                return (tracks.Select(Item).ToList(), 0);
            }
            var at = mediaId.IndexOf('@');
            if (at > 0)
            {
                var context = await TracksAsync(mediaId[(at + 1)..]);
                var trackId = BrowseIds.Parse(mediaId[..at]).Id;
                return (context.Select(Item).ToList(), Math.Max(0, context.FindIndex(t => t.Id == trackId)));
            }
            if (!mediaId.StartsWith("track:", StringComparison.Ordinal))
            {
                var tracks = await TracksAsync(mediaId);
                return (tracks.Select(Item).ToList(), 0);
            }
        }

        var resolved = new List<MediaItem>(requested.Count);
        var missing = new List<Guid>();
        foreach (var item in requested)
        {
            if (TrackItems.ToTrack(item) is { } track) resolved.Add(Item(track));
            else if (BrowseIds.Parse(item.MediaId ?? "") is ("track", var id)) missing.Add(id);
        }
        if (missing.Count > 0)
        {
            resolved.AddRange((await api.LookupTracksAsync(missing) ?? []).Select(Item));
        }
        return (resolved, Math.Clamp(startIndex, 0, Math.Max(0, resolved.Count - 1)));
    }

    public MediaItem Item(TrackDto track) => TrackItems.From(track, api.CoverUrl(track.HasCover ? track.AlbumId : null, 600));

    private async Task<List<TrackDto>> TracksAsync(string contextId)
    {
        var (kind, id) = BrowseIds.Parse(contextId);
        return kind switch
        {
            "album" => (await api.AlbumAsync(id))?.Tracks.ToList() ?? [],
            "artist" => await api.ArtistTracksAsync(id) ?? [],
            "playlist" => (await api.PlaylistAsync(id))?.Entries.Select(e => e.Track).ToList() ?? [],
            BrowseIds.Favorites => await api.FavoritesAsync() ?? [],
            BrowseIds.Recent => await api.RecentAsync(50) ?? [],
            BrowseIds.ShuffleAll => await api.RandomTracksAsync(100) ?? [],
            _ => [],
        };
    }

    private MediaItem AlbumItem(AlbumDto a) =>
        Container(BrowseIds.Album(a.Id), a.Title, a.ArtistName, api.CoverUrl(a.HasCover ? a.Id : null, 300), MediaMetadata.MediaTypeAlbum);

    private MediaItem TrackItem(TrackDto t, string? context)
    {
        var item = Item(t);
        return context is null ? item : item.BuildUpon()!.SetMediaId(BrowseIds.InContext(t.Id, context))!.Build()!;
    }

    private static MediaItem Folder(string id, string title, int mediaType) => Build(id, title, null, null, mediaType, browsable: true, playable: false);
    private static MediaItem Container(string id, string title, string? subtitle, string? artwork, int mediaType) => Build(id, title, subtitle, artwork, mediaType, browsable: true, playable: true);
    private static MediaItem Playable(string id, string title, string? subtitle, string? artwork, int mediaType) => Build(id, title, subtitle, artwork, mediaType, browsable: false, playable: true);

    private static MediaItem Build(string id, string title, string? subtitle, string? artwork, int mediaType, bool browsable, bool playable)
    {
        var metadata = new MediaMetadata.Builder()
            .SetTitle(title)!
            .SetSubtitle(subtitle)!
            .SetArtist(subtitle)!
            .SetArtworkUri(artwork is null ? null : Android.Net.Uri.Parse(artwork))!
            .SetIsBrowsable(Java.Lang.Boolean.ValueOf(browsable))!
            .SetIsPlayable(Java.Lang.Boolean.ValueOf(playable))!
            .SetMediaType(Java.Lang.Integer.ValueOf(mediaType))!
            .Build();
        return new MediaItem.Builder().SetMediaId(id)!.SetMediaMetadata(metadata)!.Build()!;
    }
}
