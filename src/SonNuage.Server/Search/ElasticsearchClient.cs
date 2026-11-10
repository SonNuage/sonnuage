using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SonNuage.Server.Search;

/// <summary>Résultat d'une recherche : identifiants dans l'ordre de pertinence, et éventuelle correction proposée.</summary>
public sealed record SearchHits(IReadOnlyList<Guid> Artists, IReadOnlyList<Guid> Albums, IReadOnlyList<Guid> Tracks, string? Correction);

/// <summary>
/// Document indexé : un par artiste, album et piste. <see cref="Description"/> : texte donné au modèle d'inférence de
/// l'option sémantique (noms, genre, décennie, tags, ambiances entendues) ; <see cref="All"/> à défaut.
/// </summary>
public sealed record SearchDocument(
    string Type,
    Guid EntityId,
    string Name,
    string? Artist,
    string? Album,
    string? Genre,
    int? Year,
    int Plays,
    string All,
    IReadOnlyList<string> Suggest,
    string? Description = null)
{
    public string Id => $"{Type}-{EntityId:N}";
}

/// <summary>
/// Accès à Elasticsearch par son API REST (compatible 8.x et 9.x, sans client lourd lié à une version).
/// <list type="bullet">
/// <item>« search_as_you_type » : résultats dès les premières lettres, sur chaque mot.</item>
/// <item>Requête floue (fuzziness AUTO) : tolère les fautes de frappe (« metalica », « beatels »).</item>
/// <item>Suggesteur de termes : « Vouliez-vous dire … » quand un mot n'existe pas dans la bibliothèque.</item>
/// <item>Complétion floue : propositions pendant la saisie.</item>
/// <item>Analyse insensible aux accents et à la casse. Popularité (écoutes) en bonus de score.</item>
/// <item>Option sémantique (semantic_text) si un endpoint d'inférence est configuré.</item>
/// </list>
/// </summary>
public sealed class ElasticsearchClient(IHttpClientFactory httpFactory, IOptions<SonNuageOptions> options, ILogger<ElasticsearchClient> logger)
{
    public const string HttpClientName = "elasticsearch";
    /// <summary>À incrémenter à chaque changement de mapping : un nouvel index est créé et rempli.</summary>
    private const int MappingVersion = 2;
    private const int BulkSize = 1000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Licences qui permettent l'API d'inférence, donc l'indexation d'un champ semantic_text.</summary>
    private static readonly string[] InferenceLicenses = ["enterprise", "trial"];

    private SearchOptions Options => options.Value.Search;
    private volatile bool _semanticRefused;

    public bool Configured => !string.IsNullOrWhiteSpace(Options.Url);

    /// <summary>
    /// Option sémantique active : un endpoint d'inférence est configuré et la licence du cluster le permet. Depuis
    /// 8.18/9.0, toute l'API d'inférence (modèles ELSER/E5 comme services externes) exige une licence enterprise ou
    /// d'essai : avec une licence basic, un champ semantic_text ferait échouer toute l'indexation.
    /// </summary>
    public bool Semantic => !string.IsNullOrWhiteSpace(Options.SemanticInferenceId) && !_semanticRefused;
    public string Index => $"{Options.Index}-v{MappingVersion}{(Semantic ? "-semantic" : "")}";

    /// <summary>Configuration du client HTTP nommé (adresse, authentification, certificat épinglé).</summary>
    public static void ConfigureHttp(IHttpClientBuilder builder) => builder
        .ConfigureHttpClient((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<SonNuageOptions>>().Value.Search;
            if (string.IsNullOrWhiteSpace(o.Url)) return;
            http.BaseAddress = new Uri(o.Url.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(60);
            if (!string.IsNullOrWhiteSpace(o.ApiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", o.ApiKey);
            else if (!string.IsNullOrWhiteSpace(o.Username))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.Username}:{o.Password}")));
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var o = sp.GetRequiredService<IOptions<SonNuageOptions>>().Value.Search;
            var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(o.CertificateFingerprint))
            {
                var pinned = o.CertificateFingerprint.Replace(":", "").Trim();
                handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None
                    || (cert is not null && Matches(cert, pinned))
                    || (chain?.ChainElements.Any(e => Matches(e.Certificate, pinned)) ?? false);
            }
            return handler;
        });

    private static bool Matches(X509Certificate cert, string fingerprint) =>
        string.Equals(new X509Certificate2(cert).GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), fingerprint, StringComparison.OrdinalIgnoreCase);

    private HttpClient Http() => httpFactory.CreateClient(HttpClientName);

    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await Http().GetAsync("_cluster/health?timeout=2s", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Nombre de documents, ou null si l'index n'existe pas encore.</summary>
    public async Task<long?> CountAsync(CancellationToken ct)
    {
        using var response = await Http().GetAsync($"{Index}/_count", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["count"]?.GetValue<long>();
    }

    public async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Options.SemanticInferenceId)) await CheckSemanticLicenseAsync(ct);
        using (var head = await Http().SendAsync(new HttpRequestMessage(HttpMethod.Head, Index), ct))
        {
            if (head.IsSuccessStatusCode) return;
        }
        logger.LogInformation("Création de l'index Elasticsearch {Index}", Index);
        using var response = await Http().PutAsync(Index, JsonBody(IndexDefinition()), ct);
        // Deux instances qui démarrent ensemble : la seconde reçoit « resource_already_exists ».
        if (response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync(ct)).Contains("resource_already_exists")) return;
        await EnsureAsync(response, ct);
    }

    /// <summary>Relue à chaque reconstruction : un passage en licence enterprise (ou son expiration) est pris en compte.</summary>
    private async Task CheckSemanticLicenseAsync(CancellationToken ct)
    {
        using var response = await Http().GetAsync("_license", ct);
        // Licence illisible (droits « monitor » absents) : on s'en tient à la configuration.
        if (!response.IsSuccessStatusCode) return;
        var license = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))?["license"];
        var type = license?["type"]?.GetValue<string>();
        var allowed = InferenceLicenses.Contains(type) && license?["status"]?.GetValue<string>() == "active";
        if (!allowed && !_semanticRefused)
        {
            logger.LogWarning("Recherche sémantique ignorée : l'endpoint d'inférence {Inference} demande une licence Elasticsearch enterprise ou d'essai (licence actuelle : {License})",
                Options.SemanticInferenceId, type ?? "inconnue");
        }
        _semanticRefused = !allowed;
    }

    /// <summary>Indexe tous les documents avec une génération, puis supprime ceux des générations précédentes.</summary>
    public async Task ReplaceAllAsync(IReadOnlyList<SearchDocument> documents, CancellationToken ct)
    {
        var generation = Guid.NewGuid().ToString("N");
        foreach (var chunk in documents.Chunk(BulkSize))
        {
            var body = new StringBuilder();
            foreach (var doc in chunk)
            {
                body.Append("{\"index\":{\"_index\":\"").Append(Index).Append("\",\"_id\":\"").Append(doc.Id).Append("\"}}\n");
                body.Append(JsonSerializer.Serialize(ToSource(doc, generation), Json)).Append('\n');
            }
            using var response = await Http().PostAsync("_bulk", new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson"), ct);
            await EnsureAsync(response, ct);
            var result = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            if (result?["errors"]?.GetValue<bool>() == true)
            {
                var first = result["items"]?.AsArray().Select(i => i?["index"]?["error"]).FirstOrDefault(e => e is not null);
                throw new InvalidOperationException($"Indexation Elasticsearch refusée : {first?.ToJsonString()}");
            }
        }

        var stale = new JsonObject { ["query"] = new JsonObject { ["bool"] = new JsonObject { ["must_not"] = Term("gen", generation) } } };
        using (var delete = await Http().PostAsync($"{Index}/_delete_by_query?conflicts=proceed&refresh=true", JsonBody(stale), ct))
        {
            await EnsureAsync(delete, ct);
        }
        using var refresh = await Http().PostAsync($"{Index}/_refresh", null, ct);
        await EnsureAsync(refresh, ct);
    }

    public async Task<SearchHits> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var body = new StringBuilder();
        foreach (var (type, size) in new[] { ("track", limit), ("artist", Math.Min(limit, 12)), ("album", Math.Min(limit, 12)) })
        {
            var request = new JsonObject
            {
                ["size"] = size,
                ["_source"] = new JsonArray("entity_id"),
                ["query"] = TypedQuery(type, query),
            };
            if (type == "track")
            {
                request["suggest"] = new JsonObject
                {
                    ["fix"] = new JsonObject
                    {
                        ["text"] = query,
                        ["term"] = new JsonObject { ["field"] = "all", ["suggest_mode"] = "missing", ["min_word_length"] = 3 },
                    },
                };
            }
            body.Append("{\"index\":\"").Append(Index).Append("\"}\n").Append(request.ToJsonString()).Append('\n');
        }

        using var response = await Http().PostAsync("_msearch", new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson"), ct);
        await EnsureAsync(response, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var responses = json?["responses"]?.AsArray() ?? [];
        if (responses.FirstOrDefault(r => r?["error"] is not null) is { } failed)
            throw new InvalidOperationException($"Recherche Elasticsearch en erreur : {failed["error"]?.ToJsonString()}");

        return new SearchHits(Ids(responses, 1), Ids(responses, 2), Ids(responses, 0), Correction(query, responses.FirstOrDefault()));
    }

    /// <summary>Complétion floue sur les noms d'artistes, d'albums et de titres.</summary>
    public async Task<IReadOnlyList<string>> SuggestAsync(string prefix, int limit, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["_source"] = new JsonArray("name"),
            ["suggest"] = new JsonObject
            {
                ["names"] = new JsonObject
                {
                    ["prefix"] = prefix,
                    ["completion"] = new JsonObject
                    {
                        ["field"] = "suggest",
                        ["size"] = limit,
                        ["skip_duplicates"] = true,
                        // Tolérance aux fautes seulement à partir de 5 lettres : avant, trop de bruit (« acd » → « and »).
                        ["fuzzy"] = new JsonObject { ["fuzziness"] = "AUTO", ["prefix_length"] = 1, ["min_length"] = 5 },
                    },
                },
            },
        };
        using var response = await Http().PostAsync($"{Index}/_search", JsonBody(request), ct);
        await EnsureAsync(response, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        return json?["suggest"]?["names"]?[0]?["options"]?.AsArray()
            .Select(o => o?["_source"]?["name"]?.GetValue<string>() ?? o?["text"]?.GetValue<string>())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    private JsonObject TypedQuery(string type, string query)
    {
        var lexical = new JsonArray
        {
            // Préfixe sur chaque mot du nom : « hig to h » trouve « Highway to Hell ».
            new JsonObject { ["multi_match"] = new JsonObject { ["query"] = query, ["type"] = "bool_prefix", ["fields"] = SaytFields("name"), ["boost"] = 4 } },
            // Mêmes règles sur nom + artiste + album : « acdc highway ».
            new JsonObject { ["multi_match"] = new JsonObject { ["query"] = query, ["type"] = "bool_prefix", ["fields"] = SaytFields("all"), ["boost"] = 2 } },
            // Fautes de frappe.
            new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = query,
                    ["fields"] = new JsonArray("name^3", "artist^2", "album", "all"),
                    ["fuzziness"] = "AUTO",
                    ["prefix_length"] = 1,
                },
            },
        };
        var match = new JsonObject
        {
            ["filter"] = new JsonArray(Term("type", type)),
            // Il faut une correspondance textuelle : la similarité sémantique trouve toujours un « plus proche »,
            // même pour un nom qui n'existe pas. Elle ne fait que réordonner (les demandes d'ambiance ont leur section).
            ["must"] = new JsonObject { ["bool"] = new JsonObject { ["should"] = lexical, ["minimum_should_match"] = 1 } },
        };
        if (Semantic)
        {
            match["should"] = new JsonArray(new JsonObject { ["semantic"] = new JsonObject { ["field"] = "semantic", ["query"] = query } });
        }

        return new JsonObject
        {
            ["function_score"] = new JsonObject
            {
                ["query"] = new JsonObject { ["bool"] = match },
                // Les titres souvent écoutés remontent, sans écraser la pertinence textuelle.
                ["field_value_factor"] = new JsonObject { ["field"] = "plays", ["modifier"] = "log2p", ["missing"] = 0 },
                ["boost_mode"] = "sum",
            },
        };
    }

    private static JsonArray SaytFields(string field) => new(field, $"{field}._2gram", $"{field}._3gram");

    private static JsonObject Term(string field, string value) => new() { ["term"] = new JsonObject { [field] = value } };

    private static List<Guid> Ids(JsonArray responses, int index) =>
        responses.ElementAtOrDefault(index)?["hits"]?["hits"]?.AsArray()
            .Select(h => Guid.TryParse(h?["_source"]?["entity_id"]?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList() ?? [];

    /// <summary>Remplace chaque mot inconnu par la meilleure suggestion. Null si rien à corriger.</summary>
    internal static string? Correction(string query, JsonNode? response)
    {
        var entries = response?["suggest"]?["fix"]?.AsArray();
        if (entries is null || entries.Count == 0) return null;
        var corrected = new StringBuilder(query);
        var changed = false;
        foreach (var entry in entries.Reverse())
        {
            var option = entry?["options"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>();
            if (option is null) continue;
            var offset = entry!["offset"]!.GetValue<int>();
            var length = entry["length"]!.GetValue<int>();
            corrected.Remove(offset, length).Insert(offset, option);
            changed = true;
        }
        return changed ? corrected.ToString() : null;
    }

    private object ToSource(SearchDocument d, string generation) => new
    {
        d.Type,
        EntityId = d.EntityId.ToString("N"),
        d.Name,
        d.Artist,
        d.Album,
        d.Genre,
        d.Year,
        d.Plays,
        d.All,
        Suggest = new { Input = CompletionInputs(d.Suggest), Weight = Math.Min(d.Plays + 1, 1000) },
        Semantic = Semantic ? d.Description ?? d.All : null,
        Gen = generation,
    };

    /// <summary>
    /// La complétion ne marche qu'à partir du début d'une entrée : on ajoute aussi la fin du nom à partir de
    /// chaque mot, pour que « gilm » propose « David Gilmour » et « yellow » « The Girl in the Yellow Dress ».
    /// </summary>
    private static List<string> CompletionInputs(IEnumerable<string> names)
    {
        var inputs = new List<string>();
        foreach (var name in names)
        {
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < Math.Min(words.Length, 6); i++)
            {
                var input = string.Join(' ', words[i..]);
                // Une entrée de complétion est limitée en longueur par défaut (max_input_length = 50).
                inputs.Add(input.Length > 50 ? input[..50] : input);
            }
        }
        return inputs.Distinct().ToList();
    }

    /// <summary>Supprime les index des versions de mapping précédentes une fois le nouveau rempli.</summary>
    public async Task DeleteOldIndexesAsync(CancellationToken ct)
    {
        using var response = await Http().GetAsync($"_cat/indices/{Options.Index}-v*?h=index&format=json", ct);
        if (!response.IsSuccessStatusCode) return;
        var indexes = await response.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        foreach (var name in indexes.Select(i => i?["index"]?.GetValue<string>()).OfType<string>().Where(n => n != Index))
        {
            logger.LogInformation("Suppression de l'ancien index {Index}", name);
            using var _ = await Http().DeleteAsync(name, ct);
        }
    }

    private JsonObject IndexDefinition()
    {
        var folding = new JsonObject { ["type"] = "search_as_you_type", ["analyzer"] = "folding" };
        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "keyword" },
            ["entity_id"] = new JsonObject { ["type"] = "keyword" },
            ["gen"] = new JsonObject { ["type"] = "keyword" },
            ["name"] = folding,
            ["all"] = folding.DeepClone(),
            ["artist"] = new JsonObject { ["type"] = "text", ["analyzer"] = "folding" },
            ["album"] = new JsonObject { ["type"] = "text", ["analyzer"] = "folding" },
            ["genre"] = new JsonObject { ["type"] = "text", ["analyzer"] = "folding" },
            ["year"] = new JsonObject { ["type"] = "integer" },
            ["plays"] = new JsonObject { ["type"] = "integer" },
            ["suggest"] = new JsonObject { ["type"] = "completion", ["analyzer"] = "folding", ["preserve_separators"] = false },
        };
        if (Semantic)
        {
            properties["semantic"] = new JsonObject { ["type"] = "semantic_text", ["inference_id"] = Options.SemanticInferenceId };
        }

        return new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["number_of_shards"] = 1,
                ["number_of_replicas"] = 0,
                ["analysis"] = new JsonObject
                {
                    ["analyzer"] = new JsonObject
                    {
                        ["folding"] = new JsonObject
                        {
                            ["type"] = "custom",
                            ["char_filter"] = new JsonArray("join_initials"),
                            ["tokenizer"] = "standard",
                            ["filter"] = new JsonArray("lowercase", "asciifolding"),
                        },
                    },
                    // « AC/DC », « T.N.T. », « It’s » : la ponctuation à l'intérieur d'un mot est retirée,
                    // on trouve donc « acdc », « tnt », « its » (et « ac/dc » tapé tel quel).
                    ["char_filter"] = new JsonObject
                    {
                        ["join_initials"] = new JsonObject
                        {
                            ["type"] = "pattern_replace",
                            ["pattern"] = "(?<=\\p{L})[./'’](?=\\p{L})",
                            ["replacement"] = "",
                        },
                    },
                },
            },
            ["mappings"] = new JsonObject { ["dynamic"] = false, ["properties"] = properties },
        };
    }

    private static StringContent JsonBody(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Elasticsearch {(int)response.StatusCode} : {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
    }
}
