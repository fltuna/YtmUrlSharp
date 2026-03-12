using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace YtmUrlSharp;

/// <summary>
/// Monitors clipboard for YouTube URLs using WM_CLIPBOARDUPDATE.
/// Must be used from a thread with a message loop (WinForms UI thread).
/// </summary>
public sealed partial class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    private string? _lastDetectedUrl;

    [GeneratedRegex(
        @"(?:https?://)?(?:www\.)?(?:youtube\.com/watch\?v=|youtu\.be/|youtube\.com/shorts/|music\.youtube\.com/watch\?v=)[\w-]{11}[\w\-&=%.?]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeUrlPattern();

    /// <summary>
    /// Fired when a new YouTube URL is detected on the clipboard.
    /// </summary>
    public event Action<string>? YouTubeUrlDetected;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            CheckClipboard();
        }
        base.WndProc(ref m);
    }

    private void CheckClipboard()
    {
        try
        {
            var text = TextCopy.ClipboardService.GetText();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var trimmed = text.Trim();
            var match = YouTubeUrlPattern().Match(trimmed);
            if (!match.Success)
                return;

            // Only react when the clipboard contains just the URL (no surrounding text)
            if (match.Index != 0 || match.Length != trimmed.Length)
                return;

            var url = match.Value;

            // Ensure https:// prefix
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            if (url == _lastDetectedUrl)
                return;

            _lastDetectedUrl = url;
            YouTubeUrlDetected?.Invoke(url);
        }
        catch
        {
            // Clipboard access can fail transiently
        }
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
