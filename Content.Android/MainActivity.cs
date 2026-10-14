extern alias ppysdl;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Android.App;
using Android.OS;
using Android.Views;
using SDLActivity = ppysdl::Org.Libsdl.App.SDLActivity;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]

namespace Content.Android
{
    [Activity(
        Label = "Space Station 14",
        MainLauncher = false,
        Exported = true,
        Icon = "@mipmap/ic_launcher",
        RoundIcon = "@mipmap/ic_launcher_round",
        Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
        ConfigurationChanges =
            global::Android.Content.PM.ConfigChanges.Orientation |
            global::Android.Content.PM.ConfigChanges.Keyboard |
            global::Android.Content.PM.ConfigChanges.KeyboardHidden |
            global::Android.Content.PM.ConfigChanges.ScreenSize |
            global::Android.Content.PM.ConfigChanges.ScreenLayout |
            global::Android.Content.PM.ConfigChanges.UiMode |
            global::Android.Content.PM.ConfigChanges.Navigation |
            global::Android.Content.PM.ConfigChanges.Touchscreen |
            global::Android.Content.PM.ConfigChanges.SmallestScreenSize |
            global::Android.Content.PM.ConfigChanges.Density |
            global::Android.Content.PM.ConfigChanges.FontScale |
            global::Android.Content.PM.ConfigChanges.LayoutDirection |
            global::Android.Content.PM.ConfigChanges.Locale,
        ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
    public class MainActivity : SDLActivity, global::Android.Hardware.Input.InputManager.IInputDeviceListener
    {
        public static MainActivity? Instance { get; private set; }

        public MainActivity()
        {
            Instance = this;
        }

        public MainActivity(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer)
            : base(handle, transfer)
        {
            Instance = this;
        }

        public static bool IsEngineReady => (bool?)AppDomain.CurrentDomain.GetData("SS14_ENGINE_READY") ?? false;
        public static bool IsTextInputActive => (bool?)AppDomain.CurrentDomain.GetData("SS14_TEXT_INPUT_ACTIVE") ?? false;

        public static bool IsHardwareKeyboardConnected()
        {
            try
            {
                var cfg = Application.Context?.Resources?.Configuration;
                if (cfg != null && cfg.Keyboard == global::Android.Content.Res.KeyboardType.Qwerty)
                {
                    if (cfg.HardKeyboardHidden != global::Android.Content.Res.HardKeyboardHidden.Yes)
                    {
                        return true;
                    }
                }

                var deviceIds = InputDevice.GetDeviceIds();
                if (deviceIds != null)
                {
                    foreach (var id in deviceIds)
                    {
                        if (id <= 0) continue;
                        var dev = InputDevice.GetDevice(id);
                        if (dev == null) continue;

                        var isKeyboard = (dev.Sources & InputSourceType.Keyboard) == InputSourceType.Keyboard;
                        if (isKeyboard)
                        {
                            if (dev.IsExternal)
                                return true;

                            if (!dev.IsVirtual && (int)dev.KeyboardType == 2)
                                return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Keyboard", $"Error checking hardware keyboard: {ex.Message}");
            }
            return false;
        }

        public static void DismissSoftKeyboard()
        {
            try
            {
                var imm = (global::Android.Views.InputMethods.InputMethodManager?)Application.Context?.GetSystemService(global::Android.Content.Context.InputMethodService);
                var view = Instance?.Window?.DecorView ?? MSurface;
                if (imm != null && view != null)
                {
                    imm.HideSoftInputFromWindow(view.WindowToken, 0);
                }
            }
            catch {}
        }

        public static void UpdateHardwareKeyboardState()
        {
            var connected = IsHardwareKeyboardConnected();
            AppDomain.CurrentDomain.SetData("SS14_HARDWARE_KEYBOARD_CONNECTED", connected);
            global::Android.Util.Log.Info("SS14Keyboard", $"Hardware keyboard connected: {connected}");
            if (connected)
            {
                Instance?.RunOnUiThread(DismissSoftKeyboard);
            }
        }

        public void OnInputDeviceAdded(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public void OnInputDeviceRemoved(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public void OnInputDeviceChanged(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            UpdateHardwareKeyboardState();
        }

        public override bool DispatchTouchEvent(MotionEvent? ev)
        {
            if (!IsEngineReady)
                return true;
            return base.DispatchTouchEvent(ev);
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (!IsEngineReady)
                return true;

            if (e != null)
            {
                if (e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute)
                {
                    return base.DispatchKeyEvent(e);
                }

                if (e.KeyCode == Keycode.Back)
                {
                    if (e.Action == KeyEventActions.Down)
                    {
                        OnBackPressed();
                    }
                    return true;
                }

                if (IsTextInputActive)
                {
                    return base.DispatchKeyEvent(e);
                }

                try
                {
                    var effectiveKey = NormalizeKeycode(e);
                    if (effectiveKey != Keycode.Unknown)
                    {
                        if (HandleKeyEvent(MSurface, (int)effectiveKey, e, null))
                            return true;
                    }
                }
                catch (Exception ex)
                {
                    LogCrash("DispatchKeyEvent", ex);
                }
            }
            return base.DispatchKeyEvent(e);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Keycode> _downKeyMap = new();

        private static Keycode NormalizeKeycode(KeyEvent e)
        {
            if (e.Action == KeyEventActions.Up)
            {
                if (e.ScanCode > 0 && _downKeyMap.TryRemove(e.ScanCode, out var recordedKey))
                    return recordedKey;
                if ((int)e.KeyCode != 0 && _downKeyMap.TryRemove((int)e.KeyCode, out var recordedByCode))
                    return recordedByCode;
            }

            if (e.ScanCode > 0)
            {
                var mapped = e.ScanCode switch
                {
                    17 => Keycode.W,
                    30 => Keycode.A,
                    31 => Keycode.S,
                    32 => Keycode.D,
                    16 => Keycode.Q,
                    18 => Keycode.E,
                    19 => Keycode.R,
                    20 => Keycode.T,
                    21 => Keycode.Y,
                    22 => Keycode.U,
                    23 => Keycode.I,
                    24 => Keycode.O,
                    25 => Keycode.P,
                    26 => Keycode.LeftBracket,
                    27 => Keycode.RightBracket,
                    33 => Keycode.F,
                    34 => Keycode.G,
                    35 => Keycode.H,
                    36 => Keycode.J,
                    37 => Keycode.K,
                    38 => Keycode.L,
                    39 => Keycode.Semicolon,
                    40 => Keycode.Apostrophe,
                    41 => Keycode.Grave,
                    43 => Keycode.Backslash,
                    44 => Keycode.Z,
                    45 => Keycode.X,
                    46 => Keycode.C,
                    47 => Keycode.V,
                    48 => Keycode.B,
                    49 => Keycode.N,
                    50 => Keycode.M,
                    51 => Keycode.Comma,
                    52 => Keycode.Period,
                    53 => Keycode.Slash,
                    2 => Keycode.Num1,
                    3 => Keycode.Num2,
                    4 => Keycode.Num3,
                    5 => Keycode.Num4,
                    6 => Keycode.Num5,
                    7 => Keycode.Num6,
                    8 => Keycode.Num7,
                    9 => Keycode.Num8,
                    10 => Keycode.Num9,
                    11 => Keycode.Num0,
                    12 => Keycode.Minus,
                    13 => Keycode.Equals,
                    1 => Keycode.Escape,
                    28 => Keycode.Enter,
                    57 => Keycode.Space,
                    14 => Keycode.Del,
                    15 => Keycode.Tab,
                    _ => Keycode.Unknown
                };

                if (mapped != Keycode.Unknown)
                {
                    if (e.Action == KeyEventActions.Down)
                        _downKeyMap[e.ScanCode] = mapped;
                    return mapped;
                }
            }

            var unicode = e.UnicodeChar;
            if (unicode > 0)
            {
                var ch = char.ToLowerInvariant((char)unicode);
                var charMapped = ch switch
                {
                    'w' or 'ц' => Keycode.W,
                    'a' or 'ф' => Keycode.A,
                    's' or 'ы' => Keycode.S,
                    'd' or 'в' => Keycode.D,
                    'q' or 'й' => Keycode.Q,
                    'e' or 'у' => Keycode.E,
                    'r' or 'к' => Keycode.R,
                    't' or 'е' => Keycode.T,
                    'y' or 'н' => Keycode.Y,
                    'u' or 'г' => Keycode.U,
                    'i' or 'ш' => Keycode.I,
                    'o' or 'щ' => Keycode.O,
                    'p' or 'з' => Keycode.P,
                    '[' or 'х' => Keycode.LeftBracket,
                    ']' or 'ъ' => Keycode.RightBracket,
                    'f' or 'а' => Keycode.F,
                    'g' or 'п' => Keycode.G,
                    'h' or 'р' => Keycode.H,
                    'j' or 'о' => Keycode.J,
                    'k' or 'л' => Keycode.K,
                    'l' or 'д' => Keycode.L,
                    ';' or 'ж' => Keycode.Semicolon,
                    '\'' or 'э' => Keycode.Apostrophe,
                    'z' or 'я' => Keycode.Z,
                    'x' or 'ч' => Keycode.X,
                    'c' or 'с' => Keycode.C,
                    'v' or 'м' => Keycode.V,
                    'b' or 'и' => Keycode.B,
                    'n' or 'т' => Keycode.N,
                    'm' or 'ь' => Keycode.M,
                    ',' or 'б' => Keycode.Comma,
                    '.' or 'ю' => Keycode.Period,
                    '`' or 'ё' => Keycode.Grave,
                    _ => Keycode.Unknown
                };

                if (charMapped != Keycode.Unknown)
                {
                    if (e.Action == KeyEventActions.Down)
                    {
                        if (e.ScanCode > 0)
                            _downKeyMap[e.ScanCode] = charMapped;
                        else if ((int)e.KeyCode != 0)
                            _downKeyMap[(int)e.KeyCode] = charMapped;
                    }
                    return charMapped;
                }
            }

            return e.KeyCode;
        }

        public override bool OnGenericMotionEvent(MotionEvent? e)
        {
            if (e != null && e.Action == MotionEventActions.Scroll)
            {
                try
                {
                    var vscroll = e.GetAxisValue(Axis.Vscroll);
                    var hscroll = e.GetAxisValue(Axis.Hscroll);
                    SDLActivity.OnNativeMouse(0, (int)MotionEventActions.Scroll, hscroll, vscroll, false);
                    return true;
                }
                catch (Exception ex)
                {
                    LogCrash("OnGenericMotionEvent", ex);
                }
            }
            return base.OnGenericMotionEvent(e);
        }

        public override void OnBackPressed()
        {
            try
            {
                var now = SystemClock.UptimeMillis();
                var escDown = new KeyEvent(now, now, KeyEventActions.Down, Keycode.Escape, 0);
                var escUp = new KeyEvent(now, now, KeyEventActions.Up, Keycode.Escape, 0);
                HandleKeyEvent(MSurface, (int)Keycode.Escape, escDown, null);
                HandleKeyEvent(MSurface, (int)Keycode.Escape, escUp, null);
            }
            catch (Exception ex)
            {
                LogCrash("OnBackPressed", ex);
            }
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Back)
            {
                OnBackPressed();
                return true;
            }
            return base.OnKeyDown(keyCode, e);
        }

        public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Back)
            {
                return true;
            }
            return base.OnKeyUp(keyCode, e);
        }

        private static void LogCrash(string stage, Exception ex)
        {
            try
            {
                CrashReporter.WriteCrash(stage, ex);
            }
            catch {}
        }

        /// <summary>
        /// Catches Java-side (non-.NET) crashes and routes them to crash.txt.
        /// </summary>
        private sealed class JavaCrashHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
        {
            private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _default;

            public JavaCrashHandler(Java.Lang.Thread.IUncaughtExceptionHandler? def)
            {
                _default = def;
            }

            public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
            {
                try
                {
                    CrashReporter.WriteJavaCrash($"JavaThread:{t?.Name ?? "?"}",
                        e?.ToString() ?? "<no throwable>");
                }
                catch { }
                try
                {
                    // Give the file write a moment before the process dies.
                    System.Threading.Thread.Sleep(300);
                }
                catch { }
                if (_default != null)
                {
                    try { _default.UncaughtException(t, e); } catch { }
                }
            }
        }

        private static bool _javaHandlerInstalled;

        // [SolomonNumb1] hook JVM uncaught exception handler to capture OS level android crashes before app termination
        private static void InstallJavaCrashHandler()
        {
            if (_javaHandlerInstalled)
                return;
            _javaHandlerInstalled = true;
            try
            {
                var def = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
                if (def is JavaCrashHandler)
                    return;
                Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaCrashHandler(def);
            }
            catch (Exception ex)
            {
                try { CrashReporter.WriteCrash("InstallJavaCrashHandler", ex); } catch { }
            }
        }

        static MainActivity()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    LogCrash("UnhandledException", args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception"));
                };
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
                {
                    LogCrash("UnobservedTaskException", args.Exception);
                };
                try
                {
                    global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
                    {
                        LogCrash("AndroidEnvironment.Unhandled", args.Exception);
                    };
                }
                catch { }

                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    var name = new AssemblyName(args.Name).Name;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name == name)
                        {
                            return assembly;
                        }
                    }
                    return null;
                };

                try
                {
                    System.Environment.SetEnvironmentVariable("MONO_GC_PARAMS", "major=marksweep-conc,nursery-size=8m");
                }
                catch { }

            }
            catch (Exception ex)
            {
                LogCrash("StaticConstructor", ex);
            }
        }

        public static string? LaunchMountZip;
        public static string? LaunchConnectAddress;
        public static string? LaunchSs14Address;
        public static string? LaunchUsername;
        public static string? LaunchToken;
        public static string? LaunchUserId;
        public static string? LaunchPubKey;
        public static readonly System.Collections.Generic.Dictionary<string, string> LaunchBuildCVars = new();

        private static void EnsureResourcesExtracted(string? filesDir)
        {
            if (string.IsNullOrEmpty(filesDir))
                filesDir = "/data/data/io.spacestation14.android/files";
            var zipFile = Path.Combine(filesDir, "resources.zip");
            var verFile = Path.Combine(filesDir, "resources.version");
            var currentVer = typeof(MainActivity).Assembly.GetName().Version?.ToString() ?? "1.3.0";
            try
            {
                if (!File.Exists(zipFile) || new FileInfo(zipFile).Length == 0 || !File.Exists(verFile) || File.ReadAllText(verFile).Trim() != currentVer)
                {
                    var assembly = typeof(MainActivity).Assembly;
                    var resourceName = "Content.Android.resources.zip";
                    var names = assembly.GetManifestResourceNames();
                    foreach (var name in names)
                    {
                        if (name.EndsWith("resources.zip"))
                        {
                            resourceName = name;
                            break;
                        }
                    }
                    using var inStream = assembly.GetManifestResourceStream(resourceName);
                    if (inStream != null)
                    {
                        Directory.CreateDirectory(filesDir);
                        var tempFile = zipFile + ".tmp";
                        using (var outStream = File.Create(tempFile))
                        {
                            inStream.CopyTo(outStream);
                        }
                        File.Move(tempFile, zipFile, true);
                        File.WriteAllText(verFile, currentVer);
                        global::Android.Util.Log.Info("SS14_LOG", $"Extracted fresh resources.zip ({new FileInfo(zipFile).Length} bytes) for version {currentVer}");
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("SS14_LOG", $"Failed to ensure resources extracted: {ex}");
            }
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            try
            {
                System.Environment.SetEnvironmentVariable("MONO_GC_PARAMS", "major=marksweep-conc,nursery-size=8m");
            }
            catch { }

            InstallJavaCrashHandler();
            // The SDL runtime can reset orientation at startup; enforce
            // landscape in code (manifest attribute alone doesn't stick).
            try
            {
                RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                VolumeControlStream = global::Android.Media.Stream.Music;
            }
            catch { }

            try
            {
                LaunchMountZip = Intent?.GetStringExtra("mount_zip") ?? LaunchMountZip;
                LaunchConnectAddress = Intent?.GetStringExtra("connect_addr") ?? LaunchConnectAddress;
                LaunchSs14Address = Intent?.GetStringExtra("ss14_addr") ?? LaunchSs14Address;
                LaunchUsername = Intent?.GetStringExtra("username") ?? LaunchUsername;
                LaunchToken = Intent?.GetStringExtra("token") ?? LaunchToken;
                LaunchUserId = Intent?.GetStringExtra("userid") ?? LaunchUserId;
                LaunchPubKey = Intent?.GetStringExtra("pubkey") ?? LaunchPubKey;

                LaunchBuildCVars.Clear();
                if (Intent?.Extras != null)
                {
                    var extras = Intent.Extras;
                    var keySet = extras.KeySet();
                    if (keySet != null)
                    {
                        foreach (var key in keySet)
                        {
                            if (key != null && key.StartsWith("cvar_build_"))
                            {
                                var cvarName = key.Substring("cvar_build_".Length);
                                var val = extras.GetString(key);
                                if (!string.IsNullOrEmpty(val))
                                    LaunchBuildCVars[cvarName] = val;
                            }
                        }
                    }
                }

                Assembly.Load("Robust.Shared");
                Assembly.Load("Robust.Client");
            }
            catch (Exception ex)
            {
                LogCrash("OnCreateLoadAssemblies", ex);
            }

            try
            {
                EnsureResourcesExtracted(FilesDir?.AbsolutePath);
            }
            catch (Exception ex)
            {
                LogCrash("OnCreateExtractResources", ex);
            }
            base.OnCreate(savedInstanceState);
            Instance = this;
            try
            {
                var im = (global::Android.Hardware.Input.InputManager?)GetSystemService(InputService);
                im?.RegisterInputDeviceListener(this, null);
            }
            catch {}
            AppDomain.CurrentDomain.SetData("SS14_IS_HARDWARE_KEYBOARD_CONNECTED", (Func<bool>)IsHardwareKeyboardConnected);
            AppDomain.CurrentDomain.SetData("SS14_DISMISS_SOFT_KEYBOARD_IF_HARDWARE", (Action)(() =>
            {
                if (IsHardwareKeyboardConnected())
                {
                    Instance?.RunOnUiThread(DismissSoftKeyboard);
                }
            }));
            UpdateHardwareKeyboardState();
            ApplyImmersiveMode();
        }

        protected override void OnDestroy()
        {
            try
            {
                var im = (global::Android.Hardware.Input.InputManager?)GetSystemService(InputService);
                im?.UnregisterInputDeviceListener(this);
            }
            catch {}
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }


        private void ApplyImmersiveMode()
        {
            try
            {
                if (Window == null) return;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
                }

                Window.AddFlags(WindowManagerFlags.KeepScreenOn);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    Window.SetDecorFitsSystemWindows(false);
                    var controller = Window.InsetsController;
                    if (controller != null)
                    {
                        controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars() | WindowInsets.Type.CaptionBar());
                        controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }
                }
                else
                {
#pragma warning disable CS0618
                    Window.AddFlags(WindowManagerFlags.Fullscreen);
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                        SystemUiFlags.Fullscreen |
                        SystemUiFlags.HideNavigation |
                        SystemUiFlags.ImmersiveSticky |
                        SystemUiFlags.LayoutStable |
                        SystemUiFlags.LayoutHideNavigation |
                        SystemUiFlags.LayoutFullscreen);
#pragma warning restore CS0618
                }

                Window.SetSoftInputMode(SoftInput.StateAlwaysHidden);
            }
            catch (Exception ex)
            {
                LogCrash("ApplyImmersiveMode", ex);
            }
        }

        private void EnsureSurfaceFocus()
        {
            try
            {
                if (MSurface != null)
                {
                    MSurface.Focusable = true;
                    MSurface.FocusableInTouchMode = true;
                    MSurface.RequestFocus();
                }
            }
            catch {}
        }

        // [SolomonNumb1] downscales render surface to 720p/900p/1080p, drastically improving framerates on L GPUs(telling this with my mali g57 mc2,lmao)
        private void ApplyDisplayResolution()
        {
            try
            {
                if (MSurface?.Holder == null) return;
                var res = Intent?.GetStringExtra("display_resolution") ?? "native";
                if (res == "native" || string.IsNullOrEmpty(res))
                {
                    MSurface.Holder.SetSizeFromLayout();
                    return;
                }

                var metrics = Resources?.DisplayMetrics;
                if (metrics == null) return;

                var screenW = Math.Max(metrics.WidthPixels, metrics.HeightPixels);
                var screenH = Math.Min(metrics.WidthPixels, metrics.HeightPixels);
                if (screenH <= 0) return;
                var aspect = (float)screenW / screenH;

                int targetH = res switch
                {
                    "720p" => 720,
                    "900p" => 900,
                    "1080p" => 1080,
                    _ => screenH
                };

                if (targetH < screenH)
                {
                    int targetW = (int)Math.Round(targetH * aspect);
                    global::Android.Util.Log.Info("SS14Launch", $"[Resolution] Setting Surface size to {targetW}x{targetH} (native is {screenW}x{screenH})");
                    MSurface.Holder.SetFixedSize(targetW, targetH);
                }
                else
                {
                    MSurface.Holder.SetSizeFromLayout();
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Launch", $"ApplyDisplayResolution failed: {ex.Message}");
            }
        }

        // [SolomonNumb1] enforces SensorLandscape orientation, preventing SDL runtime from resetting display mode
        public override global::Android.Content.PM.ScreenOrientation RequestedOrientation
        {
            get => base.RequestedOrientation;
            set
            {
                // SDL runtime requests FULL_USER or PORTRAIT during window creation;
                // unconditionally enforce SensorLandscape so orientation never flips.
                base.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            try
            {
                if (RequestedOrientation != global::Android.Content.PM.ScreenOrientation.SensorLandscape)
                    RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
            }
            catch { }
            UpdateHardwareKeyboardState();
            ApplyImmersiveMode();
            EnsureSurfaceFocus();
            ApplyDisplayResolution();
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                try
                {
                    // SDL sets its own orientation at runtime, stomping the
                    // manifest; re-assert after focus lands (and again shortly
                    // after, to win any late SDL reset).
                    RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                    var view = Window?.DecorView;
                    view?.PostDelayed(() =>
                    {
                        try
                        {
                            if (RequestedOrientation != global::Android.Content.PM.ScreenOrientation.SensorLandscape)
                                RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                        }
                        catch { }
                    }, 500);
                }
                catch { }
                ApplyImmersiveMode();
                EnsureSurfaceFocus();
                ApplyDisplayResolution();
            }
        }

        protected override string[] GetLibraries()
        {
            return new[]
            {
                "SDL3",
                "sodium",
                "zstd",
                "freetype6",
                "openal"
            };
        }

        private sealed class AndroidLogcatWriter : TextWriter
        {
            private readonly object _lock = new();
            private readonly System.Text.StringBuilder _sb = new();
            private readonly string _tag;
            private readonly string? _filePath;
            private StreamWriter? _writer;
            private long _fileLength;

            public AndroidLogcatWriter(string tag, string? filePath = null)
            {
                _tag = tag;
                _filePath = filePath;
                InitWriter();
            }

            private void InitWriter()
            {
                if (string.IsNullOrEmpty(_filePath))
                    return;

                try
                {
                    var fi = new FileInfo(_filePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        fi.Delete();
                    }
                    _writer = new StreamWriter(File.Open(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    _fileLength = 0;
                }
                catch { }
            }

            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public override void Write(char value)
            {
                lock (_lock)
                {
                    if (value == '\n')
                    {
                        var line = _sb.ToString();
                        _sb.Clear();
                        global::Android.Util.Log.Info(_tag, line);
                        if (_writer != null)
                        {
                            try
                            {
                                _writer.WriteLine(line);
                                _fileLength += line.Length + 1;
                                if (_fileLength > 2 * 1024 * 1024)
                                {
                                    _writer.Dispose();
                                    InitWriter();
                                }
                            }
                            catch { }
                        }
                    }
                    else if (value != '\r')
                    {
                        _sb.Append(value);
                    }
                }
            }

            public override void Write(string? value)
            {
                if (value == null)
                    return;

                lock (_lock)
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        var c = value[i];
                        if (c == '\n')
                        {
                            var line = _sb.ToString();
                            _sb.Clear();
                            global::Android.Util.Log.Info(_tag, line);
                            if (_writer != null)
                            {
                                try
                                {
                                    _writer.WriteLine(line);
                                    _fileLength += line.Length + 1;
                                    if (_fileLength > 2 * 1024 * 1024)
                                    {
                                        _writer.Dispose();
                                        InitWriter();
                                    }
                                }
                                catch { }
                            }
                        }
                        else if (c != '\r')
                        {
                            _sb.Append(c);
                        }
                    }
                }
            }
        }


        protected override void Main()
        {
            try
            {
                var filesDir = "/data/data/io.spacestation14.android/files";
                try
                {
                    var logWriter = new AndroidLogcatWriter("SS14_LOG", Path.Combine(filesDir, "game.log"));
                    Console.SetOut(logWriter);
                    Console.SetError(logWriter);
                    global::Android.Util.Log.Info("SS14_LOG", "=== Console redirected to AndroidLogcatWriter ===");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("SS14_LOG", $"Failed to redirect console: {ex}");
                }

                var robustShared = Assembly.Load("Robust.Shared");
                if (robustShared == null) throw new Exception("robustShared assembly is null");
                var robustClient = Assembly.Load("Robust.Client");
                if (robustClient == null) throw new Exception("robustClient assembly is null");

                var programSharedType = robustShared.GetType("Robust.Shared.ProgramShared");
                if (programSharedType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Robust.Shared.ProgramShared");
                        if (t != null)
                        {
                            programSharedType = t;
                            break;
                        }
                    }
                }
                if (programSharedType == null) throw new Exception("Robust.Shared.ProgramShared type is null");
                var pathOffsetField = programSharedType.GetField("PathOffset", BindingFlags.Public | BindingFlags.Static);
                if (pathOffsetField == null) throw new Exception("PathOffset field is null");
                pathOffsetField.SetValue(null, filesDir + "/");

                var mountZip = LaunchMountZip;
                var connectAddr = LaunchConnectAddress;
                var ss14Addr = LaunchSs14Address;
                var username = LaunchUsername;
                var token = LaunchToken;
                var userId = LaunchUserId;
                var pubKey = LaunchPubKey;

                var authPath = Path.Combine(filesDir, "auth.json");
                if (File.Exists(authPath))
                {
                    try
                    {
                        var json = File.ReadAllText(authPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (string.IsNullOrEmpty(username) && root.TryGetProperty("username", out var u))
                            username = u.GetString();
                        if (string.IsNullOrEmpty(token) && root.TryGetProperty("token", out var tok))
                            token = tok.GetString();
                        if (string.IsNullOrEmpty(userId) && root.TryGetProperty("userId", out var uid))
                            userId = uid.GetString();
                    }
                    catch {}
                }

                if (!string.IsNullOrEmpty(token))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", token);
                }
                if (!string.IsNullOrEmpty(userId))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", userId);
                }
                if (!string.IsNullOrEmpty(pubKey))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_PUBKEY", pubKey);
                }
                System.Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");

                var hasZip = !string.IsNullOrEmpty(mountZip) && File.Exists(mountZip);

                var args = new List<string>
                {
                    "--cvar", "thread.parallel_count=2",
                    "--cvar", "light.blur=false",
                    "--cvar", "light.soft_shadows=false",
                    "--cvar", "light.ambient_occlusion=false",
                    "--cvar", "light.resolution_scale=0.25",
                    "--cvar", "light.max_shadowcasting_lights=8",
                    "--cvar", "parallax.enabled=false",
                    "--cvar", "viewport.scale_render=false",
                    "--cvar", "display.vsync=false",
                    "--cvar", "display.max_fps=60",
                    "--cvar", "audio.max_ambient_sources=16",
                    "--cvar", "audio.max_channels=24",
                    "--cvar", "net.logging=false",
                    "--cvar", "mobile.touch_controls=true",
                    "--cvar", "hud.fps_counter_visible=true",
#if DEBUG
                    "--cvar", "hud.version_watermark=true",
                    "--cvar", "log.level=Debug",
#else
                    "--cvar", "hud.version_watermark=false",
                    "--cvar", "log.level=Info",
#endif
                };

                if (!string.IsNullOrEmpty(connectAddr))
                {
                    if (hasZip)
                    {
                        args.Add("--mount-zip");
                        args.Add(mountZip!);
                    }
                    args.Add("--connect");
                    args.Add("--connect-address");
                    args.Add(connectAddr);
                    if (!string.IsNullOrEmpty(ss14Addr))
                    {
                        args.Add("--ss14-address");
                        args.Add(ss14Addr);
                    }
                    args.Add("--launcher");
                    if (!string.IsNullOrEmpty(username))
                    {
                        args.Add("--username");
                        args.Add(username);
                    }

                    foreach (var (cvarName, val) in LaunchBuildCVars)
                    {
                        args.Add("--cvar");
                        args.Add($"build.{cvarName}={val}");
                        global::Android.Util.Log.Info("SS14Launch", $"[Main] Build CVar: build.{cvarName}={val}");
                    }
                }

                var gcType = robustClient.GetType("Robust.Client.GameController");
                if (gcType == null) throw new Exception("Robust.Client.GameController type is null");
                var startMethod = gcType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
                if (startMethod == null) throw new Exception("Start method on GameController is null");

                var optsType = robustClient.GetType("Robust.Client.GameControllerOptions");
                var options = Activator.CreateInstance(optsType!);
                optsType?.GetProperty("Sandboxing")?.SetValue(options, false);

                var contentStart = !hasZip;
                global::Android.Util.Log.Info("SS14Launch", $"[Main] Starting GameController: contentStart={contentStart}, hasZip={hasZip}, mountZip={mountZip}, connectAddr={connectAddr}, user={username}");

                startMethod.Invoke(null, new object?[] { args.ToArray(), options, contentStart, null });
            }

            catch (Exception ex)
            {
                LogCrash("Main", ex);
                throw;
            }
        }
    }
}
