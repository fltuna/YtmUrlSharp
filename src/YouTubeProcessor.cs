using System.Diagnostics;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace YtmUrlSharp;

/// <summary>
/// Extracts stream manifest URLs from YouTube videos.
/// Uses YoutubeExplode for individual streams and yt-dlp for direct stream URLs.
/// </summary>
public sealed class YouTubeProcessor
{
    private readonly YoutubeClient _client = new();
    private readonly YtDlpProvider _ytDlpProvider;
    private readonly DenoProvider _denoProvider;
    private readonly ILogger<YouTubeProcessor> _logger;

    public YtDlpProvider YtDlpProvider => _ytDlpProvider;
    public DenoProvider DenoProvider => _denoProvider;

    public YouTubeProcessor(ILoggerFactory loggerFactory, AppState state)
    {
        _logger = loggerFactory.CreateLogger<YouTubeProcessor>();
        _ytDlpProvider = new YtDlpProvider(loggerFactory, state);
        _denoProvider = new DenoProvider(loggerFactory, state);
    }

    public async Task<(string Title, List<StreamEntry> Streams)> ExtractStreamsAsync(
        string youtubeUrl, CancellationToken ct = default)
    {
        var streams = new List<StreamEntry>();
        string? title = null;

        // Try YoutubeExplode for video info and streams (non-fatal)
        try
        {
            var video = await _client.Videos.GetAsync(youtubeUrl, ct);
            title = video.Title;

            try
            {
                var manifest = await _client.Videos.Streams.GetManifestAsync(video.Id, ct);

                // Muxed streams (video + audio)
                foreach (var s in manifest.GetMuxedStreams().OrderByDescending(s => s.VideoQuality.MaxHeight))
                {
                    streams.Add(new StreamEntry
                    {
                        Quality = $"{s.VideoQuality.MaxHeight}p (muxed)",
                        Container = s.Container.Name,
                        Codec = $"{s.VideoCodec}+{s.AudioCodec}",
                        Url = s.Url,
                        Size = s.Size.Bytes > 0 ? s.Size.Bytes : null,
                        Type = "muxed"
                    });
                }

                // Video-only streams
                foreach (var s in manifest.GetVideoOnlyStreams().OrderByDescending(s => s.VideoQuality.MaxHeight))
                {
                    streams.Add(new StreamEntry
                    {
                        Quality = $"{s.VideoQuality.MaxHeight}p{(s.VideoQuality.Framerate > 30 ? s.VideoQuality.Framerate.ToString() : "")}",
                        Container = s.Container.Name,
                        Codec = s.VideoCodec,
                        Url = s.Url,
                        Size = s.Size.Bytes > 0 ? s.Size.Bytes : null,
                        Type = "video"
                    });
                }

                // Audio-only streams
                foreach (var s in manifest.GetAudioOnlyStreams().OrderByDescending(s => s.Bitrate.BitsPerSecond))
                {
                    streams.Add(new StreamEntry
                    {
                        Quality = $"{s.Bitrate.KiloBitsPerSecond:F0}kbps",
                        Container = s.Container.Name,
                        Codec = s.AudioCodec,
                        Url = s.Url,
                        Size = s.Size.Bytes > 0 ? s.Size.Bytes : null,
                        Type = "audio"
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("YoutubeExplode stream manifest failed, continuing with yt-dlp: {Message}", ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("YoutubeExplode video info failed, continuing with yt-dlp: {Message}", ex.Message);
        }

        // Try yt-dlp for direct stream URLs (always attempted)
        // Muxed first: a single URL that already contains video + audio
        await TryAddYtDlpMuxedUrlAsync(youtubeUrl, streams, ct);
        await TryAddYtDlpUrlsAsync(youtubeUrl, streams, ct);

        if (streams.Count == 0)
            throw new InvalidOperationException($"No streams could be extracted from {youtubeUrl}");

        return (title ?? youtubeUrl, streams);
    }

    /// <summary>
    /// Uses yt-dlp -f best to get a single progressive URL that carries video and audio together.
    /// YouTube only serves these up to 360p (format 18) these days, but the URL plays with
    /// sound as-is, with no muxing needed on the player side.
    /// </summary>
    private async Task TryAddYtDlpMuxedUrlAsync(string youtubeUrl, List<StreamEntry> streams, CancellationToken ct)
    {
        try
        {
            // --print implies --simulate; fields are pipe-separated on one line per format
            var output = await RunYtDlpAsync(
                ["-f", "best[vcodec!=none][acodec!=none]", "--no-warnings",
                 "--print", "%(height)s|%(ext)s|%(vcodec)s|%(acodec)s|%(url)s", youtubeUrl],
                ct);
            if (output == null) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 5) continue;

                var (height, ext, vcodec, acodec) = (parts[0], parts[1], parts[2], parts[3]);
                // The URL itself may contain '|', so rejoin everything after the 4th field
                var url = string.Join('|', parts[4..]);

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                // Guard against a video-only format slipping through the selector
                if (acodec.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;

                var quality = int.TryParse(height, out var h) ? $"{h}p" : "best";

                streams.Add(new StreamEntry
                {
                    Quality = $"{quality} Video+Audio (yt-dlp)",
                    Container = string.IsNullOrEmpty(ext) || ext == "NA" ? "direct" : ext,
                    Codec = $"{vcodec}+{acodec}",
                    Url = url,
                    Size = null,
                    Type = "yt-muxed"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp muxed lookup failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Uses yt-dlp --get-url to get direct stream URLs.
    /// Default format selection returns video+audio URLs (typically 2 lines).
    /// </summary>
    private async Task TryAddYtDlpUrlsAsync(string youtubeUrl, List<StreamEntry> streams, CancellationToken ct)
    {
        try
        {
            var output = await RunYtDlpAsync(["--get-url", "--no-warnings", youtubeUrl], ct);
            if (output == null) return;

            var urls = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _logger.LogInformation("yt-dlp: {Count} URL(s) returned", urls.Length);

            for (var i = 0; i < urls.Length; i++)
            {
                var url = urls[i];
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Classify based on URL content and position
                // yt-dlp default: first = video (best), second = audio (best)
                var isVideo = url.Contains("mime=video", StringComparison.OrdinalIgnoreCase)
                           || url.Contains("mime%3Dvideo", StringComparison.OrdinalIgnoreCase);
                var isAudio = url.Contains("mime=audio", StringComparison.OrdinalIgnoreCase)
                           || url.Contains("mime%3Daudio", StringComparison.OrdinalIgnoreCase);

                if (!isVideo && !isAudio)
                {
                    // Fallback: first URL is usually video, second is audio
                    isVideo = i == 0;
                    isAudio = i == 1;
                }

                var type = isAudio ? "yt-audio" : "yt-video";
                var label = isAudio ? "Best Audio (yt-dlp)" : "Best Video (yt-dlp)";

                streams.Add(new StreamEntry
                {
                    Quality = label,
                    Container = "direct",
                    Codec = isAudio ? "audio" : "video",
                    Url = url,
                    Size = null,
                    Type = type
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp not available or failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Wires yt-dlp's JavaScript runtime and keeps its caches inside our data directory.
    ///
    /// YouTube's nsig challenge cannot be solved by yt-dlp alone: it ships the solver script but
    /// needs an engine (deno/node/bun/quickjs) to execute it. With none available, extraction
    /// still succeeds and the URLs it returns are answered with HTTP 403 — the player fails while
    /// this tool reports success. yt-dlp finds an engine on PATH by itself, so this only has to
    /// point it at the copy we manage.
    /// </summary>
    private async Task ConfigureYtDlpAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(YtDlpProvider.CacheDir);

        var denoPath = await _denoProvider.GetPathAsync(ct);
        if (denoPath == null)
        {
            _logger.LogWarning(
                "No JavaScript runtime available; yt-dlp URLs are likely to be rejected with HTTP 403.");
            return;
        }

        psi.ArgumentList.Add("--js-runtimes");
        psi.ArgumentList.Add($"deno:{denoPath}");
        psi.Environment["DENO_DIR"] = DenoProvider.CacheDir;
    }

    /// <summary>
    /// Runs yt-dlp with the given arguments. Returns stdout, or null if yt-dlp is
    /// unavailable or exited non-zero.
    /// </summary>
    private async Task<string?> RunYtDlpAsync(string[] args, CancellationToken ct)
    {
        var ytDlpPath = await _ytDlpProvider.GetPathAsync(ct);
        if (ytDlpPath == null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        await ConfigureYtDlpAsync(psi, ct);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        // Read both pipes concurrently so a full stderr buffer can't deadlock stdout
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        var output = await outTask;
        var err = await errTask;
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("yt-dlp exited with {ExitCode}: {Error}", proc.ExitCode, UrlMasker.MaskForLog(err.Trim()));
            return null;
        }

        return output;
    }
}
