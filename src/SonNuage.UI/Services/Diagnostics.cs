using System.Text;
using SonNuage.Contracts;

namespace SonNuage.UI.Services;

/// <summary>
/// Dernières actions de l'utilisateur et de l'appli (navigation, lecture, cycle de vie, avertissements), datées,
/// jointes au rapport en cas de plantage. Écrites depuis plusieurs threads : la file est protégée par un verrou.
/// </summary>
public sealed class Breadcrumbs(TimeProvider? time = null)
{
    public const int Capacity = 60;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<string> _items = new();
    private readonly Lock _lock = new();

    public void Add(string what)
    {
        var line = $"{_time.GetLocalNow():HH:mm:ss.fff} {what}";
        lock (_lock)
        {
            _items.Enqueue(line);
            while (_items.Count > Capacity) _items.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock) return [.. _items];
    }
}

/// <summary>L'appli et l'appareil au moment du plantage ; <see cref="Details"/> : mémoire, ABI, durée de vie…</summary>
public sealed record CrashDevice(string App, string AppVersion, string Device, string Os, IReadOnlyList<(string Name, string Value)> Details);

public static class CrashReports
{
    /// <summary>
    /// Rapport prêt à envoyer : l'exception d'abord (sa première ligne sert de titre côté serveur), puis le contexte et
    /// les dernières actions, coupé à la taille que le serveur accepte.
    /// </summary>
    public static CrashReportRequest Create(DateTimeOffset at, string source, Exception exception, CrashDevice device, IReadOnlyList<string> trail)
    {
        var text = new StringBuilder()
            .Append(exception.ToString().Replace("\r\n", "\n")).Append("\n\n")
            .Append("== Contexte ==\n");
        foreach (var (name, value) in device.Details) text.Append(name).Append(" : ").Append(value).Append('\n');
        text.Append("\n== Dernières actions ==\n");
        foreach (var line in trail) text.Append(line).Append('\n');

        var report = text.Length > CrashReportRequest.MaxReportLength ? text.ToString(0, CrashReportRequest.MaxReportLength) : text.ToString();
        return new CrashReportRequest(Guid.NewGuid(), at, device.App, device.AppVersion, device.Device, device.Os, source, report);
    }
}
