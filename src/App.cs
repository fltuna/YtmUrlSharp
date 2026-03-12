using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace YtmUrlSharp;

/// <summary>
/// Main application. Desktop window always shown, VR overlay when SteamVR is available.
/// </summary>
public sealed class App : IDisposable
{
    private readonly AppState _state = new();
    private readonly ClipboardWatcher _clipboardWatcher = new();
    private readonly VRChatLogWatcher _logWatcher;
    private readonly YouTubeProcessor _youtubeProcessor;
    private readonly OverlayRenderer _renderer = new();
    private readonly VROverlayManager _vrManager;
    private readonly DesktopWindow _window;
    private readonly ILogger<App> _logger;
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>
    /// Tracks URLs currently being processed to avoid duplicate processing.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTasks = new();

    private CancellationToken _ct;
    private DateTime _lastVrCheck = DateTime.MinValue;

    /// <summary>
    /// The most recently detected URL. Its result will be shown in the Streams tab.
    /// </summary>
    private string? _latestUrl;

    public App(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<App>();
        _logWatcher = new VRChatLogWatcher(loggerFactory);
        _youtubeProcessor = new YouTubeProcessor(loggerFactory, _state);
        _vrManager = new VROverlayManager(_renderer, loggerFactory);
        _window = new DesktopWindow(_renderer, _state);
        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += OnTick;
        _clipboardWatcher.YouTubeUrlDetected += OnClipboardYouTubeUrl;
    }

    public void Run(CancellationToken ct)
    {
        _ct = ct;

        _logger.LogInformation("YTM URL Sharp");
        _logger.LogInformation("Monitoring clipboard and VRChat logs for YouTube URLs...");
        _logger.LogInformation("SteamVR overlay will activate automatically when SteamVR starts.");

        // Check yt-dlp availability at startup (non-blocking)
        _ = CheckYtDlpAsync(ct);

        _state.StatusMessage = "Waiting for YouTube URL in clipboard...";
        _state.NeedsRedraw = true;

        TryConnectVR();

        ct.Register(() => _window.BeginInvoke(_window.Close));

        _timer.Start();
        Application.Run(_window);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_ct.IsCancellationRequested) return;

        CheckVRConnection();

        if (_vrManager.IsConnected)
            _vrManager.PollEvents(_state);

        // Check VRChat log for failed video playback
        var logUrl = _logWatcher.CheckLog();
        if (logUrl != null && !_activeTasks.ContainsKey(logUrl))
            await HandleNewYouTubeUrl(logUrl);

        if (_state.NeedsRedraw)
        {
            _window.UpdateFrame();

            if (_vrManager.IsConnected)
                _vrManager.UpdateTexture(_state);

            _state.NeedsRedraw = false;
        }
    }

    private void CheckVRConnection()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastVrCheck).TotalSeconds < 5) return;
        _lastVrCheck = now;

        if (!_vrManager.IsConnected)
            TryConnectVR();
    }

    private async void OnClipboardYouTubeUrl(string url)
    {
        if (_ct.IsCancellationRequested) return;
        if (_activeTasks.ContainsKey(url) || url == _state.DetectedYouTubeUrl) return;
        await HandleNewYouTubeUrl(url);
    }

    private void TryConnectVR()
    {
        if (_vrManager.TryConnect())
            _state.NeedsRedraw = true;
    }

    private async Task HandleNewYouTubeUrl(string url)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        if (!_activeTasks.TryAdd(url, cts))
        {
            cts.Dispose();
            return;
        }

        _latestUrl = url;

        // Update UI to show this URL is being processed
        _state.DetectedYouTubeUrl = url;
        _state.VideoTitle = null;
        _state.Streams.Clear();
        _state.SelectedIndex = -1;
        _state.ScrollOffset = 0;
        _state.IsProcessing = true;
        UpdateProcessingStatus();
        _state.NeedsRedraw = true;

        _logger.LogInformation("Detected: {Url} ({Active} active)", url, _activeTasks.Count);

        try
        {
            var (title, streams) = await _youtubeProcessor.ExtractStreamsAsync(url, cts.Token);

            // Add to history regardless of whether this is the latest URL
            _state.AddToHistory(title, url, streams);
            _logger.LogInformation("\"{Title}\" - {Count} streams", title, streams.Count);

            // Only update the main view if this is still the latest URL
            if (_latestUrl == url)
            {
                _state.DetectedYouTubeUrl = url;
                _state.VideoTitle = title;
                _state.Streams = streams;
                _state.StatusMessage = $"{streams.Count} streams found";
                _state.SelectedIndex = -1;
                _state.ScrollOffset = 0;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract streams from {Url}", url);
            if (_latestUrl == url)
                _state.StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            _activeTasks.TryRemove(url, out _);
            cts.Dispose();

            if (_activeTasks.IsEmpty)
                _state.IsProcessing = false;

            UpdateProcessingStatus();
            _state.NeedsRedraw = true;
        }
    }

    private async Task CheckYtDlpAsync(CancellationToken ct)
    {
        try
        {
            await _youtubeProcessor.YtDlpProvider.GetPathAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("yt-dlp startup check failed: {Message}", ex.Message);
        }
    }

    private void UpdateProcessingStatus()
    {
        if (_activeTasks.IsEmpty) return;

        _state.StatusMessage = _activeTasks.Count == 1
            ? "Extracting stream URLs..."
            : $"Extracting stream URLs... ({_activeTasks.Count} URLs processing)";
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();

        foreach (var (_, cts) in _activeTasks)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeTasks.Clear();

        _clipboardWatcher.Dispose();
        _logWatcher.Dispose();
        _vrManager.Dispose();
        _renderer.Dispose();
    }
}
