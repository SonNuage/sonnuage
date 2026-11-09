using SonNuage.Contracts;

namespace SonNuage.UI.Services;

public sealed record Toast(Guid Id, string Message, bool IsError);

/// <summary>État d'interface partagé : notifications, liste des playlists (menu « ajouter à… »), favoris.</summary>
public sealed class UiState(ApiClient api, IPlayer player, Localizer L)
{
    private readonly List<Toast> _toasts = [];

    public IReadOnlyList<Toast> Toasts => _toasts;
    public IReadOnlyList<PlaylistDto> Playlists { get; private set; } = [];
    public bool NowPlayingOpen { get; private set; }

    public event Action? Changed;

    public void Notify(string message, bool isError = false)
    {
        var toast = new Toast(Guid.NewGuid(), message, isError);
        _toasts.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(isError ? 6000 : 3000).ContinueWith(_ =>
        {
            _toasts.Remove(toast);
            Changed?.Invoke();
        });
    }

    public void SetNowPlayingOpen(bool open)
    {
        NowPlayingOpen = open;
        Changed?.Invoke();
    }

    /// <summary>Le plein écran montre les paroles à la place de la pochette.</summary>
    public bool LyricsShown { get; private set; }

    public void SetLyricsShown(bool shown)
    {
        LyricsShown = shown;
        Changed?.Invoke();
    }

    /// <summary>Bouton « Paroles » de la barre du lecteur : le plein écran s'ouvre sur les paroles.</summary>
    public void OpenLyrics()
    {
        LyricsShown = true;
        NowPlayingOpen = true;
        Changed?.Invoke();
    }

    public async Task RefreshPlaylistsAsync()
    {
        try
        {
            Playlists = await api.PlaylistsAsync() ?? [];
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
        }
    }

    public async Task<TrackDto> ToggleFavoriteAsync(TrackDto track)
    {
        var updated = track with { IsFavorite = !track.IsFavorite };
        try
        {
            await api.SetFavoriteAsync(track.Id, updated.IsFavorite);
            player.UpdateTrack(updated);
            Notify(L[updated.IsFavorite ? "toast.favoriteAdded" : "toast.favoriteRemoved"]);
            return updated;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(L.Server(ex.Message), true);
            return track;
        }
    }

    public async Task AddToPlaylistAsync(Guid playlistId, IReadOnlyList<Guid> trackIds)
    {
        try
        {
            await api.AddToPlaylistAsync(playlistId, trackIds);
            var name = Playlists.FirstOrDefault(p => p.Id == playlistId)?.Name ?? L["toast.thePlaylist"];
            Notify(trackIds.Count == 1 ? L["toast.addedTo", ("name", name)] : L["toast.addedManyTo", ("count", trackIds.Count), ("name", name)]);
            await RefreshPlaylistsAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(L.Server(ex.Message), true);
        }
    }

    public async Task<PlaylistDto?> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds)
    {
        try
        {
            var created = await api.CreatePlaylistAsync(name, trackIds);
            Notify(L["toast.playlistCreated", ("name", name)]);
            await RefreshPlaylistsAsync();
            return created;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(L.Server(ex.Message), true);
            return null;
        }
    }
}

/// <summary>Durées et tailles dans la langue de l'interface.</summary>
public static class Format
{
    private static Localizer? L => Localizer.Current;

    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string LongDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (L is not { } l) return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
        return t.TotalHours >= 1
            ? l["format.hoursMinutes", ("hours", (int)t.TotalHours), ("minutes", t.Minutes.ToString("00"))]
            : l["format.minutes", ("minutes", t.Minutes)];
    }

    public static string Percent(double value) =>
        L is { } l ? l["format.percent", ("value", l.Number(value, 1))] : $"{value:0.0} %";

    public static string Bytes(long bytes)
    {
        var (unit, value, decimals) = bytes switch
        {
            >= 1L << 40 => ("tb", bytes / (double)(1L << 40), 1),
            >= 1L << 30 => ("gb", bytes / (double)(1L << 30), 1),
            >= 1L << 20 => ("mb", bytes / (double)(1L << 20), 1),
            >= 1L << 10 => ("kb", bytes / 1024d, 0),
            _ => ("b", (double)bytes, 0),
        };
        return L is { } l ? l[$"format.bytes.{unit}", ("value", l.Number(value, decimals))] : $"{value:0.#} {unit}";
    }
}
