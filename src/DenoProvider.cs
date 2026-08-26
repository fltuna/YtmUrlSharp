using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace YtmUrlSharp;

/// <summary>
/// Manages the deno binary — locates it on PATH or downloads it automatically.
///
/// yt-dlp ships the JavaScript that solves YouTube's nsig challenge, but not an engine to run it.
/// Without one of deno/node/bun/quickjs, extraction still reports success while the URLs it hands
/// back are rejected by YouTube with HTTP 403. That is the worst failure mode for this tool: the
/// VRChat player fails to play while YtmUrlSharp shows a green result, so the user cannot tell
/// which side is broken.
///
/// Unlike yt-dlp this needs no freshness check — a JavaScript engine does not go stale when
/// YouTube changes.
/// </summary>
public sealed class DenoProvider
{
    private const string DownloadUrl =
        "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YtmUrlSharp");

    private static readonly string LocalBinaryPath = Path.Combine(DataDir, "deno.exe");

    /// <summary>Kept inside our data directory so deno never writes to the user profile.</summary>
    public static string CacheDir => Path.Combine(DataDir, "cache", "deno");

    private readonly ILogger<DenoProvider> _logger;
    private readonly AppState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _resolvedPath;

    public DenoProvider(ILoggerFactory loggerFactory, AppState state)
    {
        _logger = loggerFactory.CreateLogger<DenoProvider>();
        _state = state;
    }

    /// <summary>
    /// Returns the path to a deno executable, or null if it could not be obtained.
    /// Checks PATH, then the local cache, then downloads. Extraction still runs without deno,
    /// so a null here degrades the result rather than failing it.
    /// </summary>
    public async Task<string?> GetPathAsync(CancellationToken ct = default)
    {
        if (_resolvedPath != null)
            return _resolvedPath;

        // Serialized so concurrent extractions cannot race on the same download target.
        await _gate.WaitAsync(ct);
        try
        {
            if (_resolvedPath != null)
                return _resolvedPath;

            var onPath = FindOnPath("deno.exe") ?? FindOnPath("deno");
            if (onPath != null)
            {
                _logger.LogInformation("deno found on PATH");
                _resolvedPath = onPath;
                SetStatus(ToolStatus.Present);
                return _resolvedPath;
            }

            if (File.Exists(LocalBinaryPath))
            {
                _resolvedPath = LocalBinaryPath;
                SetStatus(ToolStatus.Present);
                return _resolvedPath;
            }

            _logger.LogInformation("deno not found. Downloading (needed to solve YouTube's nsig challenge)...");
            return await DownloadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> DownloadAsync(CancellationToken ct)
    {
        var archive = LocalBinaryPath + ".zip";
        var extractDir = Path.Combine(DataDir, "deno_extract");

        try
        {
            Directory.CreateDirectory(DataDir);
            SetStatus(ToolStatus.Downloading);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YtmUrlSharp/1.0");

            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(archive);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    if (totalBytes.HasValue)
                    {
                        _state.DenoDownloadPercent = (int)(downloaded * 100 / totalBytes.Value);
                        _state.NeedsRedraw = true;
                    }
                }
            }

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(archive, extractDir);

            // The official archive holds deno.exe at the root, but search anyway so a
            // reorganized release does not break this.
            var produced = Directory.EnumerateFiles(extractDir, "deno.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (produced == null)
            {
                _logger.LogWarning("deno archive did not contain deno.exe");
                SetStatus(ToolStatus.Failed);
                return null;
            }

            File.Move(produced, LocalBinaryPath, overwrite: true);

            _logger.LogInformation("deno downloaded to {Path}", LocalBinaryPath);
            _resolvedPath = LocalBinaryPath;
            SetStatus(ToolStatus.Downloaded);
            return _resolvedPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Failed to download deno: {Message}", ex.Message);
            SetStatus(ToolStatus.Failed);
            return null;
        }
        finally
        {
            TryDelete(archive);
            TryDeleteDirectory(extractDir);
        }
    }

    private void SetStatus(ToolStatus status)
    {
        _state.DenoState = status;
        _state.NeedsRedraw = true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar == null) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch
            {
                // Malformed PATH entry; keep scanning.
            }
        }

        return null;
    }
}
