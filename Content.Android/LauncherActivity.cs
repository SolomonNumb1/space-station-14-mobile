using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Content.Android.Launcher.Api;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Services;
using Content.Android.Launcher.Storage;
using Content.Android.Launcher.UI;
using Content.Android.Launcher.Utility;
using Path = System.IO.Path;
using Environment = System.Environment;

namespace Content.Android;

[Activity(
    Label = "Space Station 14",
    MainLauncher = true,
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen",
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
public sealed class LauncherActivity : Activity
{
    private enum Tab
    {
        Servers,
        Favorites,
        DirectConnect
    }

    private readonly LauncherStorage _storage = new();
    private readonly HubClient _hubClient = new();
    private readonly AuthClient _authClient = new();
    private readonly AczClient _aczClient = new();

    private List<ServerListEntry> _servers = new();
    private Tab _activeTab = Tab.Servers;
    private string _searchFilter = string.Empty;

    private Button _accountBtn = default!;
    private Button _tabServersBtn = default!;
    private Button _tabFavsBtn = default!;
    private Button _tabDirectBtn = default!;
    private EditText _searchInput = default!;
    private TextView _statusCountText = default!;
    private LinearLayout _listLayout = default!;
    private ScrollView _scroll = default!;

    private LinearLayout _overlayBox = default!;
    private TextView _overlayText = default!;
    private ProgressBar _overlayProgress = default!;

    private ServerInfo? _pendingServerInfo;
    private string? _pendingServerAddress;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AczClient.InitNative();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 101);
            }
        }

        ContentDownloadService.ProgressChanged += OnServiceProgressChanged;
        ContentDownloadService.DownloadCompleted += OnServiceDownloadCompleted;
        ContentDownloadService.DownloadFailed += OnServiceDownloadFailed;

        ApplyImmersiveMode();
        BuildOfficialUi();
        _ = RefreshHubServersAsync();

        if (!_storage.Data.DisclaimerAccepted)
        {
            ShowDisclaimerDialog();
        }
        else
        {
            _ = CheckForUpdatesAsync();
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveMode();
        UpdateAccountButton();

        if (ContentDownloadService.IsDownloading)
        {
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Visible;
            var tot = ContentDownloadService.TotalProgress;
            var cur = ContentDownloadService.CurrentProgress;
            _overlayProgress.Indeterminate = tot <= 0;
            if (tot > 0)
            {
                _overlayProgress.Max = tot;
                _overlayProgress.Progress = cur;
            }
            _overlayText.Text = ContentDownloadService.CurrentStatus ?? "Downloading content...";
        }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        ContentDownloadService.ProgressChanged -= OnServiceProgressChanged;
        ContentDownloadService.DownloadCompleted -= OnServiceDownloadCompleted;
        ContentDownloadService.DownloadFailed -= OnServiceDownloadFailed;
    }

    private void ApplyImmersiveMode()
    {
        try
        {
            if (Window == null) return;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window.InsetsController?.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            }
            else
            {
#pragma warning disable CS0618
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.Fullscreen |
                    SystemUiFlags.HideNavigation |
                    SystemUiFlags.ImmersiveSticky |
                    SystemUiFlags.LayoutStable);
#pragma warning restore CS0618
            }
        }
        catch {}
    }

    private void BuildOfficialUi()
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var root = new FrameLayout(this);
        root.SetBackgroundColor(LauncherTheme.Background);

        var mainLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        mainLayout.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var header = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        header.SetBackgroundColor(LauncherTheme.HeaderBackground);
        header.SetPadding((int)(12 * density), (int)(6 * density), (int)(12 * density), (int)(6 * density));
        header.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(52 * density));

        var logoImage = new ImageView(this);
        logoImage.SetScaleType(ImageView.ScaleType.FitCenter);
        logoImage.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent);
        LoadLogo(logoImage);
        header.AddView(logoImage);

        var headerSpacer = new View(this);
        headerSpacer.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f);
        header.AddView(headerSpacer);

        var discordBtn = CreateHeaderLinkButton("Discord", () => OpenUrl("https://discord.ss14.io/"));
        header.AddView(discordBtn);

        var websiteBtn = CreateHeaderLinkButton("Website", () => OpenUrl("https://spacestation14.com"));
        header.AddView(websiteBtn);

        var githubBtn = CreateHeaderLinkButton("GitHub", () => OpenUrl("https://github.com/SolomonNumb1/space-station-14-mobile"));
        header.AddView(githubBtn);

        _accountBtn = new Button(this)
        {
            TextSize = 12,
            Typeface = Typeface.DefaultBold
        };
        _accountBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        _accountBtn.SetTextColor(LauncherTheme.Foreground);
        _accountBtn.SetPadding((int)(12 * density), 0, (int)(12 * density), 0);
        _accountBtn.Click += (_, _) => ShowAccountDialog();
        UpdateAccountButton();
        header.AddView(_accountBtn);

        mainLayout.AddView(header);

        var goldLine = new View(this);
        goldLine.SetBackgroundColor(LauncherTheme.NanoGold);
        goldLine.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(2 * density));
        mainLayout.AddView(goldLine);

        var controlsRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        controlsRow.SetBackgroundColor(LauncherTheme.HeaderBackground);
        controlsRow.SetPadding((int)(10 * density), (int)(4 * density), (int)(10 * density), (int)(4 * density));
        controlsRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(42 * density));

        _tabServersBtn = CreateTabButton("Servers", true);
        _tabFavsBtn = CreateTabButton("Favorites", false);
        _tabDirectBtn = CreateTabButton("Direct Connect", false);

        _tabServersBtn.Click += (_, _) => SwitchTab(Tab.Servers);
        _tabFavsBtn.Click += (_, _) => SwitchTab(Tab.Favorites);
        _tabDirectBtn.Click += (_, _) => SwitchTab(Tab.DirectConnect);

        controlsRow.AddView(_tabServersBtn);
        controlsRow.AddView(_tabFavsBtn);
        controlsRow.AddView(_tabDirectBtn);

        var tabSpacer = new View(this);
        tabSpacer.LayoutParameters = new LinearLayout.LayoutParams((int)(12 * density), ViewGroup.LayoutParams.MatchParent);
        controlsRow.AddView(tabSpacer);

        _searchInput = new EditText(this)
        {
            Hint = "Search servers...",
            TextSize = 12
        };
        _searchInput.SetHintTextColor(LauncherTheme.ForegroundMuted);
        _searchInput.SetTextColor(LauncherTheme.Foreground);
        _searchInput.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 3, LauncherTheme.ControlMid);
        _searchInput.SetPadding((int)(10 * density), (int)(4 * density), (int)(10 * density), (int)(4 * density));
        _searchInput.LayoutParameters = new LinearLayout.LayoutParams(0, (int)(32 * density), 1f);
        _searchInput.TextChanged += (_, e) =>
        {
            _searchFilter = e?.Text?.ToString() ?? string.Empty;
            RenderServerList();
        };
        controlsRow.AddView(_searchInput);

        var refreshBtn = new Button(this)
        {
            Text = "↻",
            TextSize = 14,
            Typeface = Typeface.DefaultBold
        };
        refreshBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        refreshBtn.SetTextColor(LauncherTheme.Foreground);
        refreshBtn.LayoutParameters = new LinearLayout.LayoutParams((int)(34 * density), (int)(32 * density));
        refreshBtn.SetPadding(0, 0, 0, 0);
        refreshBtn.Click += async (_, _) => await RefreshHubServersAsync();
        controlsRow.AddView(refreshBtn);

        mainLayout.AddView(controlsRow);

        var sep = new View(this);
        sep.SetBackgroundColor(LauncherTheme.Separator);
        sep.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(1 * density));
        mainLayout.AddView(sep);

        var tableHeader = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        tableHeader.SetBackgroundColor(LauncherTheme.HeaderBackground);
        tableHeader.SetPadding((int)(12 * density), (int)(4 * density), (int)(12 * density), (int)(4 * density));

        var thName = CreateTableHeaderText("SERVER NAME", 1f);
        var thStatus = CreateTableHeaderText("STATUS", 0.35f);
        var thPlayers = CreateTableHeaderText("PLAYERS", 0.25f);
        var thAction = CreateTableHeaderText("CONNECT", 0.3f);

        tableHeader.AddView(thName);
        tableHeader.AddView(thStatus);
        tableHeader.AddView(thPlayers);
        tableHeader.AddView(thAction);
        mainLayout.AddView(tableHeader);

        _scroll = new ScrollView(this)
        {
            LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f)
        };
        _listLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        _scroll.AddView(_listLayout);
        mainLayout.AddView(_scroll);

        var bottomBar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        bottomBar.SetBackgroundColor(LauncherTheme.HeaderBackground);
        bottomBar.SetPadding((int)(12 * density), (int)(4 * density), (int)(12 * density), (int)(4 * density));
        bottomBar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density));

        _statusCountText = new TextView(this)
        {
            Text = "Loading servers...",
            TextSize = 11
        };
        _statusCountText.SetTextColor(LauncherTheme.SubText);
        _statusCountText.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        bottomBar.AddView(_statusCountText);

        var controlsBtn = new Button(this)
        {
            Text = "Controls",
            TextSize = 11
        };
        controlsBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        controlsBtn.SetTextColor(LauncherTheme.Foreground);
        controlsBtn.SetPadding((int)(10 * density), 0, (int)(10 * density), 0);
        var controlsLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        controlsLp.SetMargins(0, 0, (int)(8 * density), 0);
        controlsBtn.LayoutParameters = controlsLp;
        controlsBtn.Click += (_, _) => ShowControlsSettingsDialog();
        bottomBar.AddView(controlsBtn);

        var sandboxBtn = new Button(this)
        {
            Text = "Offline Sandbox",
            TextSize = 11
        };
        sandboxBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        sandboxBtn.SetTextColor(LauncherTheme.Foreground);
        sandboxBtn.SetPadding((int)(10 * density), 0, (int)(10 * density), 0);
        var sandboxLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        sandboxLp.SetMargins(0, 0, (int)(8 * density), 0);
        sandboxBtn.LayoutParameters = sandboxLp;
        sandboxBtn.Click += (_, _) => LaunchOfflineSandbox();
        bottomBar.AddView(sandboxBtn);

        var versionBtn = new Button(this)
        {
            Text = GetCurrentAppVersion(),
            TextSize = 10
        };
        versionBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        versionBtn.SetTextColor(LauncherTheme.SubText);
        versionBtn.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);
        versionBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        versionBtn.Click += async (_, _) => await CheckForUpdatesAsync(userInitiated: true);
        bottomBar.AddView(versionBtn);

        mainLayout.AddView(bottomBar);
        root.AddView(mainLayout);

        _overlayBox = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        _overlayBox.SetBackgroundColor(Color.ParseColor("#CC18181A"));
        _overlayBox.SetGravity(GravityFlags.Center);
        _overlayBox.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var modal = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        modal.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 6, LauncherTheme.NanoGold);
        modal.SetPadding((int)(24 * density), (int)(20 * density), (int)(24 * density), (int)(20 * density));
        modal.LayoutParameters = new LinearLayout.LayoutParams((int)(340 * density), ViewGroup.LayoutParams.WrapContent);

        _overlayText = new TextView(this)
        {
            Text = "Connecting...",
            TextSize = 14,
            Typeface = Typeface.DefaultBold
        };
        _overlayText.SetTextColor(LauncherTheme.Foreground);
        _overlayText.Gravity = GravityFlags.Center;
        modal.AddView(_overlayText);

        _overlayProgress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = true,
            Max = 100
        };
        _overlayProgress.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(14 * density));
        modal.AddView(_overlayProgress);

        var cancelBtn = new Button(this)
        {
            Text = "Cancel",
            TextSize = 12
        };
        cancelBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        cancelBtn.SetTextColor(LauncherTheme.Foreground);
        var cancelLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(32 * density))
        {
            TopMargin = (int)(12 * density)
        };
        cancelBtn.LayoutParameters = cancelLp;
        cancelBtn.Click += (_, _) =>
        {
            ContentDownloadService.Cancel();
            _overlayBox.Visibility = ViewStates.Gone;
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        };
        modal.AddView(cancelBtn);

        _overlayBox.AddView(modal);
        root.AddView(_overlayBox);

        SetContentView(root);
    }

    private void LoadLogo(ImageView imageView)
    {
        try
        {
            var resName = typeof(LauncherActivity).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("logo-long.png", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = typeof(LauncherActivity).Assembly.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    var bmp = BitmapFactory.DecodeStream(stream);
                    imageView.SetImageBitmap(bmp);
                    return;
                }
            }
        }
        catch {}
    }

    private Button CreateHeaderLinkButton(string text, Action onClick)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var btn = new Button(this)
        {
            Text = text,
            TextSize = 11
        };
        btn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        btn.SetTextColor(LauncherTheme.Foreground);
        btn.SetPadding((int)(10 * density), 0, (int)(10 * density), 0);
        btn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, (int)(32 * density))
        {
            RightMargin = (int)(6 * density)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateTabButton(string text, bool active)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var btn = new Button(this)
        {
            Text = text,
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        btn.SetPadding((int)(14 * density), 0, (int)(14 * density), 0);
        btn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, (int)(32 * density))
        {
            RightMargin = (int)(4 * density)
        };
        ApplyTabStyle(btn, active);
        return btn;
    }

    private void ApplyTabStyle(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
            btn.SetTextColor(Color.White);
        }
        else
        {
            btn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            btn.SetTextColor(LauncherTheme.SubText);
        }
    }

    private TextView CreateTableHeaderText(string title, float weight)
    {
        var tv = new TextView(this)
        {
            Text = title,
            TextSize = 10,
            Typeface = Typeface.DefaultBold
        };
        tv.SetTextColor(LauncherTheme.NanoGold);
        tv.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, weight);
        return tv;
    }

    private void SwitchTab(Tab tab)
    {
        _activeTab = tab;
        ApplyTabStyle(_tabServersBtn, tab == Tab.Servers);
        ApplyTabStyle(_tabFavsBtn, tab == Tab.Favorites);
        ApplyTabStyle(_tabDirectBtn, tab == Tab.DirectConnect);
        RenderServerList();
    }

    private void UpdateAccountButton()
    {
        if (_accountBtn == null) return;
        var acc = _storage.GetActiveAccount();
        if (acc != null && !string.IsNullOrEmpty(acc.Token))
        {
            _accountBtn.Text = $"{acc.Username}";
            _accountBtn.SetTextColor(Color.ParseColor("#86efac"));
        }
        else if (!string.IsNullOrEmpty(_storage.Data.GuestUsername))
        {
            _accountBtn.Text = $"{_storage.Data.GuestUsername} (Guest)";
            _accountBtn.SetTextColor(LauncherTheme.Foreground);
        }
        else
        {
            _accountBtn.Text = "Log in";
            _accountBtn.SetTextColor(LauncherTheme.SubText);
        }
    }

    private async Task RefreshHubServersAsync()
    {
        _statusCountText.Text = "Querying Space Station 14 servers...";
        try
        {
            var list = await _hubClient.FetchAllServersAsync(_storage.Data.HubUrls);
            RunOnUiThread(() =>
            {
                _servers = list;
                RenderServerList();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                _statusCountText.Text = $"Failed to load hub servers: {ex.Message}";
            });
        }
    }

    private void RenderServerList()
    {
        _listLayout.RemoveAllViews();
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        if (_activeTab == Tab.DirectConnect)
        {
            RenderDirectConnectView(density);
            return;
        }

        IEnumerable<ServerListEntry> items = _servers;
        if (_activeTab == Tab.Favorites)
        {
            items = items.Where(s => _storage.IsFavorite(s.Address));
        }

        if (!string.IsNullOrWhiteSpace(_searchFilter))
        {
            var q = _searchFilter.Trim();
            items = items.Where(s =>
                (s.StatusData?.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                s.Address.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (s.StatusData?.Tags != null && s.StatusData.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))));
        }

        var sorted = items.OrderByDescending(s => s.StatusData?.Players ?? 0).ToList();
        var totalPlayers = sorted.Sum(s => s.StatusData?.Players ?? 0);
        _statusCountText.Text = $"{sorted.Count} servers online, {totalPlayers} players total";

        var idx = 0;
        foreach (var server in sorted)
        {
            var row = CreateServerTableRow(server, idx % 2 == 1, density);
            _listLayout.AddView(row);
            idx++;
        }
    }

    private View CreateServerTableRow(ServerListEntry server, bool alternate, float density)
    {
        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        row.SetBackgroundColor(alternate ? LauncherTheme.RowAlternate : LauncherTheme.Background);
        row.SetPadding((int)(12 * density), (int)(6 * density), (int)(12 * density), (int)(6 * density));
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        var nameCol = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        nameCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);

        var isFav = _storage.IsFavorite(server.Address);
        var starBtn = new TextView(this)
        {
            Text = isFav ? "★ " : "☆ ",
            TextSize = 14
        };
        starBtn.SetTextColor(isFav ? LauncherTheme.NanoGold : LauncherTheme.ForegroundMuted);
        starBtn.Clickable = true;
        starBtn.Click += (_, _) =>
        {
            _storage.ToggleFavorite(server.Address);
            RenderServerList();
        };
        nameCol.AddView(starBtn);

        var titleLayout = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        var titleText = new TextView(this)
        {
            Text = server.StatusData?.Name ?? server.Address,
            TextSize = 13,
            Typeface = Typeface.DefaultBold
        };
        titleText.SetTextColor(LauncherTheme.Foreground);
        titleLayout.AddView(titleText);

        var addrText = new TextView(this)
        {
            Text = server.Address,
            TextSize = 10
        };
        addrText.SetTextColor(LauncherTheme.SubText);
        titleLayout.AddView(addrText);

        nameCol.AddView(titleLayout);
        row.AddView(nameCol);

        var statusCol = new TextView(this)
        {
            Text = FormatRunLevel(server.StatusData?.RunLevel),
            TextSize = 11
        };
        statusCol.SetTextColor(LauncherTheme.SubText);
        statusCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.35f);
        row.AddView(statusCol);

        var players = server.StatusData?.Players ?? 0;
        var maxPlayers = server.StatusData?.SoftMaxPlayers ?? 0;
        var playersCol = new TextView(this)
        {
            Text = $"{players} / {maxPlayers}",
            TextSize = 12,
            Typeface = Typeface.DefaultBold
        };
        playersCol.SetTextColor(players > 0 ? Color.ParseColor("#22c55e") : LauncherTheme.SubText);
        playersCol.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.25f);
        row.AddView(playersCol);

        var connectBtn = new Button(this)
        {
            Text = "Connect",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        connectBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
        connectBtn.SetTextColor(Color.White);
        connectBtn.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);
        connectBtn.LayoutParameters = new LinearLayout.LayoutParams(0, (int)(30 * density), 0.3f);
        connectBtn.Click += async (_, _) => await StartConnectingAsync(server.Address);
        row.AddView(connectBtn);

        return row;
    }

    private void RenderDirectConnectView(float density)
    {
        var box = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        box.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 6, LauncherTheme.Separator);
        box.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));
        box.LayoutParameters = new LinearLayout.LayoutParams((int)(400 * density), ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = (int)(20 * density),
            Gravity = GravityFlags.CenterHorizontal
        };

        var label = new TextView(this)
        {
            Text = "Direct Server Address (ss14:// or host:port):",
            TextSize = 12
        };
        label.SetTextColor(LauncherTheme.SubText);
        box.AddView(label);

        var directInput = new EditText(this)
        {
            Text = _storage.Data.LastServer ?? "central.spacestation14.io:1212",
            TextSize = 13
        };
        directInput.SetTextColor(LauncherTheme.Foreground);
        directInput.Background = LauncherTheme.CreateBox(LauncherTheme.Background, 3, LauncherTheme.ControlMid);
        directInput.SetPadding((int)(10 * density), (int)(8 * density), (int)(10 * density), (int)(8 * density));
        box.AddView(directInput);

        var connectBtn = new Button(this)
        {
            Text = "Connect to Server",
            TextSize = 13,
            Typeface = Typeface.DefaultBold
        };
        connectBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 4);
        connectBtn.SetTextColor(Color.White);
        connectBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(38 * density))
        {
            TopMargin = (int)(12 * density)
        };
        connectBtn.Click += async (_, _) =>
        {
            var addr = directInput.Text?.Trim();
            if (!string.IsNullOrEmpty(addr))
            {
                await StartConnectingAsync(addr);
            }
        };
        box.AddView(connectBtn);

        _listLayout.AddView(box);
    }

    private string FormatRunLevel(int? runLevel)
    {
        return runLevel switch
        {
            0 => "Lobby",
            1 => "In-Round",
            2 => "Post-Round",
            _ => "Online"
        };
    }

    private void ShowAccountDialog()
    {
        var builder = new AlertDialog.Builder(this);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetBackgroundColor(LauncherTheme.PopupBackground);
        view.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));

        var title = new TextView(this)
        {
            Text = "Space Station 14 Account",
            TextSize = 16,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(0, 0, 0, (int)(10 * density));
        view.AddView(title);

        var current = _storage.GetActiveAccount();
        if (current != null && !string.IsNullOrEmpty(current.Token))
        {
            var userText = new TextView(this)
            {
                Text = $"Active Account: {current.Username}\nUser ID: {current.UserId}",
                TextSize = 13
            };
            userText.SetTextColor(Color.ParseColor("#86efac"));
            view.AddView(userText);

            var switchGuestBtn = new Button(this)
            {
                Text = "Play as Guest"
            };
            switchGuestBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            switchGuestBtn.SetTextColor(LauncherTheme.Foreground);
            switchGuestBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density))
            {
                TopMargin = (int)(8 * density)
            };
            switchGuestBtn.Click += (_, _) =>
            {
                _storage.Data.ActiveUserId = null;
                _storage.Save();
                UpdateAccountButton();
                Toast.MakeText(this, "Switched to Guest mode", ToastLength.Short)?.Show();
            };
            view.AddView(switchGuestBtn);

            var logoutBtn = new Button(this)
            {
                Text = "Log Out"
            };
            logoutBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            logoutBtn.SetTextColor(Color.ParseColor("#ef4444"));
            logoutBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density))
            {
                TopMargin = (int)(8 * density)
            };
            logoutBtn.Click += async (_, _) =>
            {
                await _authClient.LogoutAsync(current.Token);
                _storage.RemoveAccount(current.UserId);
                try { File.Delete(Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json")); } catch {}
                UpdateAccountButton();
                Toast.MakeText(this, "Logged out", ToastLength.Short)?.Show();
            };
            view.AddView(logoutBtn);
        }
        else
        {
            var uInput = new EditText(this) { Hint = "Username or Email", TextSize = 13 };
            uInput.SetHintTextColor(LauncherTheme.ForegroundMuted);
            uInput.SetTextColor(LauncherTheme.Foreground);
            uInput.Background = LauncherTheme.CreateBox(LauncherTheme.Background, 3, LauncherTheme.ControlMid);
            uInput.SetPadding((int)(10 * density), (int)(6 * density), (int)(10 * density), (int)(6 * density));
            view.AddView(uInput);

            var pInput = new EditText(this)
            {
                Hint = "Password",
                TextSize = 13,
                InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword
            };
            pInput.SetHintTextColor(LauncherTheme.ForegroundMuted);
            pInput.SetTextColor(LauncherTheme.Foreground);
            pInput.Background = LauncherTheme.CreateBox(LauncherTheme.Background, 3, LauncherTheme.ControlMid);
            pInput.SetPadding((int)(10 * density), (int)(6 * density), (int)(10 * density), (int)(6 * density));
            pInput.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = (int)(6 * density)
            };
            view.AddView(pInput);

            var loginBtn = new Button(this)
            {
                Text = "Log In",
                TextSize = 13,
                Typeface = Typeface.DefaultBold
            };
            loginBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
            loginBtn.SetTextColor(Color.White);
            loginBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density))
            {
                TopMargin = (int)(10 * density)
            };
            loginBtn.Click += async (_, _) =>
            {
                var res = await _authClient.AuthenticateAsync(uInput.Text ?? "", pInput.Text ?? "");
                if (res.Success && res.Account != null)
                {
                    _storage.AddOrUpdateAccount(res.Account);
                    try
                    {
                        var authFile = Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json");
                        var json = $"{{\"token\":\"{res.Account.Token}\",\"username\":\"{res.Account.Username}\",\"userId\":\"{res.Account.UserId}\"}}";
                        File.WriteAllText(authFile, json);
                    }
                    catch {}
                    UpdateAccountButton();
                    Toast.MakeText(this, $"Logged in as {res.Account.Username}", ToastLength.Short)?.Show();
                }
                else
                {
                    Toast.MakeText(this, $"Error: {res.ErrorMessage}", ToastLength.Long)?.Show();
                }
            };
            view.AddView(loginBtn);

            var regBtn = new Button(this)
            {
                Text = "Register SS14 Account (Web)"
            };
            regBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            regBtn.SetTextColor(LauncherTheme.Foreground);
            regBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(34 * density))
            {
                TopMargin = (int)(6 * density)
            };
            regBtn.Click += (_, _) => OpenUrl("https://account.spacestation14.com/Identity/Account/Register");
            view.AddView(regBtn);

            var guestLabel = new TextView(this)
            {
                Text = "─── Or Play as Guest ───",
                TextSize = 11
            };
            guestLabel.SetTextColor(LauncherTheme.SubText);
            guestLabel.SetPadding(0, (int)(10 * density), 0, (int)(4 * density));
            view.AddView(guestLabel);

            var guestInput = new EditText(this)
            {
                Text = _storage.Data.GuestUsername,
                Hint = "Guest Player Name",
                TextSize = 13
            };
            guestInput.SetTextColor(LauncherTheme.Foreground);
            guestInput.Background = LauncherTheme.CreateBox(LauncherTheme.Background, 3, LauncherTheme.ControlMid);
            guestInput.SetPadding((int)(10 * density), (int)(6 * density), (int)(10 * density), (int)(6 * density));
            view.AddView(guestInput);

            var guestSaveBtn = new Button(this)
            {
                Text = "Save Guest Name"
            };
            guestSaveBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            guestSaveBtn.SetTextColor(LauncherTheme.Foreground);
            guestSaveBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(34 * density))
            {
                TopMargin = (int)(6 * density)
            };
            guestSaveBtn.Click += (_, _) =>
            {
                _storage.SetGuestUsername(guestInput.Text ?? "AndroidPlayer");
                UpdateAccountButton();
                Toast.MakeText(this, "Guest name saved", ToastLength.Short)?.Show();
            };
            view.AddView(guestSaveBtn);
        }

        builder.SetView(view);
        builder.SetPositiveButton("Close", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private sealed class MobileButtonConfig
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Key { get; set; } = "";
        public float Size { get; set; } = 50f;
        public bool IsDeleted { get; set; }
        public bool IsCustom { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
    }

    private static List<MobileButtonConfig> GetDefaultButtonConfigs() => new()
    {
        new() { Id = "use", Label = "USE", Key = "Z", Size = 54f, X = 820, Y = 360 },
        new() { Id = "inter", Label = "INTER", Key = "E", Size = 48f, X = 760, Y = 385 },
        new() { Id = "sec", Label = "SEC", Key = "Secondary", Size = 46f, X = 705, Y = 405 },
        new() { Id = "hand", Label = "HAND", Key = "X", Size = 44f, X = 820, Y = 295 },
        new() { Id = "drop", Label = "DROP", Key = "Q", Size = 42f, X = 760, Y = 325 },
        new() { Id = "alt", Label = "ALT", Key = "Alt", Size = 42f, X = 705, Y = 345 },
        new() { Id = "pull", Label = "PULL", Key = "Control", Size = 44f, X = 60, Y = 240 },
        new() { Id = "exam", Label = "EXAM", Key = "Shift", Size = 44f, X = 116, Y = 240 },
        new() { Id = "esc", Label = "ESC", Key = "Escape", Size = 46f, X = 24, Y = 14 },
        new() { Id = "keyb", Label = "KEYB", Key = "Keyboard", Size = 46f, X = 900, Y = 14 },
    };

    private List<MobileButtonConfig> LoadAllButtonsFromPrefs()
    {
        var list = GetDefaultButtonConfigs();
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        var layout = prefs?.GetString("mobile_layout", "") ?? "";
        if (string.IsNullOrWhiteSpace(layout))
            return list;

        var entries = layout.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(':');
            if (parts.Length < 2) continue;

            if (parts[0] == "del" && parts.Length >= 2)
            {
                var delId = parts[1];
                if (delId == "edit" || delId == "hide" || delId == "esc" || delId == "keyb") continue;
                var existing = list.Find(b => b.Id == delId);
                if (existing != null) existing.IsDeleted = true;
                continue;
            }

            if (parts[0] == "custom" && parts.Length >= 5)
            {
                var id = parts[1];
                var label = parts[2];
                var key = parts[3];
                var coords = parts[4].Split(',');
                float cx = 400, cy = 300, cSize = 50f;
                if (coords.Length == 2 && float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) cx = px;
                if (coords.Length == 2 && float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) cy = py;
                if (parts.Length >= 6 && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) cSize = ps;

                var existing = list.Find(b => b.Id == id);
                if (existing == null)
                {
                    list.Add(new MobileButtonConfig
                    {
                        Id = id,
                        Label = label,
                        Key = key,
                        Size = cSize,
                        X = cx,
                        Y = cy,
                        IsCustom = true
                    });
                }
                else
                {
                    existing.Label = label;
                    existing.Key = key;
                    existing.Size = cSize;
                    existing.X = cx;
                    existing.Y = cy;
                }
                continue;
            }

            if (parts[0] == "btn" && parts.Length >= 5)
            {
                var id = parts[1];
                var label = parts[2];
                var key = parts[3];
                var coords = parts[4].Split(',');
                var existing = list.Find(b => b.Id == id);
                if (existing != null)
                {
                    existing.Label = label;
                    existing.Key = key;
                    if (coords.Length == 2 && float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) existing.X = px;
                    if (coords.Length == 2 && float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) existing.Y = py;
                    if (parts.Length >= 6 && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) existing.Size = ps;
                }
                continue;
            }

            var btnId = parts[0];
            var bCoords = parts[1].Split(',');
            var bExisting = list.Find(b => b.Id == btnId);
            if (bExisting != null)
            {
                if (bCoords.Length == 2 && float.TryParse(bCoords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) bExisting.X = px;
                if (bCoords.Length == 2 && float.TryParse(bCoords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) bExisting.Y = py;
                if (parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) bExisting.Size = ps;
            }
        }

        return list;
    }

    private void SaveAllButtonsToPrefs(List<MobileButtonConfig> buttons)
    {
        var parts = new List<string>();
        foreach (var b in buttons)
        {
            if (b.Id == "edit" || b.Id == "hide") continue;

            if (b.IsDeleted)
            {
                if (b.Id == "esc" || b.Id == "keyb") continue;
                parts.Add($"del:{b.Id}");
                continue;
            }

            if (b.IsCustom)
            {
                parts.Add($"custom:{b.Id}:{b.Label}:{b.Key}:{b.X.ToString("F1", CultureInfo.InvariantCulture)},{b.Y.ToString("F1", CultureInfo.InvariantCulture)}:{b.Size.ToString("F0", CultureInfo.InvariantCulture)}");
            }
            else
            {
                parts.Add($"btn:{b.Id}:{b.Label}:{b.Key}:{b.X.ToString("F1", CultureInfo.InvariantCulture)},{b.Y.ToString("F1", CultureInfo.InvariantCulture)}:{b.Size.ToString("F0", CultureInfo.InvariantCulture)}");
            }
        }

        var serialized = string.Join(";", parts);
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        prefs?.Edit()?.PutString("mobile_layout", serialized)?.Apply();
        MainActivity.LaunchBuildCVars["mobile.controls_layout"] = serialized;
    }

    private void ShowControlsSettingsDialog()
    {
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        var density = Resources!.DisplayMetrics!.Density;

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Touch Controls Settings");

        var scrollView = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));
        scrollView.AddView(view);

        var scaleLabel = new TextView(this)
        {
            Text = "Controls Scale:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        scaleLabel.SetTextColor(LauncherTheme.Foreground);
        view.AddView(scaleLabel);

        var currentScale = prefs?.GetFloat("mobile_scale", 1.0f) ?? 1.0f;
        var scaleBtnsLayout = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var scales = new (string text, float val)[]
        {
            ("80%", 0.8f),
            ("100%", 1.0f),
            ("120%", 1.2f),
            ("140%", 1.4f)
        };

        var scaleBtnList = new List<Button>();
        foreach (var (text, val) in scales)
        {
            var btn = new Button(this)
            {
                Text = text,
                TextSize = 11
            };
            btn.Background = Math.Abs(currentScale - val) < 0.05f 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            btn.SetTextColor(LauncherTheme.Foreground);
            var lp = new LinearLayout.LayoutParams(0, (int)(32 * density), 1f);
            lp.SetMargins((int)(2 * density), (int)(4 * density), (int)(2 * density), (int)(10 * density));
            btn.LayoutParameters = lp;
            btn.Click += (_, _) =>
            {
                prefs?.Edit()?.PutFloat("mobile_scale", val)?.Apply();
                MainActivity.LaunchBuildCVars["mobile.controls_scale"] = val.ToString("F1", CultureInfo.InvariantCulture);
                foreach (var b in scaleBtnList)
                    b.Background = (b == btn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            };
            scaleBtnList.Add(btn);
            scaleBtnsLayout.AddView(btn);
        }
        view.AddView(scaleBtnsLayout);

        var opacityLabel = new TextView(this)
        {
            Text = "Controls Opacity:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        opacityLabel.SetTextColor(LauncherTheme.Foreground);
        view.AddView(opacityLabel);

        var currentOpacity = prefs?.GetFloat("mobile_opacity", 0.85f) ?? 0.85f;
        var opacityBtnsLayout = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var opacities = new (string text, float val)[]
        {
            ("50%", 0.5f),
            ("70%", 0.7f),
            ("85%", 0.85f),
            ("100%", 1.0f)
        };

        var opBtnList = new List<Button>();
        foreach (var (text, val) in opacities)
        {
            var btn = new Button(this)
            {
                Text = text,
                TextSize = 11
            };
            btn.Background = Math.Abs(currentOpacity - val) < 0.05f 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            btn.SetTextColor(LauncherTheme.Foreground);
            var lp = new LinearLayout.LayoutParams(0, (int)(32 * density), 1f);
            lp.SetMargins((int)(2 * density), (int)(4 * density), (int)(2 * density), (int)(10 * density));
            btn.LayoutParameters = lp;
            btn.Click += (_, _) =>
            {
                prefs?.Edit()?.PutFloat("mobile_opacity", val)?.Apply();
                MainActivity.LaunchBuildCVars["mobile.controls_opacity"] = val.ToString("F2", CultureInfo.InvariantCulture);
                foreach (var b in opBtnList)
                    b.Background = (b == btn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            };
            opBtnList.Add(btn);
            opacityBtnsLayout.AddView(btn);
        }
        view.AddView(opacityBtnsLayout);

        var listHeader = new TextView(this)
        {
            Text = "Individual Button Settings:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        listHeader.SetTextColor(LauncherTheme.Foreground);
        var lhp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lhp.SetMargins(0, (int)(10 * density), 0, (int)(6 * density));
        listHeader.LayoutParameters = lhp;
        view.AddView(listHeader);

        var buttons = LoadAllButtonsFromPrefs();
        var buttonsContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        view.AddView(buttonsContainer);

        void PopulateButtonsList()
        {
            buttonsContainer.RemoveAllViews();
            foreach (var b in buttons)
            {
                var card = new LinearLayout(this)
                {
                    Orientation = Orientation.Horizontal,
                    BaselineAligned = false
                };
                card.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
                card.SetPadding((int)(10 * density), (int)(6 * density), (int)(10 * density), (int)(6 * density));
                var clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                clp.SetMargins(0, (int)(2 * density), 0, (int)(2 * density));
                card.LayoutParameters = clp;

                var textCol = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var tclp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                textCol.LayoutParameters = tclp;

                var title = new TextView(this)
                {
                    Text = b.Label + (b.IsDeleted ? " (Hidden)" : ""),
                    TextSize = 12,
                    Typeface = global::Android.Graphics.Typeface.DefaultBold
                };
                title.SetTextColor(b.IsDeleted ? LauncherTheme.SubText : LauncherTheme.Foreground);
                textCol.AddView(title);

                var subtitle = new TextView(this)
                {
                    Text = $"Key: {b.Key} | Size: {b.Size:F0}px" + (b.IsCustom ? " | Custom" : ""),
                    TextSize = 10
                };
                subtitle.SetTextColor(LauncherTheme.SubText);
                textCol.AddView(subtitle);
                card.AddView(textCol);

                var curBtn = b;

                var editBtn = new Button(this)
                {
                    Text = "Edit",
                    TextSize = 10
                };
                editBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
                editBtn.SetTextColor(LauncherTheme.Foreground);
                var elp = new LinearLayout.LayoutParams((int)(56 * density), (int)(32 * density));
                elp.SetMargins((int)(3 * density), 0, (int)(3 * density), 0);
                editBtn.LayoutParameters = elp;
                editBtn.Click += (_, _) =>
                {
                    ShowEditButtonDialog(curBtn, () =>
                    {
                        SaveAllButtonsToPrefs(buttons);
                        PopulateButtonsList();
                    });
                };
                card.AddView(editBtn);

                var toggleBtn = new Button(this)
                {
                    Text = curBtn.IsDeleted ? "Show" : (curBtn.IsCustom ? "Del" : "Hide"),
                    TextSize = 10
                };
                toggleBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
                toggleBtn.SetTextColor(curBtn.IsDeleted ? LauncherTheme.ControlHigh : LauncherTheme.SubText);
                var tglp = new LinearLayout.LayoutParams((int)(56 * density), (int)(32 * density));
                toggleBtn.LayoutParameters = tglp;
                toggleBtn.Click += (_, _) =>
                {
                    if (curBtn.IsCustom && !curBtn.IsDeleted)
                    {
                        buttons.Remove(curBtn);
                    }
                    else
                    {
                        curBtn.IsDeleted = !curBtn.IsDeleted;
                    }
                    SaveAllButtonsToPrefs(buttons);
                    PopulateButtonsList();
                };
                card.AddView(toggleBtn);

                buttonsContainer.AddView(card);
            }
        }

        PopulateButtonsList();

        var addBtn = new Button(this)
        {
            Text = "+ Add Custom Button",
            TextSize = 12
        };
        addBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
        addBtn.SetTextColor(LauncherTheme.Foreground);
        var alp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density));
        alp.SetMargins(0, (int)(8 * density), 0, (int)(4 * density));
        addBtn.LayoutParameters = alp;
        addBtn.Click += (_, _) =>
        {
            ShowAddButtonDialog((newBtn) =>
            {
                buttons.Add(newBtn);
                SaveAllButtonsToPrefs(buttons);
                PopulateButtonsList();
            });
        };
        view.AddView(addBtn);

        var resetBtn = new Button(this)
        {
            Text = "Reset All Controls to Default",
            TextSize = 11
        };
        resetBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        resetBtn.SetTextColor(LauncherTheme.SubText);
        var rlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(34 * density));
        rlp.SetMargins(0, (int)(4 * density), 0, 0);
        resetBtn.LayoutParameters = rlp;
        resetBtn.Click += (_, _) =>
        {
            prefs?.Edit()?.Remove("mobile_layout")?.Apply();
            MainActivity.LaunchBuildCVars["mobile.controls_layout"] = "";
            buttons = GetDefaultButtonConfigs();
            PopulateButtonsList();
            Toast.MakeText(this, "Controls layout reset to default", ToastLength.Short)?.Show();
        };
        view.AddView(resetBtn);

        var infoText = new TextView(this)
        {
            Text = "In-game: tap EDIT at the top to reposition buttons by dragging.",
            TextSize = 10
        };
        infoText.SetTextColor(LauncherTheme.SubText);
        var ilp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ilp.SetMargins(0, (int)(8 * density), 0, (int)(4 * density));
        infoText.LayoutParameters = ilp;
        view.AddView(infoText);

        builder.SetView(scrollView);
        builder.SetPositiveButton("Close", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private void ShowEditButtonDialog(MobileButtonConfig btn, Action onSaved)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle($"Configure: {btn.Label}");

        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));

        var nameLabel = new TextView(this) { Text = "Button Label:", TextSize = 11 };
        nameLabel.SetTextColor(LauncherTheme.SubText);
        view.AddView(nameLabel);

        var nameInput = new EditText(this) { Text = btn.Label, TextSize = 13 };
        nameInput.SetTextColor(LauncherTheme.Foreground);
        nameInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(nameInput);

        var keyLabel = new TextView(this) { Text = "Key Code (e.g. E, Space, Return, Shift):", TextSize = 11 };
        keyLabel.SetTextColor(LauncherTheme.SubText);
        var klp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        klp.SetMargins(0, (int)(8 * density), 0, 0);
        keyLabel.LayoutParameters = klp;
        view.AddView(keyLabel);

        var keyInput = new EditText(this) { Text = btn.Key, TextSize = 13 };
        keyInput.SetTextColor(LauncherTheme.Foreground);
        keyInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(keyInput);

        var quickRow1 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var qlp1 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        qlp1.SetMargins(0, (int)(4 * density), 0, 0);
        quickRow1.LayoutParameters = qlp1;
        string[] quickKeys1 = ["E", "F", "R", "C", "T", "Tab", "Space"];
        foreach (var k in quickKeys1)
        {
            var qBtn = new Button(this) { Text = k, TextSize = 9 };
            qBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            qBtn.SetTextColor(LauncherTheme.Foreground);
            var qp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            qp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            qBtn.LayoutParameters = qp;
            var keyVal = k;
            qBtn.Click += (_, _) =>
            {
                keyInput.Text = keyVal;
                if (string.IsNullOrWhiteSpace(nameInput.Text) || nameInput.Text == btn.Key)
                    nameInput.Text = keyVal;
            };
            quickRow1.AddView(qBtn);
        }
        view.AddView(quickRow1);

        var quickRow2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var qlp2 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        qlp2.SetMargins(0, (int)(4 * density), 0, 0);
        quickRow2.LayoutParameters = qlp2;
        string[] quickKeys2 = ["Shift", "Control", "Alt", "1", "2", "3", "4", "Return"];
        foreach (var k in quickKeys2)
        {
            var qBtn = new Button(this) { Text = k, TextSize = 9 };
            qBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            qBtn.SetTextColor(LauncherTheme.Foreground);
            var qp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            qp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            qBtn.LayoutParameters = qp;
            var keyVal = k;
            qBtn.Click += (_, _) =>
            {
                keyInput.Text = keyVal;
                if (string.IsNullOrWhiteSpace(nameInput.Text) || nameInput.Text == btn.Key)
                    nameInput.Text = keyVal;
            };
            quickRow2.AddView(qBtn);
        }
        view.AddView(quickRow2);

        var sizeLabel = new TextView(this) { Text = "Button Size:", TextSize = 11 };
        sizeLabel.SetTextColor(LauncherTheme.SubText);
        var slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        slp.SetMargins(0, (int)(8 * density), 0, 0);
        sizeLabel.LayoutParameters = slp;
        view.AddView(sizeLabel);

        var sizeRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var sizes = new (string text, float val)[]
        {
            ("40px", 40f),
            ("50px", 50f),
            ("60px", 60f),
            ("72px", 72f)
        };
        var currentSize = btn.Size;
        var sizeBtnList = new List<Button>();
        foreach (var (st, sv) in sizes)
        {
            var sBtn = new Button(this) { Text = st, TextSize = 9 };
            sBtn.Background = Math.Abs(currentSize - sv) < 5f 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 2) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            sBtn.SetTextColor(LauncherTheme.Foreground);
            var sp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            sp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            sBtn.LayoutParameters = sp;
            var valCapture = sv;
            sBtn.Click += (_, _) =>
            {
                currentSize = valCapture;
                foreach (var sb in sizeBtnList)
                    sb.Background = (sb == sBtn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 2) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            };
            sizeBtnList.Add(sBtn);
            sizeRow.AddView(sBtn);
        }
        view.AddView(sizeRow);

        builder.SetView(view);
        builder.SetPositiveButton("Save", (_, _) =>
        {
            btn.Label = string.IsNullOrWhiteSpace(nameInput.Text) ? "BTN" : nameInput.Text.Trim();
            btn.Key = string.IsNullOrWhiteSpace(keyInput.Text) ? btn.Label : keyInput.Text.Trim();
            btn.Size = currentSize;
            onSaved();
            Toast.MakeText(this, $"Saved button \"{btn.Label}\"", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private void ShowAddButtonDialog(Action<MobileButtonConfig> onAdded)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("New Screen Button");

        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));

        var nameLabel = new TextView(this) { Text = "Button Label (e.g. E, F, TAB):", TextSize = 11 };
        nameLabel.SetTextColor(LauncherTheme.SubText);
        view.AddView(nameLabel);

        var nameInput = new EditText(this) { Text = "E", TextSize = 13 };
        nameInput.SetTextColor(LauncherTheme.Foreground);
        nameInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(nameInput);

        var keyLabel = new TextView(this) { Text = "Key Code (e.g. E, Space, Return, Shift):", TextSize = 11 };
        keyLabel.SetTextColor(LauncherTheme.SubText);
        var klp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        klp.SetMargins(0, (int)(8 * density), 0, 0);
        keyLabel.LayoutParameters = klp;
        view.AddView(keyLabel);

        var keyInput = new EditText(this) { Text = "E", TextSize = 13 };
        keyInput.SetTextColor(LauncherTheme.Foreground);
        keyInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(keyInput);

        builder.SetView(view);
        builder.SetPositiveButton("Add", (_, _) =>
        {
            var btnText = string.IsNullOrWhiteSpace(nameInput.Text) ? "BTN" : nameInput.Text.Trim();
            var btnKey = string.IsNullOrWhiteSpace(keyInput.Text) ? btnText : keyInput.Text.Trim();
            var id = $"custom_{Guid.NewGuid().ToString()[..6]}";

            var newBtn = new MobileButtonConfig
            {
                Id = id,
                Label = btnText,
                Key = btnKey,
                Size = 52f,
                X = 400f,
                Y = 300f,
                IsCustom = true
            };

            onAdded(newBtn);
            Toast.MakeText(this, $"Button \"{btnText}\" added!", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private async Task StartConnectingAsync(string address)
    {
        _storage.SetLastServer(address);
        _overlayBox.Visibility = ViewStates.Visible;
        _overlayProgress.Indeterminate = true;
        _overlayText.Text = "Resolving server build info...";
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        try
        {
            if (!UriHelper.TryParseSs14Uri(address, out var parsedAddress))
            {
                throw new FormatException($"Invalid server address: {address}");
            }

            var serverInfo = await _hubClient.FetchServerInfoAsync(address);
            var build = serverInfo?.Build;

            global::Android.Util.Log.Info("SS14Launcher", $"Server info: connectAddr={serverInfo?.ConnectAddress}, hasBuild={build != null}, acz={build?.Acz}, engineVer={build?.EngineVersion}, manifestHash={build?.ManifestHash}");

            var cacheDir = Path.Combine(FilesDir?.AbsolutePath ?? AppContext.BaseDirectory, "content_cache");
            Directory.CreateDirectory(cacheDir);

            string? targetZip = null;

            var expectedHash = build?.ManifestHash?.ToUpperInvariant() ?? build?.Hash?.ToUpperInvariant();
            if (!string.IsNullOrEmpty(expectedHash))
            {
                var cachedZip = Path.Combine(cacheDir, $"{expectedHash}.zip");
                if (File.Exists(cachedZip) && new FileInfo(cachedZip).Length > 0)
                {
                    global::Android.Util.Log.Info("SS14Launcher", $"Found cached content zip: {cachedZip}");
                    targetZip = cachedZip;
                }
            }

            if (targetZip != null && File.Exists(targetZip))
            {
                Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                _overlayText.Text = "Launching game...";
                LaunchGameWithZip(targetZip, serverInfo, parsedAddress.ToString());
                return;
            }

            if (build == null)
            {
                Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                _overlayText.Text = "Launching game...";
                LaunchGameWithZip(null, serverInfo, parsedAddress.ToString());
                return;
            }

            _pendingServerInfo = serverInfo;
            _pendingServerAddress = parsedAddress.ToString();

            _overlayProgress.Indeterminate = true;
            _overlayText.Text = "Downloading server content (check notification)...";

            ContentDownloadService.Start(this, build, address, cacheDir);
        }
        catch (Exception ex)
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Gone;
            global::Android.Util.Log.Error("SS14Launcher", $"StartConnectingAsync error: {ex}");
            RunOnUiThread(() =>
            {
                Toast.MakeText(this, $"Connection error: {ex.Message}", ToastLength.Long)?.Show();
            });
        }
    }

    private void OnServiceProgressChanged(int cur, int tot, string status)
    {
        RunOnUiThread(() =>
        {
            if (_overlayBox.Visibility != ViewStates.Visible)
            {
                _overlayBox.Visibility = ViewStates.Visible;
                Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            }

            _overlayProgress.Indeterminate = tot <= 0;
            if (tot > 0)
            {
                _overlayProgress.Max = tot;
                _overlayProgress.Progress = cur;
            }
            _overlayText.Text = status;
        });
    }

    private void OnServiceDownloadCompleted(string targetZip)
    {
        RunOnUiThread(() =>
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayText.Text = "Launching game...";
            LaunchGameWithZip(targetZip, _pendingServerInfo, _pendingServerAddress ?? "");
        });
    }

    private void OnServiceDownloadFailed(string error)
    {
        RunOnUiThread(() =>
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Gone;
            Toast.MakeText(this, $"Download failed: {error}", ToastLength.Long)?.Show();
        });
    }

    private void LaunchGameWithZip(string? targetZip, ServerInfo? serverInfo, string ss14Address)
    {
        _overlayBox.Visibility = ViewStates.Gone;

        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        if (targetZip != null && File.Exists(targetZip))
        {
            intent.PutExtra("mount_zip", targetZip);
        }

        var parsedAddr = UriHelper.TryParseSs14Uri(ss14Address, out var pAddr) ? pAddr : new Uri("ss14://127.0.0.1:1212");
        var realConnectAddr = UriHelper.GetConnectAddress(serverInfo, parsedAddr);

        global::Android.Util.Log.Info("SS14Launcher", $"Launching MainActivity: connect_addr={realConnectAddr}, ss14_addr={ss14Address}, mount_zip={targetZip}");

        intent.PutExtra("connect_addr", realConnectAddr);
        intent.PutExtra("ss14_addr", ss14Address);

        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        if (prefs != null)
        {
            var s = prefs.GetFloat("mobile_scale", 1.0f);
            intent.PutExtra("cvar_build_mobile.controls_scale", s.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            var o = prefs.GetFloat("mobile_opacity", 0.85f);
            intent.PutExtra("cvar_build_mobile.controls_opacity", o.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(serverInfo?.Auth?.PublicKey))
        {
            intent.PutExtra("pubkey", serverInfo.Auth.PublicKey);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_PUBKEY", serverInfo.Auth.PublicKey);
        }

        var build = serverInfo?.Build;
        if (build != null)
        {
            if (!string.IsNullOrEmpty(build.EngineVersion))
            {
                intent.PutExtra("engine_version", build.EngineVersion);
                intent.PutExtra("cvar_build_engine_version", build.EngineVersion);
            }
            if (!string.IsNullOrEmpty(build.ManifestHash)) intent.PutExtra("cvar_build_manifest_hash", build.ManifestHash);
            if (!string.IsNullOrEmpty(build.ForkId)) intent.PutExtra("cvar_build_fork_id", build.ForkId);
            if (!string.IsNullOrEmpty(build.Version)) intent.PutExtra("cvar_build_version", build.Version);
            if (!string.IsNullOrEmpty(build.Hash)) intent.PutExtra("cvar_build_hash", build.Hash);
            if (!string.IsNullOrEmpty(build.DownloadUrl)) intent.PutExtra("cvar_build_download_url", build.DownloadUrl);
            if (!string.IsNullOrEmpty(build.ManifestUrl)) intent.PutExtra("cvar_build_manifest_url", build.ManifestUrl);
            if (!string.IsNullOrEmpty(build.ManifestDownloadUrl)) intent.PutExtra("cvar_build_manifest_download_url", build.ManifestDownloadUrl);
        }

        var acc = _storage.GetActiveAccount();
        if (acc != null && !string.IsNullOrEmpty(acc.Token))
        {
            intent.PutExtra("username", acc.Username);
            intent.PutExtra("token", acc.Token);
            intent.PutExtra("userid", acc.UserId);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", acc.Token);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", acc.UserId);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");
        }
        else
        {
            string? fallbackUser = null;
            try
            {
                var authPath = Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json");
                if (File.Exists(authPath))
                {
                    var json = File.ReadAllText(authPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var user = root.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                    var userid = root.TryGetProperty("userId", out var id) ? id.GetString() : (root.TryGetProperty("userid", out var id2) ? id2.GetString() : null);
                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(token))
                    {
                        fallbackUser = user;
                        intent.PutExtra("username", user);
                        intent.PutExtra("token", token);
                        if (!string.IsNullOrEmpty(userid)) intent.PutExtra("userid", userid);
                        Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", token);
                        if (!string.IsNullOrEmpty(userid)) Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", userid);
                        Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");
                    }
                }
            }
            catch {}

            if (fallbackUser == null)
            {
                intent.PutExtra("username", _storage.Data.GuestUsername);
            }
        }

        StartActivity(intent);
    }

    private void LaunchOfflineSandbox()
    {
        var intent = new Intent(this, typeof(MainActivity));
        StartActivity(intent);
    }

    private void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            StartActivity(intent);
        }
        catch {}
    }

    public const string CurrentAppVersion = "v1.0.2";

    public string GetCurrentAppVersion()
    {
        try
        {
            var pInfo = PackageManager?.GetPackageInfo(PackageName ?? "", 0);
            if (!string.IsNullOrEmpty(pInfo?.VersionName))
            {
                var ver = pInfo.VersionName.Trim();
                return ver.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? ver : "v" + ver;
            }
        }
        catch {}
        return CurrentAppVersion;
    }

    private void ShowDisclaimerDialog()
    {
        var builder = new AlertDialog.Builder(this);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var scroll = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetBackgroundColor(LauncherTheme.PopupBackground);
        view.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));

        var title = new TextView(this)
        {
            Text = "Unofficial Mobile Client",
            TextSize = 16,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(0, 0, 0, (int)(10 * density));
        view.AddView(title);

        var msgText = new TextView(this)
        {
            Text = "This Space Station 14 Android port is an UNOFFICIAL, community-driven client modification (proof of concept).\n\n" +
                   "• It is NOT developed, provided, or supported by Wizard's Den or Space Wizards Federation.\n\n" +
                   "• Play at your own risk: Stability, controls, and performance vary depending on your device.\n\n" +
                   "• DO NOT report mobile-specific issues (crashes, low FPS, touch UI bugs) via in-game 'ahelp' or to official Wizard's Den staff/mentors.\n\n" +
                   "• If you encounter bugs or performance problems, report them directly to the GitHub Issues section.",
            TextSize = 12
        };
        msgText.SetTextColor(LauncherTheme.Foreground);
        msgText.SetPadding(0, 0, 0, (int)(14 * density));
        view.AddView(msgText);

        var btnRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        btnRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        var issuesBtn = new Button(this)
        {
            Text = "GitHub Issues",
            TextSize = 11
        };
        issuesBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        issuesBtn.SetTextColor(LauncherTheme.Foreground);
        var issuesLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1f)
        {
            RightMargin = (int)(6 * density)
        };
        issuesBtn.LayoutParameters = issuesLp;
        issuesBtn.Click += (_, _) => OpenUrl("https://github.com/SolomonNumb1/space-station-14-mobile/issues");
        btnRow.AddView(issuesBtn);

        AlertDialog? dialog = null;

        var agreeBtn = new Button(this)
        {
            Text = "I Understand & Agree",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        agreeBtn.Background = LauncherTheme.CreateBox(Color.ParseColor("#2e7d32"), 3);
        agreeBtn.SetTextColor(LauncherTheme.Foreground);
        var agreeLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1.2f);
        agreeBtn.LayoutParameters = agreeLp;
        agreeBtn.Click += (_, _) =>
        {
            _storage.Data.DisclaimerAccepted = true;
            _storage.Save();
            dialog?.Dismiss();
            _ = CheckForUpdatesAsync();
        };
        btnRow.AddView(agreeBtn);

        view.AddView(btnRow);
        scroll.AddView(view);
        builder.SetView(scroll);
        builder.SetCancelable(false);

        dialog = builder.Create();
        dialog.Show();
    }

    private async Task CheckForUpdatesAsync(bool userInitiated = false)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SS14-Android-Launcher");

            var json = await http.GetStringAsync("https://api.github.com/repos/SolomonNumb1/space-station-14-mobile/releases/latest");
            var release = JsonSerializer.Deserialize<GitHubReleaseInfo>(json);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                if (userInitiated)
                {
                    RunOnUiThread(() => Toast.MakeText(this, "Could not check for updates.", ToastLength.Short)?.Show());
                }
                return;
            }

            var currentVer = GetCurrentAppVersion();
            if (IsNewerVersion(release.TagName, currentVer))
            {
                RunOnUiThread(() => ShowUpdateDialog(release, currentVer));
            }
            else if (userInitiated)
            {
                RunOnUiThread(() => Toast.MakeText(this, $"You are on the latest version ({currentVer})", ToastLength.Short)?.Show());
            }
        }
        catch (Exception ex)
        {
            if (userInitiated)
            {
                RunOnUiThread(() => Toast.MakeText(this, $"Update check failed: {ex.Message}", ToastLength.Short)?.Show());
            }
        }
    }

    private static bool IsNewerVersion(string latestTag, string currentTag)
    {
        var lStr = latestTag.TrimStart('v', 'V').Trim();
        var cStr = currentTag.TrimStart('v', 'V').Trim();

        if (Version.TryParse(lStr, out var lVer) && Version.TryParse(cStr, out var cVer))
        {
            return lVer > cVer;
        }

        var lParts = lStr.Split('.');
        var cParts = cStr.Split('.');
        var len = Math.Max(lParts.Length, cParts.Length);
        for (var i = 0; i < len; i++)
        {
            var lNum = i < lParts.Length && int.TryParse(lParts[i], out var p1) ? p1 : 0;
            var cNum = i < cParts.Length && int.TryParse(cParts[i], out var p2) ? p2 : 0;
            if (lNum > cNum) return true;
            if (lNum < cNum) return false;
        }

        return string.Compare(latestTag, currentTag, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private void ShowUpdateDialog(GitHubReleaseInfo release, string currentVer)
    {
        var builder = new AlertDialog.Builder(this);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var scroll = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetBackgroundColor(LauncherTheme.PopupBackground);
        view.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));

        var title = new TextView(this)
        {
            Text = $"New Update Available: {release.TagName}",
            TextSize = 16,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(0, 0, 0, (int)(6 * density));
        view.AddView(title);

        var verText = new TextView(this)
        {
            Text = $"Current: {currentVer}  →  New: {release.TagName}",
            TextSize = 12
        };
        verText.SetTextColor(LauncherTheme.SubText);
        verText.SetPadding(0, 0, 0, (int)(10 * density));
        view.AddView(verText);

        if (!string.IsNullOrWhiteSpace(release.Body))
        {
            var notesTitle = new TextView(this)
            {
                Text = "Changelog:",
                TextSize = 12,
                Typeface = Typeface.DefaultBold
            };
            notesTitle.SetTextColor(LauncherTheme.Foreground);
            notesTitle.SetPadding(0, 0, 0, (int)(4 * density));
            view.AddView(notesTitle);

            var notesText = new TextView(this)
            {
                Text = release.Body.Trim(),
                TextSize = 11
            };
            notesText.SetTextColor(LauncherTheme.ForegroundMuted);
            notesText.SetPadding(0, 0, 0, (int)(12 * density));
            view.AddView(notesText);
        }

        var btnRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        btnRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        scroll.AddView(view);
        builder.SetView(scroll);

        AlertDialog? dialog = null;

        var laterBtn = new Button(this)
        {
            Text = "Later",
            TextSize = 11
        };
        laterBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        laterBtn.SetTextColor(LauncherTheme.Foreground);
        var laterLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1f)
        {
            RightMargin = (int)(6 * density)
        };
        laterBtn.LayoutParameters = laterLp;
        laterBtn.Click += (_, _) => dialog?.Dismiss();
        btnRow.AddView(laterBtn);

        var downloadBtn = new Button(this)
        {
            Text = "Download APK",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        downloadBtn.Background = LauncherTheme.CreateBox(LauncherTheme.NanoGold, 3);
        downloadBtn.SetTextColor(Color.ParseColor("#111827"));
        var downloadLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1.2f);
        downloadBtn.LayoutParameters = downloadLp;

        var apkAsset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        var downloadUrl = apkAsset?.BrowserDownloadUrl ?? release.HtmlUrl ?? "https://github.com/SolomonNumb1/space-station-14-mobile/releases/latest";

        downloadBtn.Click += (_, _) =>
        {
            dialog?.Dismiss();
            OpenUrl(downloadUrl);
        };
        btnRow.AddView(downloadBtn);

        view.AddView(btnRow);

        dialog = builder.Create();
        dialog.Show();
    }
}
