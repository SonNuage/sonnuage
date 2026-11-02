namespace SonNuage.Contracts;

/// <summary>
/// Rapport de plantage envoyé par une appli : <see cref="Report"/> porte la trace de pile, le contexte et les dernières
/// actions. L'identifiant vient de l'appli, pour qu'un renvoi ne crée pas de doublon.
/// </summary>
public sealed record CrashReportRequest(Guid Id, DateTimeOffset OccurredAt, string App, string AppVersion, string Device, string Os, string Source, string Report)
{
    public const int MaxReportLength = 256 * 1024;
    public const int MaxPerHour = 20;
}

public sealed record CrashReportSummaryDto(Guid Id, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string User, string App, string AppVersion, string Device, string Headline);

public sealed record CrashReportDto(Guid Id, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, string User, string App, string AppVersion, string Device, string Os, string Source, string Report);
