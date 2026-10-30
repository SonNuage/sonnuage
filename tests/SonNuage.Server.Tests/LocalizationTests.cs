using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SonNuage.Contracts;
using SonNuage.Contracts.Localization;

namespace SonNuage.Server.Tests;

public partial class LocalizationTests
{
    private static readonly Translations T = Translations.Instance;
    private static readonly string[] PluralCategories = ["zero", "one", "two", "few", "many", "other"];

    [Fact]
    public void Every_language_has_the_same_keys_and_placeholders_as_english()
    {
        var english = Flatten("en");
        foreach (var language in T.Available.Select(l => l.Code).Where(c => c != "en"))
        {
            var other = Flatten(language);
            Assert.Empty(english.Keys.Except(other.Keys).Select(k => $"{language} : clé manquante {k}"));
            Assert.Empty(other.Keys.Except(english.Keys).Select(k => $"{language} : clé en trop {k}"));
            // Les formats de date choisissent librement leurs composants (jour/mois numériques ou nom du mois).
            foreach (var (key, placeholders) in english.Where(e => !e.Key.StartsWith("format.date", StringComparison.Ordinal)))
                Assert.True(placeholders.SetEquals(other[key]), $"{language} : variables différentes pour {key}");
        }
    }

    [Theory]
    [InlineData("fr", 0, "0 titre")]
    [InlineData("fr", 1, "1 titre")]
    [InlineData("fr", 2, "2 titres")]
    [InlineData("fr", 12345, "12_345 titres")]
    [InlineData("en", 0, "0 tracks")]
    [InlineData("en", 1, "1 track")]
    [InlineData("en", 12345, "12_345 tracks")]
    public void Plurals_follow_each_language_rules(string language, int count, string expected) =>
        Assert.Equal(expected.Replace("_", T.Format(language).GroupSeparator), T.Get(language, "common.tracks", ("count", count)));

    [Theory]
    [InlineData("fr-FR,fr;q=0.9,en;q=0.8", "fr")]
    [InlineData("de-DE,en-GB;q=0.8", "en")]
    [InlineData("pt-BR", "en")]
    [InlineData(null, "en")]
    public void Resolve_matches_primary_language_and_falls_back_to_english(string? header, string expected) =>
        Assert.Equal(expected, T.Resolve(header));

    [Fact]
    public void Unknown_keys_fall_back_to_the_key_itself() => Assert.Equal("nope.missing", T.Get("fr", "nope.missing"));

    [Fact]
    public void Server_markers_survive_storage_and_translate_with_arguments_and_nesting()
    {
        var inner = Tx.Key("errors.yt.checksumMismatch", ("asset", "yt-dlp_linux"));
        var stored = "yt-dlp: ERROR boom" + Tx.Key("errors.yt.updateFailed", ("message", inner));

        Assert.Equal("yt-dlp: ERROR boomUpdate failed: yt-dlp_linux: unexpected SHA-256 checksum, download refused.", T.Translate("en", stored));
        Assert.StartsWith("yt-dlp: ERROR boomMise à jour impossible : yt-dlp_linux : somme", T.Translate("fr", stored));
        Assert.Equal("Password: 8 characters minimum.", T.Translate("en", Tx.Key("errors.passwordMin", ("min", 8))));
        Assert.Equal("plain text", T.Translate("en", "plain text"));
    }

    [Fact]
    public void Literal_values_are_shown_as_is_and_numbers_get_separators()
    {
        var change = Tx.Key("jobs.rescan.change", ("field", Tx.Key("jobs.fields.year")), ("from", Tx.Literal("∅")), ("to", Tx.Literal(2021)));
        Assert.Equal("Year: “∅” → “2021”", T.Translate("en", change));
        Assert.Equal("12,500 tracks to check.", T.Translate("en", Tx.Key("jobs.rescan.planned", ("count", 12500))));
    }

    private static Dictionary<string, HashSet<string>> Flatten(string language)
    {
        var path = Path.Combine(RepoRoot(), "src", "SonNuage.Contracts", "Localization", language + ".json");
        var result = new Dictionary<string, HashSet<string>>();
        Walk(JsonNode.Parse(File.ReadAllText(path))!.AsObject(), "", result);
        return result;
    }

    private static void Walk(JsonObject node, string prefix, Dictionary<string, HashSet<string>> result)
    {
        foreach (var (name, value) in node)
        {
            if (name == "_meta") continue;
            var key = prefix + name;
            if (value is JsonObject obj && obj.All(p => PluralCategories.Contains(p.Key)))
                result[key] = obj.SelectMany(p => Placeholders(p.Value!.GetValue<string>())).ToHashSet();
            else if (value is JsonObject child)
                Walk(child, key + ".", result);
            else
                result[key] = Placeholders(value!.GetValue<string>()).ToHashSet();
        }
    }

    private static IEnumerable<string> Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "SonNuage.Contracts"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable");
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex Placeholder();
}

public sealed class LanguageApiTests(SonNuageFactory factory) : IClassFixture<SonNuageFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Problem_details_follow_accept_language()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "nope", null), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid credentials.", await response.Content.ReadAsStringAsync(Ct));

        client.DefaultRequestHeaders.AcceptLanguage.Clear();
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr"));
        response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "nope", null), Ct);
        Assert.Contains("Identifiants invalides.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Account_language_is_saved_and_validated()
    {
        var admin = await factory.AdminClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/auth/language", new SetLanguageRequest("xx"), Ct)).StatusCode);

        var response = await admin.PutAsJsonAsync("/api/auth/language", new SetLanguageRequest("en"), Ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("en", (await response.Content.ReadFromJsonAsync<UserDto>(Ct))!.Language);

        // Retour à la langue du navigateur.
        response = await admin.PutAsJsonAsync("/api/auth/language", new SetLanguageRequest(null), Ct);
        response.EnsureSuccessStatusCode();
        Assert.Null((await response.Content.ReadFromJsonAsync<UserDto>(Ct))!.Language);
    }
}
