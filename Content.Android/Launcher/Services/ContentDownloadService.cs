using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Content.Android.Launcher.Api;
using Content.Android.Launcher.Models;

namespace Content.Android.Launcher.Services;

[Service(
    Name = "io.spacestation14.android.ContentDownloadService",
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync
)]
public sealed class ContentDownloadService : Service
{
    public const int NotificationId = 1001;
    public const string ChannelId = "ss14_download_channel";

    public static event Action<int, int, string>? ProgressChanged;
    public static event Action<string>? DownloadCompleted;
    public static event Action<string>? DownloadFailed;

    public static bool IsDownloading { get; private set; }
    public static int CurrentProgress { get; private set; }
    public static int TotalProgress { get; private set; }
    public static string? CurrentStatus { get; private set; }
    public static string? ActiveServerAddress { get; private set; }

    private static CancellationTokenSource? _cts;
    private PowerManager.WakeLock? _wakeLock;
    private NotificationManager? _notifyManager;
    private Notification.Builder? _notifBuilder;

    public static void Start(Context context, ServerBuildInfo build, string serverAddress, string cacheDir)
    {
        if (IsDownloading) return;

        IsDownloading = true;
        ActiveServerAddress = serverAddress;
        CurrentProgress = 0;
        TotalProgress = 0;
        CurrentStatus = "Connecting...";

        var intent = new Intent(context, typeof(ContentDownloadService));
        intent.PutExtra("server_address", serverAddress);
        intent.PutExtra("cache_dir", cacheDir);

        if (!string.IsNullOrEmpty(build.ManifestUrl)) intent.PutExtra("manifest_url", build.ManifestUrl);
        if (!string.IsNullOrEmpty(build.ManifestDownloadUrl)) intent.PutExtra("manifest_download_url", build.ManifestDownloadUrl);
        if (!string.IsNullOrEmpty(build.DownloadUrl)) intent.PutExtra("download_url", build.DownloadUrl);
        if (!string.IsNullOrEmpty(build.Hash)) intent.PutExtra("hash", build.Hash);
        if (!string.IsNullOrEmpty(build.ManifestHash)) intent.PutExtra("manifest_hash", build.ManifestHash);
        if (!string.IsNullOrEmpty(build.Version)) intent.PutExtra("version", build.Version);
        if (!string.IsNullOrEmpty(build.ForkId)) intent.PutExtra("fork_id", build.ForkId);
        if (!string.IsNullOrEmpty(build.EngineVersion)) intent.PutExtra("engine_version", build.EngineVersion);
        intent.PutExtra("acz", build.Acz);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Cancel()
    {
        _cts?.Cancel();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _notifyManager = (NotificationManager?)GetSystemService(NotificationService);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _notifyManager != null)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "SS14 Content Download",
                NotificationImportance.Low
            )
            {
                Description = "Displays download progress in notification shade"
            };
            _notifyManager.CreateNotificationChannel(channel);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent == null)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        var serverAddress = intent.GetStringExtra("server_address") ?? "";
        var cacheDir = intent.GetStringExtra("cache_dir") ?? "";

        var build = new ServerBuildInfo
        {
            ManifestUrl = intent.GetStringExtra("manifest_url"),
            ManifestDownloadUrl = intent.GetStringExtra("manifest_download_url"),
            DownloadUrl = intent.GetStringExtra("download_url"),
            Hash = intent.GetStringExtra("hash"),
            ManifestHash = intent.GetStringExtra("manifest_hash"),
            Version = intent.GetStringExtra("version"),
            ForkId = intent.GetStringExtra("fork_id"),
            EngineVersion = intent.GetStringExtra("engine_version"),
            Acz = intent.GetBooleanExtra("acz", false)
        };

        var launchIntent = new Intent(this, typeof(LauncherActivity));
        launchIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
        );

        _notifBuilder = new Notification.Builder(this, ChannelId);

        _notifBuilder
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetContentTitle("Space Station 14")
            .SetContentText("Connecting to server...")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetProgress(0, 0, true)
            .SetContentIntent(pendingIntent);

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, _notifBuilder.Build(), global::Android.Content.PM.ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, _notifBuilder.Build());
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SS14_Service", $"StartForeground error: {ex}");
        }

        try
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "SS14:DownloadWakeLock");
            _wakeLock?.Acquire(30 * 60 * 1000L);
        }
        catch { }

        _cts = new CancellationTokenSource();
        Task.Run(async () => await RunDownloadAsync(build, serverAddress, cacheDir, pendingIntent, _cts.Token));

        return StartCommandResult.NotSticky;
    }

    private async Task RunDownloadAsync(
        ServerBuildInfo build,
        string serverAddress,
        string cacheDir,
        PendingIntent pendingIntent,
        CancellationToken cancel)
    {
        var aczClient = new AczClient();
        var sw = Stopwatch.StartNew();
        var lastNotifMs = 0L;

        try
        {
            var targetZip = await aczClient.DownloadManifestBuildAsync(
                build,
                serverAddress,
                cacheDir,
                (cur, tot, status) =>
                {
                    CurrentProgress = cur;
                    TotalProgress = tot;
                    CurrentStatus = status;

                    ProgressChanged?.Invoke(cur, tot, status);

                    if (sw.ElapsedMilliseconds - lastNotifMs >= 500 || cur == tot)
                    {
                        lastNotifMs = sw.ElapsedMilliseconds;
                        try
                        {
                            var pct = tot > 0 ? (int)(cur * 100L / tot) : 0;
                            _notifBuilder?
                                .SetContentText(status)
                                .SetProgress(tot, cur, tot <= 0);

                            if (_notifBuilder != null)
                            {
                                _notifyManager?.Notify(NotificationId, _notifBuilder.Build());
                            }
                        }
                        catch { }
                    }
                },
                cancel
            );

            try
            {
                var doneBuilder = new Notification.Builder(this, ChannelId)
                    .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownloadDone)
                    .SetContentTitle("Space Station 14")
                    .SetContentText("Download completed! Tap to play.")
                    .SetOngoing(false)
                    .SetAutoCancel(true)
                    .SetContentIntent(pendingIntent);

                _notifyManager?.Notify(NotificationId, doneBuilder.Build());
            }
            catch { }

            DownloadCompleted?.Invoke(targetZip);
        }
        catch (System.OperationCanceledException)
        {
            DownloadFailed?.Invoke("Download canceled.");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("SS14Launcher", $"Background download failed: {ex}");
            try
            {
                var errBuilder = new Notification.Builder(this, ChannelId)
                    .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyError)
                    .SetContentTitle("Space Station 14")
                    .SetContentText($"Download error: {ex.Message}")
                    .SetOngoing(false)
                    .SetAutoCancel(true)
                    .SetContentIntent(pendingIntent);

                _notifyManager?.Notify(NotificationId, errBuilder.Build());
            }
            catch { }

            DownloadFailed?.Invoke(ex.Message);
        }
        finally
        {
            IsDownloading = false;
            try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch { }
            try
            {
                StopForeground(StopForegroundFlags.Detach);
            }
            catch { }

            StopSelf();
        }
    }

    public override void OnDestroy()
    {
        base.OnDestroy();
        IsDownloading = false;
        try { if (_wakeLock?.IsHeld == true) _wakeLock.Release(); } catch { }
    }
}
