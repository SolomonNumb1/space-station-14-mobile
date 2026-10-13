using System;
using System.IO;
using System.Text;

namespace Content.Android;

/// <summary>
/// File-based crash reporter. No logcat required:
/// all managed + Java crashes are appended to crash.txt in the app's
/// private files dir, alongside game.log (Console redirect).
/// The launcher surfaces these via a View/Share/Clear dialog.
/// </summary>
// [SolomonNumb1] standalone crash reporter, intercepts unhandled exceptions,logs to priveta files, enables user sharing via launcher
public static class CrashReporter
{
    private static readonly object Lock = new();
    private const string CrashFileName = "crash.txt";
    private const string GameLogFileName = "game.log";
    private const string SeenMarkerFileName = "crash_last_seen.txt";

    // Keep crash.txt bounded so it never fills internal storage.
    private const long MaxCrashFileBytes = 512 * 1024;

    // How much log text we embed/share at most (Intent extras have ~1MB limit).
    private const int MaxReportChars = 60_000;
    private const int MaxLogTailChars = 20_000;

    public static string FilesDir
    {
        get
        {
            try
            {
                var dir = global::Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(dir))
                    return dir!;
            }
            catch { }
            try
            {
                var dir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
                if (!string.IsNullOrEmpty(dir))
                    return dir!;
            }
            catch { }
            return "/data/data/io.spacestation14.android/files";
        }
    }

    public static string CrashPath => Path.Combine(FilesDir, CrashFileName);
    public static string GameLogPath => Path.Combine(FilesDir, GameLogFileName);
    private static string SeenMarkerPath => Path.Combine(FilesDir, SeenMarkerFileName);

    public static string GetDeviceSummary()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            // NOTE: kept out of the interpolation holes below — alias-qualified
            // names (global::) are not accepted inside them by this toolchain.
            var manufacturer = global::Android.OS.Build.Manufacturer;
            var model = global::Android.OS.Build.Model;
            var androidRelease = global::Android.OS.Build.VERSION.Release;
            var sdkInt = (int)global::Android.OS.Build.VERSION.SdkInt;
            sb.AppendLine($"Device: {manufacturer} {model}");
            sb.AppendLine($"Android: {androidRelease} (SDK {sdkInt})");
            try
            {
                var ctx = global::Android.App.Application.Context;
                var pkg = ctx.PackageManager?.GetPackageInfo(ctx.PackageName ?? "", 0);
                sb.AppendLine($"App: {pkg?.VersionName ?? "?"} ({pkg?.VersionCode})");
            }
            catch { sb.AppendLine("App: unknown"); }
            return sb.ToString();
        }
        catch
        {
            return $"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z (device info unavailable)";
        }
    }

    // [SolomonNumb1] dumps the full error chain,stack trace and hardware specs to help debug stuff
    public static void WriteCrash(string stage, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] CRASH AT STAGE: {stage}");
            sb.Append(GetDeviceSummary());
            if (ex == null)
            {
                sb.AppendLine("Exception: <null> (unknown)");
            }
            else
            {
                var cur = ex;
                while (cur != null)
                {
                    sb.AppendLine($"Exception ({cur.GetType().FullName}): {cur.Message}");
                    sb.AppendLine($"StackTrace: {cur.StackTrace}");
                    cur = cur.InnerException;
                    if (cur != null)
                        sb.AppendLine("--- Inner ---");
                }
            }
            sb.AppendLine();
            WriteText(sb.ToString(), "SS14_CRASH");
        }
        catch { }
    }

    public static void WriteJavaCrash(string stage, string? throwableText)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] JAVA CRASH AT STAGE: {stage}");
            sb.Append(GetDeviceSummary());
            sb.AppendLine(throwableText ?? "<no throwable info>");
            sb.AppendLine();
            WriteText(sb.ToString(), "SS14_CRASH");
        }
        catch { }
    }

    public static void WriteMessage(string stage, string message)
    {
        try
        {
            WriteText($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] [{stage}] {message}\n", "SS14_CRASH");
        }
        catch { }
    }

    private static void WriteText(string text, string logcatTag)
    {
        lock (Lock)
        {
            try { global::Android.Util.Log.Error(logcatTag, Truncate(text, 4000)); } catch { }
            try { Console.WriteLine(text); } catch { }
            try
            {
                Directory.CreateDirectory(FilesDir);
                RotateIfNeeded(CrashPath);
                File.AppendAllText(CrashPath, text);
            }
            catch { }
        }
    }

    // [SolomonNumb1] trims oldest entries to keep log file under 512 kb
    private static void RotateIfNeeded(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var len = new FileInfo(path).Length;
            if (len <= MaxCrashFileBytes)
                return;
            // Drop the oldest half, keep the newest half.
            var all = File.ReadAllText(path);
            var keepFrom = all.Length / 2;
            // Avoid splitting mid-line.
            var nl = all.IndexOf('\n', keepFrom);
            if (nl >= 0)
                keepFrom = nl + 1;
            var kept = all.Substring(keepFrom);
            File.WriteAllText(path,
                $"[... truncated {keepFrom} chars to stay under {MaxCrashFileBytes} bytes ...]\n" + kept);
        }
        catch { }
    }

    public static bool HasCrashReport()
    {
        try { return File.Exists(CrashPath) && new FileInfo(CrashPath).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// True if crash.txt grew since the launcher last marked it seen.
    /// Used to auto-prompt "last session crashed" exactly once per crash.
    /// </summary>
    public static bool HasNewCrash()
    {
        try
        {
            if (!HasCrashReport())
                return false;
            var info = new FileInfo(CrashPath);
            var sig = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            if (!File.Exists(SeenMarkerPath))
                return true;
            var seen = File.ReadAllText(SeenMarkerPath);
            return !string.Equals(seen, sig, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static void MarkSeen()
    {
        try
        {
            if (!HasCrashReport())
            {
                if (File.Exists(SeenMarkerPath))
                    File.Delete(SeenMarkerPath);
                return;
            }
            var info = new FileInfo(CrashPath);
            File.WriteAllText(SeenMarkerPath, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        catch { }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            try { if (File.Exists(CrashPath)) File.Delete(CrashPath); } catch { }
            try { if (File.Exists(SeenMarkerPath)) File.Delete(SeenMarkerPath); } catch { }
            // game.log is per-run; leave it alone so the current session's log survives.
        }
    }

    /// <summary>
    /// Full shareable report: device header + crash.txt tail + game.log tail, truncated.
    /// </summary>
    // [SolomonNumb1] generates unified diagnostic report including device info, log tails, and touch layout
    public static string GetFullReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SS14 Android crash report ===");
        sb.Append(GetDeviceSummary());
        sb.AppendLine();
        sb.AppendLine("=== crash.txt (tail) ===");
        sb.AppendLine(TailText(CrashPath, MaxReportChars - MaxLogTailChars - 2000));
        sb.AppendLine();
        sb.AppendLine("=== game.log (tail) ===");
        sb.AppendLine(TailText(GameLogPath, MaxLogTailChars));
        sb.AppendLine();
        sb.AppendLine("=== client config (mobile/display/interface/input) ===");
        var cfgSection = GetMobileConfigSection();
        sb.AppendLine(cfgSection.Length > MaxLogTailChars
            ? cfgSection.Substring(cfgSection.Length - MaxLogTailChars)
            : cfgSection);
        sb.AppendLine();
        sb.AppendLine("=== overlay debug dump (written by game) ===");
        sb.AppendLine(ReadOverlayDebugDump());
        var report = sb.ToString();
        if (report.Length > MaxReportChars)
            report = report.Substring(report.Length - MaxReportChars);
        return report;
    }

    public static string GetStatusLine()
    {
        try
        {
            var crashSize = File.Exists(CrashPath) ? new FileInfo(CrashPath).Length : 0;
            var logSize = File.Exists(GameLogPath) ? new FileInfo(GameLogPath).Length : 0;
            if (crashSize == 0 && logSize == 0)
                return "No crash report or game log found yet.";
            return $"crash.txt: {FormatBytes(crashSize)}   game.log: {FormatBytes(logSize)}";
        }
        catch { return "Log status unavailable."; }
    }

    /// <summary>
    /// Locates the engine client config to expose input/UI settings for debugging.
    /// </summary>
    public static string? FindClientConfig()
    {
        try
        {
            // Manual recursion: Directory.GetFiles(AllDirectories) aborts on
            // the first inaccessible directory; skip failures per-directory.
            var stack = new Stack<string>();
            stack.Push(FilesDir);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] subDirs = [];
                string[] files = [];
                try { subDirs = Directory.GetDirectories(dir); } catch { continue; }
                try { files = Directory.GetFiles(dir, "client_config.toml"); } catch { }
                if (files.Length > 0)
                    return files[0];
                foreach (var sub in subDirs)
                    stack.Push(sub);
            }
        }
        catch { }
        return null;
    }

    private static string GetMobileConfigSection()
    {
        try
        {
            var path = FindClientConfig();
            if (path == null)
                return "(client_config.toml not found)";
            var sb = new StringBuilder();
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.StartsWith("mobile.", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("display.", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("interface.", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("input.", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(line.Length > 2000 ? line.Substring(0, 2000) + "..." : line);
                }
            }
            var out_ = sb.ToString();
            return string.IsNullOrWhiteSpace(out_) ? "(no mobile/display/interface/input entries)" : out_;
        }
        catch (Exception ex)
        {
            return $"(could not read config: {ex.Message})";
        }
    }

    private static string TailText(string path, int maxChars)
    {
        try
        {
            if (!File.Exists(path))
                return "(file not found)";
            var info = new FileInfo(path);
            if (info.Length == 0)
                return "(empty)";
            // Read tail efficiently for large game.log files.
            const int chunk = 32 * 1024;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= maxChars + chunk)
                return Truncate(File.ReadAllText(path), maxChars);
            fs.Seek(-(maxChars + chunk), SeekOrigin.End);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            if (text.Length > maxChars)
            {
                text = text.Substring(text.Length - maxChars);
                var nl = text.IndexOf('\n');
                if (nl >= 0 && nl < 1000)
                    text = text.Substring(nl + 1);
                text = "[... truncated, showing tail ...]\n" + text;
            }
            return text;
        }
        catch (Exception ex)
        {
            return $"(could not read {Path.GetFileName(path)}: {ex.Message})";
        }
    }

    private static string ReadOverlayDebugDump()
    {
        try
        {
            var sb = new StringBuilder();
            var path = Path.Combine(FilesDir, "mobile-controls-debug.txt");
            if (!File.Exists(path))
                sb.AppendLine("(mobile-controls-debug.txt not found - overlay never wrote it)");
            else
                sb.AppendLine(File.ReadAllText(path));
            var arrange = Path.Combine(FilesDir, "mobile-arrange-debug.txt");
            if (!File.Exists(arrange))
                sb.AppendLine("(mobile-arrange-debug.txt not found - overlay never arranged)");
            else
                sb.AppendLine(File.ReadAllText(arrange));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(could not read dump: {ex.Message})";
        }
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return "[... truncated ...]\n" + s.Substring(s.Length - max);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
