using Android.Content;
using Android.OS;
using Android.Provider;

namespace Transcriber.Mobile;

/// <summary>
/// Some phone makers stop background apps even while they show a foreground notification. Exempting
/// the app from battery optimization keeps long recordings and transcriptions from being cut off.
/// </summary>
public static class BatteryOptimization
{
    public static bool IsExempt
    {
        get
        {
            var power = (PowerManager)Platform.AppContext.GetSystemService(Context.PowerService)!;
            return power.IsIgnoringBatteryOptimizations(Platform.AppContext.PackageName);
        }
    }

    /// <summary>
    /// Opens Transcriber's page in Android settings, where Battery → Unrestricted lifts the limit. The
    /// one-tap "Let app always run in background?" prompt would need REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
    /// which Google Play only allows for a few kinds of app.
    /// </summary>
    public static void OpenSettings()
    {
        var intent = new Intent(Settings.ActionApplicationDetailsSettings,
            global::Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
        try
        {
            Platform.CurrentActivity!.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Platform.CurrentActivity!.StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
        }
    }
}
