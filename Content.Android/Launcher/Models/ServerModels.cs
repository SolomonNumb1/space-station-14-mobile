using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Content.Android.Launcher.Models;

public sealed record ServerListEntry(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("statusData")] ServerStatus? StatusData
);

public sealed record ServerStatus(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("soft_max_players")] int SoftMaxPlayers,
    [property: JsonPropertyName("round_start_time")] string? RoundStartTime,
    [property: JsonPropertyName("run_level")] int? RunLevel,
    [property: JsonPropertyName("tags")] string[]? Tags
);

public sealed class ServerBuildInfo
{
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("engine_version")] public string? EngineVersion { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("fork_id")] public string? ForkId { get; set; }
    [JsonPropertyName("manifest_url")] public string? ManifestUrl { get; set; }
    [JsonPropertyName("manifest_download_url")] public string? ManifestDownloadUrl { get; set; }
    [JsonPropertyName("manifest_hash")] public string? ManifestHash { get; set; }
    [JsonPropertyName("acz")] public bool Acz { get; set; }
}

public sealed class ServerAuth
{
    [JsonPropertyName("mode")] public JsonElement Mode { get; set; }
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }

    [JsonIgnore]
    public string ModeString => Mode.ValueKind switch
    {
        JsonValueKind.String => Mode.GetString() ?? "",
        JsonValueKind.Number => Mode.GetInt32() switch { 0 => "optional", 1 => "required", 2 => "disabled", _ => "unknown" },
        _ => "optional"
    };
}

public sealed record ServerInfoLink(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("url")] string? Url
);

public sealed class ServerInfo
{
    [JsonPropertyName("connect_address")] public string? ConnectAddress { get; set; }
    [JsonPropertyName("auth")] public ServerAuth? Auth { get; set; }
    [JsonPropertyName("build")] public ServerBuildInfo? Build { get; set; }
    [JsonPropertyName("desc")] public string? Description { get; set; }

    [JsonPropertyName("links")] public JsonElement? LinksRaw { get; set; }

    [JsonIgnore]
    public List<ServerInfoLink> Links
    {
        get
        {
            var list = new List<ServerInfoLink>();
            if (!LinksRaw.HasValue) return list;
            var el = LinksRaw.Value;
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var icon = item.TryGetProperty("icon", out var ic) ? ic.GetString() : null;
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                    list.Add(new ServerInfoLink(name, icon, url));
                }
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    list.Add(new ServerInfoLink(prop.Name, prop.Name, prop.Value.GetString()));
                }
            }
            return list;
        }
    }
}
