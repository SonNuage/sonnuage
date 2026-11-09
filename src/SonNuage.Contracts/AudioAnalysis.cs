namespace SonNuage.Contracts;

/// <summary>Réglage de l'analyse audio (Essentia) : activée par défaut quand l'analyseur est installé.</summary>
public sealed record AudioAnalysisSettingsDto(bool Enabled);

/// <summary>
/// Avancement de l'analyse audio. <paramref name="Available"/> : l'analyseur est installé (image amd64) ;
/// <paramref name="Analyzed"/> + <paramref name="Errors"/> + <paramref name="Pending"/> = <paramref name="Total"/>.
/// </summary>
/// <param name="Running">Une tâche d'analyse tourne ou attend son tour.</param>
public sealed record AudioAnalysisStatusDto(bool Available, bool Enabled, int Total, int Analyzed, int Errors, int Pending, bool Running);
