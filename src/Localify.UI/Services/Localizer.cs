using Localify.Contracts;
using Localify.Contracts.Localization;
using Microsoft.JSInterop;

namespace Localify.UI.Services;

/// <summary>
/// Langue de l'interface et accès aux textes (<c>@L["player.next"]</c>, <c>@L["library.tracks", ("count", n)]</c>).
/// Ordre de choix : préférence du compte, sinon dernier choix sur cet appareil, sinon langue du navigateur / du
/// téléphone, sinon l'anglais. Un changement redessine toute l'interface (voir App.razor).
/// </summary>
public sealed class Localizer
{
    private const string StorageKey = "localify.language";
    private readonly IKeyValueStore _store;
    // Fourni par l'interface (InitAsync) : sous Android, IJSRuntime est propre à la WebView (scoped) et l'instance
    // qu'obtiendrait ce singleton n'est reliée à aucune page (« Cannot invoke JavaScript outside of a WebView context »).
    private IJSRuntime? _js;
    private readonly Translations _t = Translations.Instance;

    public Localizer(IKeyValueStore store, AuthSession auth)
    {
        _store = store;
        Current = this;
        // À la connexion (ou au renouvellement), la langue choisie sur le compte s'applique.
        auth.Changed += () =>
        {
            if (auth.User?.Language is { } preferred && _t.IsAvailable(preferred) && preferred != Language) _ = ApplyAsync(preferred, remember: true);
        };
    }

    /// <summary>Pour le code sans injection (formats statiques, client d'API).</summary>
    public static Localizer? Current { get; private set; }

    public string Language { get; private set; } = Translations.Fallback;

    public event Action? Changed;

    public IReadOnlyList<LanguageInfo> Available => _t.Available;

    public LanguageFormat Format => _t.Format(Language);

    public string this[string key] => _t.Get(Language, key);

    /// <summary>Vrai si la clé existe (dans la langue courante ou en anglais, la langue de repli).</summary>
    public bool Has(string key) => _t.Has(Language, key) || _t.Has("en", key);

    public string this[string key, params (string Name, object? Value)[] args] => _t.Get(Language, key, args);

    /// <summary>Texte venu du serveur : ses marqueurs <see cref="Tx"/> sont traduits, le reste affiché tel quel.</summary>
    public string Server(string? text) => text is null ? "" : _t.Translate(Language, text);

    public string Number(double value, int decimals = 0) => Format.Number(value, decimals);

    /// <summary>Date (heure locale) au format de la langue : « 29/10/2026 », « Oct 29, 2026 ».</summary>
    public string Date(DateTimeOffset value) => DateText("format.date", value.ToLocalTime());

    public string DateTime(DateTimeOffset value) => DateText("format.dateTime", value.ToLocalTime());

    private string DateText(string key, DateTimeOffset d) => this[key,
        ("d", d.Day.ToString()), ("dd", d.Day.ToString("00")), ("MM", d.Month.ToString("00")),
        ("MMM", Format.ShortMonth(d.Month)), ("MMMM", Format.Month(d.Month)), ("yyyy", d.Year.ToString()),
        ("HH", d.Hour.ToString("00")), ("mm", d.Minute.ToString("00"))];

    /// <param name="js">Runtime JS de la page (celui injecté dans le composant racine).</param>
    public async Task InitAsync(string? accountLanguage, IJSRuntime js)
    {
        _js = js;
        string? stored = null, device = null;
        try
        {
            stored = await _store.GetAsync(StorageKey);
            device = await js.InvokeAsync<string>("localify.languages");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
        }
        await ApplyAsync(_t.Resolve(accountLanguage, stored, device), remember: false);
    }

    /// <summary>Choix explicite de l'utilisateur : retenu sur l'appareil (et sur le compte par l'appelant).</summary>
    public Task SetAsync(string language) => ApplyAsync(_t.Resolve(language), remember: true);

    private async Task ApplyAsync(string language, bool remember)
    {
        var changed = language != Language;
        Language = language;
        if (remember) await _store.SetAsync(StorageKey, language);
        try
        {
            if (_js is not null) await _js.InvokeVoidAsync("localify.setLanguage", language);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
        }
        if (changed) Changed?.Invoke();
    }
}
