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
    private readonly ILogger<YouTubeProcessor> _logger;

    public YtDlpProvider YtDlpProvider => _ytDlpProvider;

    public YouTubeProcessor(ILoggerFactory loggerFactory, AppState state)
    {
        _logger = loggerFactory.CreateLogger<YouTubeProcessor>();
        _ytDlpProvider = new YtDlpProvider(loggerFactory, state);
    }

    public async Task<(string Title, List<StreamEntry> Streams)> ExtractStreamsAsync(
        string youtubeUrl, CancellationToken ct = default)
    {
        var video = await _client.Videos.GetAsync(youtubeUrl, ct);
        var streams = new List<StreamEntry>();

        // Try yt-dlp --get-url for direct stream URLs
        await TryAddYtDlpUrlsAsync(youtubeUrl, streams, ct);

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

        return (video.Title, streams);
    }

    /// <summary>
    /// Uses yt-dlp --get-url to get direct stream URLs.
    /// Default format selection returns video+audio URLs (typically 2 lines).
    /// </summary>
    private async Task TryAddYtDlpUrlsAsync(string youtubeUrl, List<StreamEntry> streams, CancellationToken ct)
    {
        try
        {
            var ytDlpPath = await _ytDlpProvider.GetPathAsync(ct);
            if (ytDlpPath == null) return;

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                ArgumentList = { "--get-url", "--no-warnings", youtubeUrl },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("yt-dlp exited with {ExitCode}: {Error}", proc.ExitCode, UrlMasker.MaskForLog(err.Trim()));
                return;
            }

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
}
