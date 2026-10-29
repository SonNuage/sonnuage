using Android.App;
using Android.Runtime;
using Localify.Mobile.Services;

namespace Localify.Mobile;

[Application]
public sealed class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    public override void OnCreate()
    {
        // Avant tout le reste : un échec de démarrage doit laisser une trace.
        CrashLog.Install();
        base.OnCreate();
    }

    protected override MauiApp CreateMauiApp()
    {
        try
        {
            return MauiProgram.CreateMauiApp();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Démarrage", ex);
            return MauiProgram.CreateCrashApp(CrashLog.ReadPending() ?? ex.ToString());
        }
    }
}
