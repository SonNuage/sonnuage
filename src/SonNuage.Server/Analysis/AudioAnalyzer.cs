namespace SonNuage.Server.Analysis;

/// <summary>
/// Calcule les caractéristiques sonores d'un fichier (<see cref="EssentiaAudioAnalyzer"/> en production, un faux dans
/// les tests). Un fichier illisible donne <see cref="AudioAnalysisOutcome.Failed"/> ; un analyseur hors d'usage lève
/// <see cref="AudioAnalyzerUnavailableException"/> sans rien conclure sur le fichier.
/// </summary>
public interface IAudioAnalyzer
{
    /// <summary>L'analyseur est installé (Python, script et modèles présents). Ne démarre rien.</summary>
    bool Available { get; }

    Task<AudioAnalysisOutcome> AnalyzeAsync(string path, CancellationToken ct);
}

/// <summary>Caractéristiques d'un morceau : tempo en BPM, le reste de 0 à 1 (null quand le modèle n'a rien donné).</summary>
public sealed record AudioFeatureValues(
    double? Bpm,
    double? Danceability,
    double? Energy,
    double? Valence,
    double? Happy,
    double? Sad,
    double? Relaxed,
    double? Aggressive,
    double? Party,
    double? Acoustic,
    double? Electronic,
    double? Instrumental);

/// <summary>Résultat de l'analyse d'un fichier : ses caractéristiques, ou la raison de l'échec.</summary>
public abstract record AudioAnalysisOutcome
{
    private AudioAnalysisOutcome()
    {
    }

    public sealed record Analyzed(AudioFeatureValues Values) : AudioAnalysisOutcome;

    public sealed record Failed(string Error) : AudioAnalysisOutcome;
}

/// <summary>L'analyseur ne démarre pas ou plante sans cesse : aucun morceau n'est marqué en échec.</summary>
public sealed class AudioAnalyzerUnavailableException(string message) : Exception(message);
