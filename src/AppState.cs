namespace YtmUrlSharp;

/// <summary>
/// Shared application state between components.
/// </summary>
public sealed class AppState
{
    public string? DetectedYouTubeUrl { get; set; }
    public string? VideoTitle { get; set; }
    public List<StreamEntry> Streams { get; set; } = [];
    public string? StatusMessage { get; set; }
    public bool IsProcessing { get; set; }
    public int SelectedIndex { get; set; } = -1;
    public int ScrollOffset { get; set; }
    public bool NeedsRedraw { get; set; } = true;
    public string? CopiedUrl { get; set; }
    public DateTime? CopiedAt { get; set; }
    public YtDlpStatus YtDlpState { get; set; } = YtDlpStatus.Unknown;
    public int YtDlpDownloadPercent { get; set; }

    private const int MaxHistoryEntries = 50;

    /// <summary>
    /// Copies a stream URL to clipboard.
    /// </summary>
    public void CopyStream(StreamEntry stream)
    {
        try
        {
            TextCopy.ClipboardService.SetText(stream.Url);
            CopiedUrl = stream.Url;
            CopiedAt = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>
    /// Records the current video and streams as a history entry.
    /// Called after successful stream extraction.
    /// </summary>
    public void AddToHistory(string title, string youtubeUrl, List<StreamEntry> streams)
    {
        // Remove duplicate if same URL already in history
        History.RemoveAll(h => h.YoutubeUrl == youtubeUrl);

        History.Insert(0, new ConversionHistory
        {
            VideoTitle = title,
            YoutubeUrl = youtubeUrl,
            Streams = streams,
            ConvertedAt = DateTime.UtcNow,
        });

        if (History.Count > MaxHistoryEntries)
            History.RemoveAt(History.Count - 1);
    }

    /// <summary>
    /// Restores a history entry as the active view.
    /// </summary>
    public void RestoreFromHistory(ConversionHistory entry)
    {
        DetectedYouTubeUrl = entry.YoutubeUrl;
        VideoTitle = entry.VideoTitle;
        Streams = entry.Streams;
        StatusMessage = $"{entry.Streams.Count} streams (from history)";
        SelectedIndex = -1;
        ScrollOffset = 0;
        ActiveTab = ViewTab.Streams;
        NeedsRedraw = true;
    }

    // Tab & History
    public ViewTab ActiveTab { get; set; } = ViewTab.Streams;
    public List<ConversionHistory> History { get; } = [];
    public int HistoryScrollOffset { get; set; }
    public int HistorySelectedIndex { get; set; } = -1;

    // Startup setting
    public bool AutoStartEnabled { get; set; } = StartupManager.IsRegistered;

    // Stream type filters (true = visible)
    public bool ShowYtDlp { get; set; } = true;
    public bool ShowMuxed { get; set; } = true;
    public bool ShowVideo { get; set; }
    public bool ShowAudio { get; set; }

    /// <summary>
    /// Returns streams filtered by the current type visibility settings.
    /// </summary>
    public List<StreamEntry> FilteredStreams => Streams.Where(s => s.Type switch
    {
        "yt-video" or "yt-audio" => ShowYtDlp,
        "muxed" => ShowMuxed,
        "video" => ShowVideo,
        "audio" => ShowAudio,
        _ => true,
    }).ToList();
}

public enum ViewTab { Streams, History }

public enum YtDlpStatus
{
    Unknown,
    Present,
    Checking,
    Downloading,
    Downloaded,
    NotFound,
    Failed,
}

/// <summary>
/// A past video conversion with all extracted streams.
/// </summary>
public sealed class ConversionHistory
{
    public required string VideoTitle { get; init; }
    public required string YoutubeUrl { get; init; }
    public required List<StreamEntry> Streams { get; init; }
    public required DateTime ConvertedAt { get; init; }
}

public sealed class StreamEntry
{
    public required string Quality { get; init; }
    public required string Container { get; init; }
    public required string Codec { get; init; }
    public required string Url { get; init; }
    public required long? Size { get; init; }
    public required string Type { get; init; } // "yt-video", "yt-audio", "dash", "hls", "video", "audio", "muxed"
}
