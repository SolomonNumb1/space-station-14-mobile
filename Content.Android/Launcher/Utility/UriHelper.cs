using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using Content.Android.Launcher.Models;

namespace Content.Android.Launcher.Utility;

public static class UriHelper
{
    public const string SchemeSs14 = "ss14";
    public const string SchemeSs14s = "ss14s";
    public const int DefaultServerPort = 1212;

    [Pure]
    public static Uri ParseSs14Uri(string address)
    {
        if (!TryParseSs14Uri(address, out var uri))
        {
            throw new FormatException($"Not a valid SS14 URI: {address}");
        }

        return uri;
    }

    [Pure]
    public static bool TryParseSs14Uri(string address, [NotNullWhen(true)] out Uri? uri)
    {
        address = address.Trim();
        if (!address.Contains("://"))
        {
            address = "ss14://" + address;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out uri))
        {
            return false;
        }

        if (uri.Scheme != SchemeSs14 && uri.Scheme != SchemeSs14s)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
            return false;

        return true;
    }

    [Pure]
    public static Uri GetServerApiAddress(Uri serverAddress)
    {
        var dataScheme = serverAddress.Scheme switch
        {
            SchemeSs14 => Uri.UriSchemeHttp,
            SchemeSs14s => Uri.UriSchemeHttps,
            _ => throw new ArgumentException($"Wrong URI scheme: {serverAddress.Scheme}")
        };

        var builder = new UriBuilder(serverAddress)
        {
            Scheme = dataScheme,
        };

        if (serverAddress.IsDefaultPort && serverAddress.Scheme == SchemeSs14)
        {
            builder.Port = DefaultServerPort;
        }

        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    [Pure]
    public static Uri GetServerStatusAddress(string serverAddress)
    {
        return GetServerStatusAddress(ParseSs14Uri(serverAddress));
    }

    [Pure]
    public static Uri GetServerStatusAddress(Uri serverAddress)
    {
        return new Uri(GetServerApiAddress(serverAddress), "status");
    }

    [Pure]
    public static Uri GetServerInfoAddress(string serverAddress)
    {
        return GetServerInfoAddress(ParseSs14Uri(serverAddress));
    }

    [Pure]
    public static Uri GetServerInfoAddress(Uri serverAddress)
    {
        return new Uri(GetServerApiAddress(serverAddress), "info");
    }

    [Pure]
    public static Uri GetServerSelfhostedClientZipAddress(string serverAddress)
    {
        return GetServerSelfhostedClientZipAddress(ParseSs14Uri(serverAddress));
    }

    [Pure]
    public static Uri GetServerSelfhostedClientZipAddress(Uri serverAddress)
    {
        return new Uri(GetServerApiAddress(serverAddress), "client.zip");
    }

    public static string GetConnectAddress(ServerInfo? info, Uri serverAddress)
    {
        var rawConnect = info?.ConnectAddress?.Trim();
        if (!string.IsNullOrEmpty(rawConnect))
        {
            if (!rawConnect.Contains("://"))
            {
                rawConnect = "udp://" + rawConnect;
            }
            if (Uri.TryCreate(rawConnect, UriKind.Absolute, out var uri))
            {
                return uri.ToString();
            }
            return rawConnect;
        }

        var apiAddr = GetServerApiAddress(serverAddress);
        var port = (apiAddr.Port > 0 && apiAddr.Port != 80 && apiAddr.Port != 443) ? apiAddr.Port : DefaultServerPort;
        return $"udp://{apiAddr.Host}:{port}";
    }
}
