using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Utility;

namespace Content.Android.Launcher.Api;

public sealed class HubClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<List<ServerListEntry>> FetchAllServersAsync(IEnumerable<string> hubUrls, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ServerListEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawHub in hubUrls)
        {
            if (string.IsNullOrWhiteSpace(rawHub)) continue;

            var hubUrl = rawHub.Trim();
            if (!hubUrl.EndsWith('/')) hubUrl += "/";

            var endpoint = hubUrl + "api/servers";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));

                var entries = await Http.GetFromJsonAsync<ServerListEntry[]>(endpoint, JsonOptions, cts.Token);
                if (entries == null) continue;

                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Address)) continue;
                    if (!result.ContainsKey(entry.Address))
                    {
                        result[entry.Address] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Launcher", $"Failed to fetch servers from {hubUrl}: {ex.Message}");
            }
        }

        return new List<ServerListEntry>(result.Values);
    }

    public async Task<ServerInfo?> FetchServerInfoAsync(string address, string? fallbackHubUrl = null, CancellationToken ct = default)
    {
        if (!UriHelper.TryParseSs14Uri(address, out var parsedAddress))
        {
            global::Android.Util.Log.Error("SS14Launcher", $"Invalid SS14 URI: {address}");
            return null;
        }

        var infoAddr = UriHelper.GetServerInfoAddress(parsedAddress);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            global::Android.Util.Log.Info("SS14Launcher", $"Querying direct server info: {infoAddr}");
            var info = await Http.GetFromJsonAsync<ServerInfo>(infoAddr, JsonOptions, cts.Token);
            if (info != null)
            {
                PostProcessServerInfo(info, parsedAddress);
                global::Android.Util.Log.Info("SS14Launcher", $"Direct server info succeeded: connect={info.ConnectAddress}, acz={info.Build?.Acz}, manifest={info.Build?.ManifestUrl}");
                return info;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SS14Launcher", $"Direct /info query to {infoAddr} failed: {ex.Message}");
        }

        var hub = fallbackHubUrl ?? "https://hub.spacestation14.com/";
        if (!hub.EndsWith('/')) hub += "/";

        try
        {
            var hubInfoUrl = $"{hub}api/servers/info?url={Uri.EscapeDataString(parsedAddress.ToString())}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            global::Android.Util.Log.Info("SS14Launcher", $"Querying hub server info: {hubInfoUrl}");
            var info = await Http.GetFromJsonAsync<ServerInfo>(hubInfoUrl, JsonOptions, cts.Token);
            if (info != null)
            {
                PostProcessServerInfo(info, parsedAddress);
                global::Android.Util.Log.Info("SS14Launcher", $"Hub server info succeeded: connect={info.ConnectAddress}, acz={info.Build?.Acz}");
                return info;
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SS14Launcher", $"Hub server info query failed: {ex.Message}");
        }

        return null;
    }

    public static void PostProcessServerInfo(ServerInfo info, Uri serverAddress)
    {
        if (info.Build is { } buildInfo && (buildInfo.Acz || string.IsNullOrEmpty(buildInfo.DownloadUrl)))
        {
            var acz = buildInfo.Acz;
            var apiAddress = UriHelper.GetServerApiAddress(serverAddress);

            buildInfo.DownloadUrl ??= new Uri(apiAddress, "client.zip").ToString();

            if (acz)
            {
                if (string.IsNullOrEmpty(buildInfo.ManifestUrl))
                    buildInfo.ManifestUrl = new Uri(apiAddress, "manifest.txt").ToString();

                if (string.IsNullOrEmpty(buildInfo.ManifestDownloadUrl))
                    buildInfo.ManifestDownloadUrl = new Uri(apiAddress, "download").ToString();
            }
        }
    }
}
