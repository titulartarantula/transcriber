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

    /// <summary>Shows Android's "Let app always run in background?" prompt.</summary>
    public static void RequestExemption()
    {
        var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations,
            global::Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
        try
        {
            Platform.CurrentActivity!.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            // Some builds hide the direct prompt; fall back to the full list.
            Platform.CurrentActivity!.StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
        }
    }
}
