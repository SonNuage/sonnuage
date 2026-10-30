using Localify.Contracts.Localization;
using Localify.Server.Youtube;

namespace Localify.Server.Jobs;

/// <summary>
/// Empreinte Chromaprint d'un fichier puis recherche AcoustID. Tâche de la file « acoustid » (une seule à la fois,
/// imports et rescans confondus) ; le client AcoustID espace en plus ses requêtes d'une seconde.
/// Entrée : <c>path</c> (paramètre) ou <c>audio</c> (sortie de l'étape précédente). Sortie : l'entrée reçue,
/// complétée de <c>acoustid</c> (identifiants MusicBrainz et scores).
/// </summary>
public sealed class AcoustIdIdentifyJob(MusicBrainzClient musicBrainz, YoutubeSettingsStore settings) : IJobHandler
{
    private const int MaxMatches = 10;

    public JobKind Kind => JobKinds.AcoustIdIdentify;

    public async Task RunAsync(JobContext ctx)
    {
        var ct = ctx.CancellationToken;
        foreach (var (name, value) in ctx.Input) ctx.Output[name] = value?.DeepClone();
        ctx.Set("acoustid", Array.Empty<AcoustIdMatch>());

        var path = ctx.Get<string>("path") ?? ctx.Input["audio"]?.GetValue<string>();
        if (path is null || !File.Exists(path)) throw new IOException(Tx.Key("jobs.acoustid.noFile"));
        var key = settings.Settings.AcoustIdKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            ctx.Info(Tx.Key("jobs.acoustid.noKey"));
            return;
        }

        var fingerprint = await musicBrainz.FingerprintAsync(path, ct);
        if (fingerprint is null)
        {
            ctx.Warn(Tx.Key("jobs.acoustid.noFingerprint"));
            return;
        }
        ctx.Report(0.5);

        IReadOnlyList<AcoustIdMatch> matches;
        try
        {
            matches = await musicBrainz.AcoustIdLookupAsync(fingerprint, key, ct);
        }
        catch (Exception ex) when (ex is AcoustIdException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Nouvelle tentative plus tard ; à la dernière, on continue sans empreinte plutôt que de bloquer l'import.
            if (ctx.Attempt < Kind.MaxAttempts) throw new HttpRequestException(Tx.Key("jobs.acoustid.error", ("message", ex.Message)), ex);
            ctx.Warn(Tx.Key("jobs.acoustid.error", ("message", ex.Message)));
            return;
        }

        var kept = matches.Take(MaxMatches).ToList();
        ctx.Set("acoustid", kept);
        ctx.Info(kept.Count == 0
            ? Tx.Key("jobs.acoustid.none", ("duration", (int)fingerprint.DurationSeconds))
            : Tx.Key("jobs.acoustid.found", ("count", matches.Count), ("score", Math.Round(kept[0].Score * 100))));
    }
}
