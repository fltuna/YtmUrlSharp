using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace YtmUrlSharp;

/// <summary>
/// Manages yt-dlp binary — locates it on PATH or downloads it automatically.
/// Checks SHA-256 hash against latest release and re-downloads if outdated.
/// </summary>
public sealed class YtDlpProvider
{
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string HashUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YtmUrlSharp");

    private static readonly string LocalBinaryPath = Path.Combine(DataDir, "yt-dlp.exe");

    private readonly ILogger<YtDlpProvider> _logger;
    private readonly AppState _state;
    private string? _resolvedPath;

    public YtDlpProvider(ILoggerFactory loggerFactory, AppState state)
    {
        _logger = loggerFactory.CreateLogger<YtDlpProvider>();
        _state = state;
    }

    /// <summary>
    /// Returns the path to yt-dlp executable.
    /// First checks PATH, then local cache (with hash check), then downloads automatically.
    /// </summary>
    public async Task<string?> GetPathAsync(CancellationToken ct = default)
    {
        if (_resolvedPath != null)
            return _resolvedPath;

        // 1. Check if yt-dlp is on PATH
        var onPath = FindOnPath("yt-dlp.exe") ?? FindOnPath("yt-dlp");
        if (onPath != null)
        {
            _logger.LogInformation("yt-dlp found on PATH");
            _resolvedPath = onPath;
            SetStatus(YtDlpStatus.Present);
            return _resolvedPath;
        }

        // 2. Check local cache — verify hash against latest release
        if (File.Exists(LocalBinaryPath))
        {
            SetStatus(YtDlpStatus.Checking);
            if (await IsUpToDateAsync(ct))
            {
                _logger.LogInformation("yt-dlp is up to date");
                _resolvedPath = LocalBinaryPath;
                SetStatus(YtDlpStatus.Present);
                return _resolvedPath;
            }

            _logger.LogInformation("yt-dlp is outdated, re-downloading...");
        }
        else
        {
            _logger.LogInformation("yt-dlp not found. Downloading...");
        }

        // 3. Download (or re-download)
        return await DownloadAsync(ct);
    }

    /// <summary>
    /// Compares local binary SHA-256 against latest release hash.
    /// Returns true if up to date, or true on network error (fail-open to use cached binary).
    /// </summary>
    private async Task<bool> IsUpToDateAsync(CancellationToken ct)
    {
        try
        {
            var localHash = ComputeSha256(LocalBinaryPath);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YtmUrlSharp/1.0");
            var hashFile = await http.GetStringAsync(HashUrl, ct);

            // Format: "<hash>  yt-dlp.exe" per line
            foreach (var line in hashFile.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains("yt-dlp.exe", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("_", StringComparison.Ordinal)) // skip yt-dlp_linux, yt-dlp_macos etc.
                    continue;

                var expectedHash = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return true;

                _logger.LogDebug("Hash mismatch: local={LocalHash}, remote={RemoteHash}", localHash, expectedHash);
                return false;
            }

            // Could not find yt-dlp.exe entry — assume up to date
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network error — fail-open, use cached binary
            _logger.LogDebug("Hash check failed, using cached binary: {Message}", ex.Message);
            return true;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<string?> DownloadAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            SetStatus(YtDlpStatus.Downloading);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YtmUrlSharp/1.0");

            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(LocalBinaryPath);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;

                if (totalBytes.HasValue)
                {
                    _state.YtDlpDownloadPercent = (int)(downloaded * 100 / totalBytes.Value);
                    _state.NeedsRedraw = true;
                }
            }

            _logger.LogInformation("yt-dlp downloaded to {Path}", LocalBinaryPath);
            _resolvedPath = LocalBinaryPath;
            SetStatus(YtDlpStatus.Downloaded);
            return _resolvedPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Failed to download yt-dlp: {Message}", ex.Message);
            SetStatus(YtDlpStatus.Failed);
            // Clean up partial download
            try { File.Delete(LocalBinaryPath); } catch { }
            return null;
        }
    }

    private void SetStatus(YtDlpStatus status)
    {
        _state.YtDlpState = status;
        _state.NeedsRedraw = true;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar == null) return null;

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return null;
    }
}
