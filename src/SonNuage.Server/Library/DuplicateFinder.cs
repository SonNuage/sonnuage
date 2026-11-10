namespace SonNuage.Server.Library;

/// <summary>Morceau vu par la recherche de doublons : identifiant d'enregistrement MusicBrainz, durée, empreinte.</summary>
public sealed record DuplicateCandidate(Guid Id, Guid? RecordingId, double DurationSeconds, uint[]? Fingerprint);

/// <summary>
/// Copies d'un même enregistrement. Reason : « recording » si toutes ont le même identifiant MusicBrainz (alors
/// <see cref="RecordingId"/>), sinon « fingerprint » (reliées par leurs empreintes).
/// </summary>
public sealed record DuplicateGroup(string Reason, Guid? RecordingId, IReadOnlyList<Guid> Ids);

/// <summary>
/// Regroupe les morceaux du même enregistrement : même identifiant d'enregistrement MusicBrainz, ou empreintes très
/// proches pour des durées voisines. Les liens se cumulent (A comme B, B comme C : un seul groupe).
/// </summary>
public static class DuplicateFinder
{
    /// <summary>Bits identiques au minimum entre deux empreintes du même enregistrement (autre format, autre débit).</summary>
    public const double MinSimilarity = 0.85;
    /// <summary>Écart de durée toléré entre deux copies reconnues par l'empreinte (silences, coupe différente).</summary>
    public const double MaxDurationGap = 4;

    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<DuplicateCandidate> tracks)
    {
        var parent = Enumerable.Range(0, tracks.Count).ToArray();
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        void Join(int a, int b) => parent[Root(a)] = Root(b);

        foreach (var same in Enumerable.Range(0, tracks.Count).Where(i => tracks[i].RecordingId is not null).GroupBy(i => tracks[i].RecordingId))
            foreach (var i in same.Skip(1)) Join(i, same.First());

        // Empreintes : seulement entre durées voisines (fenêtre glissante sur les morceaux triés par durée).
        var printed = Enumerable.Range(0, tracks.Count).Where(i => tracks[i].Fingerprint is { Length: > 0 })
            .OrderBy(i => tracks[i].DurationSeconds).ToList();
        for (var x = 0; x < printed.Count; x++)
        {
            var a = tracks[printed[x]];
            for (var y = x + 1; y < printed.Count && tracks[printed[y]].DurationSeconds - a.DurationSeconds <= MaxDurationGap; y++)
            {
                if (Root(printed[x]) == Root(printed[y])) continue;
                if (Fingerprints.Similarity(a.Fingerprint!, tracks[printed[y]].Fingerprint!) >= MinSimilarity) Join(printed[x], printed[y]);
            }
        }

        return Enumerable.Range(0, tracks.Count).GroupBy(Root).Where(g => g.Count() > 1).Select(g =>
        {
            var recordings = g.Select(i => tracks[i].RecordingId).Distinct().ToList();
            var recording = recordings is [{ } only] ? only : (Guid?)null;
            return new DuplicateGroup(recording is null ? "fingerprint" : "recording", recording, g.Select(i => tracks[i].Id).ToList());
        }).ToList();
    }
}
