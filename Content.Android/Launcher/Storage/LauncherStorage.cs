using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Android.Launcher.Models;

namespace Content.Android.Launcher.Storage;

public sealed class LauncherData
{
    [JsonPropertyName("accounts")]
    public List<AccountInfo> Accounts { get; set; } = new();

    [JsonPropertyName("activeUserId")]
    public string? ActiveUserId { get; set; }

    [JsonPropertyName("guestUsername")]
    public string GuestUsername { get; set; } = "AndroidPlayer";

    [JsonPropertyName("favorites")]
    public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("lastServer")]
    public string? LastServer { get; set; }

    [JsonPropertyName("hubUrls")]
    public List<string> HubUrls { get; set; } = new()
    {
        "https://hub.spacestation14.com/"
    };

    [JsonPropertyName("disclaimerAccepted")]
    public bool DisclaimerAccepted { get; set; } = false;
}

public sealed class LauncherStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    public LauncherData Data { get; private set; } = new();

    public LauncherStorage(string? baseDir = null)
    {
        var dir = baseDir;
        if (string.IsNullOrEmpty(dir))
        {
            try
            {
                dir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;
            }
            catch
            {
                dir = AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        dir ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "ss14_launcher_data.json");
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            var legacyPath = !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "launcher_data.json") : null;
            if (!File.Exists(_filePath) && legacyPath != null && File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _filePath, true); } catch {}
            }

            try
            {
                if (File.Exists(_filePath))
                {
                    var text = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<LauncherData>(text, JsonOptions);
                    if (data != null)
                    {
                        data.HubUrls = new List<string> { "https://hub.spacestation14.com/" };
                        Data = data;
                        CheckAndImportAuthJson(dir);
                        Save();
                        return;
                    }
                }
            }
            catch
            {
            }

            Data = new LauncherData();
            CheckAndImportAuthJson(dir);
            Save();
        }
    }

    private void CheckAndImportAuthJson(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            var authPath = Path.Combine(dir, "auth.json");
            if (Data.Accounts.Count == 0 && File.Exists(authPath))
            {
                var json = File.ReadAllText(authPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var user = root.TryGetProperty("username", out var u) ? u.GetString() : null;
                var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                var userid = root.TryGetProperty("userId", out var id) ? id.GetString() : (root.TryGetProperty("userid", out var id2) ? id2.GetString() : null);
                if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(userid))
                {
                    var acc = new AccountInfo
                    {
                        UserId = userid,
                        Username = user,
                        Token = token
                    };
                    Data.Accounts.Add(acc);
                    Data.ActiveUserId = userid;
                }
            }
        }
        catch {}
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var text = JsonSerializer.Serialize(Data, JsonOptions);
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, text);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
            }
        }
    }

    public AccountInfo? GetActiveAccount()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(Data.ActiveUserId))
                return Data.Accounts.FirstOrDefault();

            return Data.Accounts.FirstOrDefault(a => a.UserId == Data.ActiveUserId)
                   ?? Data.Accounts.FirstOrDefault();
        }
    }

    public void SetActiveAccount(string? userId)
    {
        lock (_lock)
        {
            Data.ActiveUserId = userId;
            Save();
        }
    }

    public void AddOrUpdateAccount(AccountInfo account)
    {
        lock (_lock)
        {
            Data.Accounts.RemoveAll(a => a.UserId == account.UserId || a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase));
            Data.Accounts.Add(account);
            Data.ActiveUserId = account.UserId;
            Save();
        }
    }

    public void RemoveAccount(string userId)
    {
        lock (_lock)
        {
            Data.Accounts.RemoveAll(a => a.UserId == userId);
            if (Data.ActiveUserId == userId)
            {
                Data.ActiveUserId = Data.Accounts.FirstOrDefault()?.UserId;
            }
            Save();
        }
    }

    public bool IsFavorite(string address)
    {
        lock (_lock)
        {
            return Data.Favorites.Contains(address);
        }
    }

    public void ToggleFavorite(string address)
    {
        lock (_lock)
        {
            if (!Data.Favorites.Add(address))
            {
                Data.Favorites.Remove(address);
            }
            Save();
        }
    }

    public void SetLastServer(string address)
    {
        lock (_lock)
        {
            Data.LastServer = address;
            Save();
        }
    }

    public void SetGuestUsername(string username)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(username))
            {
                Data.GuestUsername = username.Trim();
                Save();
            }
        }
    }
}
