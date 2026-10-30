using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>Stockage persistant clé/valeur : localStorage sur le web, Preferences/SecureStorage sur Android.</summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value);
}

/// <summary>Adresse du serveur. Sur le web c'est l'origine de la page. Sur Android elle est saisie par l'utilisateur.</summary>
public interface IServerEndpoint
{
    Uri? BaseAddress { get; }
    bool IsConfigurable { get; }
    Task SetAsync(Uri? address);
    event Action? Changed;
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

public sealed record PlayerState(
    IReadOnlyList<TrackDto> Queue,
    int CurrentIndex,
    bool IsPlaying,
    bool IsBuffering,
    double PositionSeconds,
    double DurationSeconds,
    bool Shuffle,
    RepeatMode Repeat,
    double Volume,
    string? Error)
{
    public static readonly PlayerState Empty = new([], -1, false, false, 0, 0, false, RepeatMode.Off, 1, null);
    public TrackDto? Current => CurrentIndex >= 0 && CurrentIndex < Queue.Count ? Queue[CurrentIndex] : null;
}

/// <summary>
/// Lecteur avec file d'attente. La file exposée est dans l'ordre de lecture effectif (déjà mélangée si shuffle).
/// Implémenté en JS (balise audio) côté web, par un service Media3 natif côté Android.
/// </summary>
public interface IPlayer
{
    PlayerState State { get; }
    event Action? StateChanged;

    Task PlayAsync(IReadOnlyList<TrackDto> tracks, int startIndex = 0, bool? shuffle = null);
    Task TogglePlayAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task SeekAsync(double seconds);
    Task JumpToAsync(int queueIndex);
    Task SetShuffleAsync(bool shuffle);
    Task SetRepeatAsync(RepeatMode mode);
    Task SetVolumeAsync(double volume);
    /// <param name="playNext">true : juste après la piste en cours. False : en fin de file.</param>
    Task EnqueueAsync(IReadOnlyList<TrackDto> tracks, bool playNext);
    Task RemoveAtAsync(int queueIndex);
    /// <summary>Relit les réglages (qualité, pré-chargement) et les applique aux pistes suivantes.</summary>
    Task ReloadSettingsAsync();
    /// <summary>Met à jour l'état favori d'une piste dans la file sans la recharger.</summary>
    void UpdateTrack(TrackDto track);
}

public interface IPlatformInfo
{
    /// <summary>Plateforme où un réseau mobile/Wi-Fi est distinguable (Android).</summary>
    bool HasMeteredNetworkDetection { get; }
    string Name { get; }
    /// <summary>Interface chargée depuis le serveur (web) : un déploiement la rend obsolète, il faut recharger la page.</summary>
    bool ServedByServer => false;
}

/// <summary>Lecteur capable de restaurer la file de la session précédente après connexion.</summary>
public interface IRestorablePlayer
{
    Task RestoreAsync();
    /// <summary>Avant un rechargement de la page : file et position enregistrées, lecture reprise après.</summary>
    Task PrepareReloadAsync() => Task.CompletedTask;
}
