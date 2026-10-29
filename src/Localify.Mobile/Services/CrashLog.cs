using Android.Runtime;

namespace Localify.Mobile.Services;

/// <summary>
/// Filet de sécurité : toute exception non gérée (.NET, tâches, Java) est écrite dans un fichier avant que le
/// processus ne meure. Au lancement suivant, <see cref="CrashPage"/> l'affiche et permet de la copier :
/// un plantage n'est plus jamais muet.
/// </summary>
public static class CrashLog
{
    private const string FileName = "crash.log";
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Tâche « fire and forget » en échec : on la note sans tuer l'appli.
            Write("TaskScheduler", e.Exception);
            e.SetObserved();
        };
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write("Android", e.Exception);
    }

    public static void Write(string source, Exception? exception)
    {
        try
        {
            var text = $"""
                ==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · {source} ====
                Localify {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) · Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}) · {Android.OS.Build.Manufacturer} {Android.OS.Build.Model}
                {exception}

                """;
            Android.Util.Log.Error("Localify", text);
            var path = PathOrNull();
            if (path is null) return;
            // On garde les derniers plantages, pas un historique sans fin.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, text);
        }
        catch
        {
            // Écrire le journal ne doit jamais aggraver le plantage.
        }
    }

    /// <summary>Plantage(s) enregistré(s) depuis le dernier affichage, sinon null.</summary>
    public static string? ReadPending()
    {
        try
        {
            var path = PathOrNull();
            return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (PathOrNull() is { } path && File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static string? PathOrNull() =>
        Android.App.Application.Context?.FilesDir?.AbsolutePath is { } dir ? System.IO.Path.Combine(dir, FileName) : null;
}

/// <summary>Écran d'erreur natif (sans Blazor ni WebView) : texte sélectionnable, bouton Copier, puis Continuer.</summary>
public sealed class CrashPage : ContentPage
{
    public CrashPage(string report, Func<Page>? next)
    {
        BackgroundColor = Color.FromArgb("#121212");
        var copy = new Button { Text = "Copier le rapport", BackgroundColor = Color.FromArgb("#1db954"), TextColor = Colors.Black };
        copy.Clicked += async (_, _) =>
        {
            await Clipboard.Default.SetTextAsync(report);
            copy.Text = "Copié ✓";
        };
        var buttons = new HorizontalStackLayout { Spacing = 12, Children = { copy } };
        if (next is not null)
        {
            var go = new Button { Text = "Continuer", BackgroundColor = Color.FromArgb("#333333"), TextColor = Colors.White };
            go.Clicked += (_, _) =>
            {
                CrashLog.Clear();
                if (Window is { } window) window.Page = next();
            };
            buttons.Children.Add(go);
        }

        Content = new Grid
        {
            Padding = new Thickness(16, 32, 16, 16),
            RowSpacing = 12,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children =
            {
                new Label { Text = next is null ? "Localify n'a pas pu démarrer" : "Localify s'est arrêté la dernière fois", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                Row(1, new Label { Text = "Voici le rapport d'erreur. Copiez-le pour le transmettre.", TextColor = Color.FromArgb("#b3b3b3") }),
                Row(2, new ScrollView { Content = new Editor { Text = report, IsReadOnly = true, FontSize = 11, FontFamily = "monospace", TextColor = Color.FromArgb("#e0e0e0"), BackgroundColor = Color.FromArgb("#1e1e1e"), AutoSize = EditorAutoSizeOption.TextChanges } }),
                Row(3, buttons),
            },
        };
    }

    private static View Row(int row, View view)
    {
        Grid.SetRow(view, row);
        return view;
    }
}

/// <summary>Application de secours quand <see cref="MauiProgram.CreateMauiApp"/> lui-même échoue.</summary>
public sealed class CrashApp(CrashReport report) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new CrashPage(report.Text, null)) { Title = "Localify" };
}

public sealed record CrashReport(string Text);
