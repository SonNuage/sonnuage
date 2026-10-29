using Localify.Contracts;

namespace Localify.UI.Services;

public sealed record Toast(Guid Id, string Message, bool IsError);

/// <summary>État d'interface partagé : notifications, liste des playlists (menu « ajouter à… »), favoris.</summary>
public sealed class UiState(ApiClient api, IPlayer player)
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
            Notify(updated.IsFavorite ? "Ajouté aux favoris" : "Retiré des favoris");
            return updated;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(ex.Message, true);
            return track;
        }
    }

    public async Task AddToPlaylistAsync(Guid playlistId, IReadOnlyList<Guid> trackIds)
    {
        try
        {
            await api.AddToPlaylistAsync(playlistId, trackIds);
            var name = Playlists.FirstOrDefault(p => p.Id == playlistId)?.Name ?? "la playlist";
            Notify(trackIds.Count == 1 ? $"Ajouté à {name}" : $"{trackIds.Count} titres ajoutés à {name}");
            await RefreshPlaylistsAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(ex.Message, true);
        }
    }

    public async Task<PlaylistDto?> CreatePlaylistAsync(string name, IReadOnlyList<Guid>? trackIds)
    {
        try
        {
            var created = await api.CreatePlaylistAsync(name, trackIds);
            Notify($"Playlist « {name} » créée");
            await RefreshPlaylistsAsync();
            return created;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Notify(ex.Message, true);
            return null;
        }
    }
}

public static class Format
{
    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string LongDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        >= 1L << 10 => $"{bytes / 1024d:0} Ko",
        _ => $"{bytes} o",
    };

    public static string Plural(int n, string singular, string plural) => $"{n} {(n > 1 ? plural : singular)}";
}
