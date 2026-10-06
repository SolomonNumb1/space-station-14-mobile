using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Utility;
using Robust.Shared.Utility;
using SharpZstd.Interop;

namespace Content.Android.Launcher.Api;

public sealed class AczClient
{
    private static bool _nativeInitialized;
    private static readonly object _nativeLock = new();

    private readonly HttpClient _http;

    public AczClient()
    {
        InitNative();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static void InitNative()
    {
        lock (_nativeLock)
        {
            if (_nativeInitialized) return;
            _nativeInitialized = true;

            try { Java.Lang.JavaSystem.LoadLibrary("zstd"); } catch { }
            try { Java.Lang.JavaSystem.LoadLibrary("sodium"); } catch { }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Zstd).Assembly, (name, asm, path) =>
                {
                    if (name is "zstd" or "libzstd")
                    {
                        if (NativeLibrary.TryLoad("libzstd.so", asm, path, out var handle)) return handle;
                        if (NativeLibrary.TryLoad("zstd", asm, path, out handle)) return handle;
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        }
    }

    public static (string manifestUrl, string downloadUrl) GetUrls(ServerBuildInfo build, string serverAddress)
    {
        var manifestUrl = build.ManifestUrl;
        var downloadUrl = build.ManifestDownloadUrl;

        if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(downloadUrl))
        {
            if (UriHelper.TryParseSs14Uri(serverAddress, out var uri))
            {
                var api = UriHelper.GetServerApiAddress(uri);
                manifestUrl ??= new Uri(api, "manifest.txt").ToString();
                downloadUrl ??= new Uri(api, "download").ToString();
            }
        }

        return (manifestUrl ?? "", downloadUrl ?? "");
    }

    public async Task<string> DownloadManifestBuildAsync(
        ServerBuildInfo build,
        string serverAddress,
        string cacheDir,
        Action<int, int, string>? progress,
        CancellationToken cancel = default)
    {
        var expectedHashHex = build.ManifestHash?.ToUpperInvariant();
        if (!string.IsNullOrEmpty(expectedHashHex))
        {
            var cachedZip = Path.Combine(cacheDir, $"{expectedHashHex}.zip");
            if (File.Exists(cachedZip) && new FileInfo(cachedZip).Length > 0)
            {
                return cachedZip;
            }
        }

        var (manifestUrl, downloadUrl) = GetUrls(build, serverAddress);

        progress?.Invoke(0, 0, "Fetching server manifest...");
        using var manifestReq = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        manifestReq.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
        using var manifestResp = await _http.SendAsync(manifestReq, HttpCompletionOption.ResponseHeadersRead, cancel);
        manifestResp.EnsureSuccessStatusCode();

        using var manifestStream = await manifestResp.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(manifestStream, Encoding.UTF8);

        var header = await reader.ReadLineAsync(cancel);
        if (header != "Robust Content Manifest 1")
        {
            throw new InvalidOperationException($"Invalid manifest header: {header}");
        }

        var entries = new List<(int Index, string Path)>();
        string? line;
        while ((line = await reader.ReadLineAsync(cancel)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var sep = line.IndexOf(' ');
            if (sep <= 0) continue;

            var path = line.Substring(sep + 1).Trim().Replace('\\', '/').TrimStart('/');
            entries.Add((entries.Count, path));
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Manifest contains 0 entries.");
        }

        var finalHash = expectedHashHex ?? (build.Version?.ToUpperInvariant() ?? Guid.NewGuid().ToString("N"));
        var targetZip = Path.Combine(cacheDir, $"{finalHash}.zip");
        if (File.Exists(targetZip) && new FileInfo(targetZip).Length > 0)
        {
            return targetZip;
        }

        var requestBody = new byte[entries.Count * 4];
        for (var i = 0; i < entries.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(requestBody.AsSpan(i * 4, 4), i);
        }

        using var dlReq = new HttpRequestMessage(HttpMethod.Post, downloadUrl);
        dlReq.Headers.Add("X-Robust-Download-Protocol", "1");
        dlReq.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
        dlReq.Headers.TryAddWithoutValidation("Accept-Encoding", "zstd");
        dlReq.Content = new ByteArrayContent(requestBody);
        dlReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        progress?.Invoke(0, entries.Count, $"Connecting to download stream ({entries.Count} files)...");
        using var dlResp = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, cancel);
        dlResp.EnsureSuccessStatusCode();

        var tempZip = Path.Combine(cacheDir, $"{finalHash}.tmp");
        if (File.Exists(tempZip)) File.Delete(tempZip);

        try
        {
            using (var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, false))
            using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var rawStream = await dlResp.Content.ReadAsStreamAsync(cancel);
                Stream decompressStream = dlResp.Content.Headers.ContentEncoding.Any(e => e.Equals("zstd", StringComparison.OrdinalIgnoreCase))
                    ? new ZStdDecompressStream(rawStream)
                    : rawStream;

                using var stream = new BufferedStream(decompressStream, 1048576);

                var streamHeaderBuf = new byte[4];
                await stream.ReadExactlyAsync(streamHeaderBuf, cancel);
                var streamFlags = BinaryPrimitives.ReadInt32LittleEndian(streamHeaderBuf);
                var preCompressed = (streamFlags & 1) != 0;

                var readBuffer = new byte[65536];
                var compressBuffer = new byte[65536];
                var fileHeaderBuf = new byte[preCompressed ? 8 : 4];

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var lastProgressTime = 0L;

                for (var i = 0; i < entries.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var entry = entries[i];

                    await stream.ReadExactlyAsync(fileHeaderBuf, cancel);
                    var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(fileHeaderBuf.AsSpan(0, 4));

                    ReadOnlyMemory<byte> entryData;

                    if (preCompressed)
                    {
                        var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(fileHeaderBuf.AsSpan(4, 4));
                        if (compressedLength > 0)
                        {
                            EnsureBuffer(ref compressBuffer, compressedLength);
                            await stream.ReadExactlyAsync(compressBuffer.AsMemory(0, compressedLength), cancel);

                            EnsureBuffer(ref readBuffer, uncompressedLength);
                            unsafe
                            {
                                fixed (byte* dst = readBuffer)
                                fixed (byte* src = compressBuffer)
                                {
                                    var ret = Zstd.ZSTD_decompress(dst, (nuint)uncompressedLength, src, (nuint)compressedLength);
                                    if (Zstd.ZSTD_isError(ret) != 0)
                                    {
                                        var err = Marshal.PtrToStringUTF8((IntPtr)Zstd.ZSTD_getErrorName(ret));
                                        throw new InvalidOperationException($"Zstd decompress error for {entry.Path}: {err}");
                                    }
                                }
                            }
                            entryData = readBuffer.AsMemory(0, uncompressedLength);
                        }
                        else
                        {
                            EnsureBuffer(ref readBuffer, uncompressedLength);
                            await stream.ReadExactlyAsync(readBuffer.AsMemory(0, uncompressedLength), cancel);
                            entryData = readBuffer.AsMemory(0, uncompressedLength);
                        }
                    }
                    else
                    {
                        EnsureBuffer(ref readBuffer, uncompressedLength);
                        await stream.ReadExactlyAsync(readBuffer.AsMemory(0, uncompressedLength), cancel);
                        entryData = readBuffer.AsMemory(0, uncompressedLength);
                    }

                    var zipEntry = zip.CreateEntry(entry.Path, CompressionLevel.NoCompression);
                    using (var entryStream = zipEntry.Open())
                    {
                        entryStream.Write(entryData.Span);
                    }

                    if (sw.ElapsedMilliseconds - lastProgressTime >= 150 || i == entries.Count - 1)
                    {
                        lastProgressTime = sw.ElapsedMilliseconds;
                        var pct = (int)((i + 1) * 100L / entries.Count);
                        progress?.Invoke(i + 1, entries.Count, $"Downloading content: {pct}%\n({i + 1}/{entries.Count} files)");
                    }
                }
            }

            if (File.Exists(targetZip)) File.Delete(targetZip);
            File.Move(tempZip, targetZip);
            return targetZip;
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    private static void EnsureBuffer(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            var newSize = Math.Max(buffer.Length * 2, size);
            buffer = new byte[newSize];
        }
    }
}
