using Localify.Contracts.Localization;
using Localify.UI.Services;

namespace Localify.Mobile.Services;

/// <summary>
/// Textes de la partie native (Android Auto, lecture, écran de plantage), hors Blazor : mêmes fichiers de langue,
/// dans la langue de l'interface si elle a démarré, sinon celle du téléphone.
/// </summary>
public static class NativeText
{
    public static string Get(string key, string fallback)
    {
        try
        {
            var t = Translations.Instance;
            var language = Localizer.Current?.Language ?? t.Resolve(Java.Util.Locale.Default.ToLanguageTag());
            return t.Has(language, key) || t.Has("en", key) ? t.Get(language, key) : fallback;
        }
        catch (Exception)
        {
            // L'écran de plantage doit s'afficher quoi qu'il arrive, même si les traductions sont illisibles.
            return fallback;
        }
    }
}
