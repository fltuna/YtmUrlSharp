using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace YtmUrlSharp;

/// <summary>
/// Watches VRChat log files for video playback failures and extracts YouTube URLs.
/// Inspired by VRCX's LogWatcher pattern: FileStream with position tracking.
/// </summary>
public sealed partial class VRChatLogWatcher : IDisposable
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
        "VRChat", "VRChat");

    private readonly ILogger<VRChatLogWatcher> _logger;
    private readonly HashSet<string> _processedUrls = [];
    private readonly Queue<string> _pendingUrls = new();

    private string? _currentLogPath;
    private long _position;
    private bool _sawPlaybackError;
    private DateTime _lastDirCheck = DateTime.MinValue;

    [GeneratedRegex(@"\[Video Playback\] ERROR")]
    private static partial Regex PlaybackErrorPattern();

    [GeneratedRegex(@"\[Video Playback\] URL '(https?://(?:www\.)?(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/shorts/|music\.youtube\.com/watch\?v=)[\w\-&=%.?]+)'")]
    private static partial Regex PlaybackUrlPattern();

    public VRChatLogWatcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<VRChatLogWatcher>();
        _logger.LogInformation("VRChat log directory: {Dir}", LogDir);
        _logger.LogInformation("Directory exists: {Exists}", Directory.Exists(LogDir));
    }

    /// <summary>
    /// Polls for new YouTube URLs from VRChat log.
    /// Returns null if no new URL detected.
    /// </summary>
    public string? CheckLog()
    {
        if (_pendingUrls.Count > 0)
            return _pendingUrls.Dequeue();

        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDirCheck).TotalSeconds >= 5)
            {
                _lastDirCheck = now;
                UpdateLogFile();
            }

            if (_currentLogPath != null)
                ReadNewLines();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Log read error: {Message}", ex.Message);
        }

        return _pendingUrls.Count > 0 ? _pendingUrls.Dequeue() : null;
    }

    /// <summary>
    /// Finds the latest log file. If a new one is found, switch to it.
    /// </summary>
    private void UpdateLogFile()
    {
        if (!Directory.Exists(LogDir))
        {
            _logger.LogDebug("Log directory not found: {Dir}", LogDir);
            return;
        }

        var dirInfo = new DirectoryInfo(LogDir);
        var files = dirInfo.GetFiles("output_log_*.txt", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            _logger.LogDebug("No log files found in {Dir}", LogDir);
            return;
        }

        // Sort by creation time, pick latest
        Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
        var latest = files[^1];

        if (latest.FullName == _currentLogPath) return;

        _logger.LogInformation("VRChat log found: {File} (size: {Size} bytes)", latest.Name, latest.Length);
        _currentLogPath = latest.FullName;

        // Seek to end of file — only read new content from this point
        _position = latest.Length;
        _sawPlaybackError = false;
        _logger.LogInformation("Starting tail at position {Position}", _position);
    }

    /// <summary>
    /// Reads new lines from the current log file using position-based tracking.
    /// Opens the file with shared read/write access so VRChat can continue writing.
    /// </summary>
    private void ReadNewLines()
    {
        var fileInfo = new FileInfo(_currentLogPath!);
        if (!fileInfo.Exists)
        {
            _logger.LogWarning("Log file disappeared: {File}", _currentLogPath);
            _currentLogPath = null;
            return;
        }

        // No new content
        if (fileInfo.Length == _position)
            return;

        _logger.LogInformation("Log file changed: {Size} bytes (was at {Position}, +{Delta})",
            fileInfo.Length, _position, fileInfo.Length - _position);

        // File was truncated or replaced — reset
        if (fileInfo.Length < _position)
        {
            _logger.LogInformation("Log file truncated (was {Old}, now {New}), resetting", _position, fileInfo.Length);
            _position = 0;
            _sawPlaybackError = false;
        }

        var bytesToRead = fileInfo.Length - _position;
        _logger.LogDebug("Reading {Bytes} new bytes from position {Position}", bytesToRead, _position);

        using var stream = new FileStream(
            _currentLogPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            65536,
            FileOptions.SequentialScan);

        stream.Position = _position;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lineCount = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineCount++;
            ProcessLine(line);
        }

        _position = stream.Position;
        _logger.LogDebug("Read {Lines} lines, new position: {Position}", lineCount, _position);
    }

    private void ProcessLine(string line)
    {
        // Check for playback error
        if (PlaybackErrorPattern().IsMatch(line))
        {
            _sawPlaybackError = true;
            _logger.LogDebug("Saw playback error: {Line}", line.Length > 120 ? line[..120] + "..." : line);
            return;
        }

        // Check for URL line
        var urlMatch = PlaybackUrlPattern().Match(line);
        if (!urlMatch.Success)
        {
            // Non-URL, non-error line resets the error state
            if (!string.IsNullOrWhiteSpace(line) && _sawPlaybackError)
            {
                // Only log if we're losing an error state
                if (line.Contains("[Video Playback]"))
                    _logger.LogDebug("Video Playback line (no URL match, len={Len}): {Line}", line.Length, line);
                _sawPlaybackError = false;
            }
            return;
        }

        var url = urlMatch.Groups[1].Value;
        _logger.LogDebug("Matched URL: {Url}, errorBefore={Error}, alreadyProcessed={Processed}",
            url, _sawPlaybackError, _processedUrls.Contains(url));

        if (_sawPlaybackError && _processedUrls.Add(url))
        {
            _logger.LogInformation("VRChat playback failed, extracting: {Url}", url);
            _pendingUrls.Enqueue(url);
        }

        _sawPlaybackError = false;
    }

    public void Dispose()
    {
        // No persistent file handle to close — opened per-read
    }
}
