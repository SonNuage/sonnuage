using Localify.Contracts;
using Localify.Server.Data;

namespace Localify.Server.Streaming;

public sealed record TranscodeProfile(StreamQuality Quality, string Extension, string ContentType, int? BitrateKbps)
{
    public string Name => Quality.ToWire();

    public IEnumerable<string> FfmpegArguments(string input, string output, int? sourceSampleRate)
    {
        yield return "-hide_banner";
        yield return "-loglevel"; yield return "error";
        yield return "-nostdin";
        yield return "-i"; yield return input;
        yield return "-map"; yield return "0:a:0";
        yield return "-map_metadata"; yield return "-1";
        yield return "-vn";
        if (Extension == "flac")
        {
            yield return "-c:a"; yield return "flac";
            yield return "-compression_level"; yield return "5";
            yield return "-f"; yield return "flac";
        }
        else
        {
            yield return "-c:a"; yield return "libmp3lame";
            yield return "-b:a"; yield return $"{BitrateKbps}k";
            // MP3 plafonne à 48 kHz : les sources haute résolution sont rééchantillonnées.
            if (sourceSampleRate is > 48_000) { yield return "-ar"; yield return "44100"; }
            yield return "-f"; yield return "mp3";
        }
        yield return "-y";
        yield return output;
    }

    private static readonly Dictionary<StreamQuality, TranscodeProfile> Profiles = new()
    {
        [StreamQuality.Lossless] = new(StreamQuality.Lossless, "flac", "audio/flac", null),
        [StreamQuality.High] = new(StreamQuality.High, "mp3", "audio/mpeg", 320),
        [StreamQuality.Normal] = new(StreamQuality.Normal, "mp3", "audio/mpeg", 192),
        [StreamQuality.Low] = new(StreamQuality.Low, "mp3", "audio/mpeg", 128),
        [StreamQuality.DataSaver] = new(StreamQuality.DataSaver, "mp3", "audio/mpeg", 64),
    };

    public static IEnumerable<TranscodeProfile> All => Profiles.Values;

    /// <summary>
    /// Profil à appliquer, ou null s'il vaut mieux servir le fichier original :
    /// qualité « originale », source déjà plus légère que la cible, ou FLAC demandé sur une source avec perte.
    /// Un format que les navigateurs ne lisent pas (WMA, APE…) est toujours transcodé.
    /// </summary>
    public static TranscodeProfile? Resolve(Track track, StreamQuality requested)
    {
        var playable = Library.LibraryFiles.IsDirectlyPlayable(track.Format);

        if (requested == StreamQuality.Original)
            return playable ? null : track.IsLossless ? Profiles[StreamQuality.Lossless] : Profiles[StreamQuality.High];

        var profile = Profiles[requested];
        if (profile.Extension == "flac")
        {
            if (!track.IsLossless) return playable ? null : Profiles[StreamQuality.High];
            // Déjà du FLAC : rien à gagner.
            return track.Format == "flac" ? null : profile;
        }

        if (!track.IsLossless && playable && track.BitrateKbps is { } br && br <= profile.BitrateKbps)
            return null;
        return profile;
    }
}
