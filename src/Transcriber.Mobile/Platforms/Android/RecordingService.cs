using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

namespace Transcriber.Mobile;

/// <summary>
/// Keeps the app alive with a notification while recording and transcribing, so the screen can turn
/// off or you can switch apps mid-meeting. The notification's Stop button ends the recording.
/// </summary>
[Service(ForegroundServiceType = ForegroundService.TypeMicrophone, Exported = false)]
public sealed class RecordingService : Service
{
    private const int NotificationId = 1001;
    private const string ChannelId = "recording";
    private const string AlertChannelId = "alerts";
    private const string TextExtra = "text";
    private const string RecordingExtra = "recording";
    private const string StopAction = "dev.transcriber.app.STOP";

    private static RecordingService? _running;
    private PowerManager.WakeLock? _wakeLock;

    /// <summary>
    /// Starts the foreground service, or updates its notification if it's already running. Updating in
    /// place matters: Android refuses to start a foreground service while the app is in the background.
    /// </summary>
    public static void Show(string text, bool recording)
    {
        if (_running is { } service)
        {
            service.Post(text, recording);
            return;
        }
        var context = Platform.AppContext;
        context.StartForegroundService(new Intent(context, typeof(RecordingService))
            .PutExtra(TextExtra, text)
            .PutExtra(RecordingExtra, recording));
    }

    public static void Hide()
    {
        _running = null;
        var context = Platform.AppContext;
        context.StopService(new Intent(context, typeof(RecordingService)));
    }

    /// <summary>A separate, audible notification for things that need the user: naming speakers, a finished note.</summary>
    public static void Alert(int id, string title, string text)
    {
        var context = Platform.AppContext;
        var manager = (NotificationManager)context.GetSystemService(NotificationService)!;
        EnsureChannels(manager);
        var notification = new Notification.Builder(context, AlertChannelId)
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetStyle(new Notification.BigTextStyle().BigText(text))!
            .SetSmallIcon(global::Android.Resource.Drawable.PresenceAudioOnline)!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenApp(context))!
            .Build()!;
        manager.Notify(id, notification);
    }

    public static void CancelAlert(int id)
    {
        var manager = (NotificationManager)Platform.AppContext.GetSystemService(NotificationService)!;
        manager.Cancel(id);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == StopAction)
        {
            MainThread.BeginInvokeOnMainThread(async () => await SessionController.Instance.StopAndTranscribeAsync());
            return StartCommandResult.NotSticky;
        }

        _running = this;
        EnsureChannels((NotificationManager)GetSystemService(NotificationService)!);
        var notification = Build(intent?.GetStringExtra(TextExtra) ?? "Recording", intent?.GetBooleanExtra(RecordingExtra, false) ?? false);
        // The microphone type exists from Android 11, which is also when background mic use became restricted.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            StartForeground(NotificationId, notification, ForegroundService.TypeMicrophone);
        else
            StartForeground(NotificationId, notification);

        if (_wakeLock is null)
        {
            var power = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Transcriber:recording");
            // Timed, so a crash can never pin the CPU awake indefinitely.
            _wakeLock!.Acquire((long)TimeSpan.FromHours(6).TotalMilliseconds);
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_running == this) _running = null;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        _wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    private void Post(string text, bool recording)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, Build(text, recording));
    }

    private Notification Build(string text, bool recording)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Transcriber")!
            .SetContentText(text)!
            .SetSmallIcon(global::Android.Resource.Drawable.PresenceAudioOnline)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(OpenApp(this))!;

        if (recording)
        {
            var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(RecordingService)).SetAction(StopAction),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var icon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMediaPause);
            builder.AddAction(new Notification.Action.Builder(icon, "Stop and transcribe", stop).Build());
            builder.SetUsesChronometer(true);
        }
        return builder.Build()!;
    }

    private static PendingIntent OpenApp(Context context)
    {
        var open = new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        return PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private static void EnsureChannels(NotificationManager manager)
    {
        if (manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low)
            {
                Description = "Shown while Transcriber is recording or transcribing",
            });
        }
        if (manager.GetNotificationChannel(AlertChannelId) is null)
        {
            manager.CreateNotificationChannel(new NotificationChannel(AlertChannelId, "Needs you", NotificationImportance.Default)
            {
                Description = "Naming speakers and finished transcripts",
            });
        }
    }
}
