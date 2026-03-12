using SkiaSharp;

namespace YtmUrlSharp;

/// <summary>
/// Renders the overlay UI using SkiaSharp.
/// Returns raw RGBA pixel data suitable for OpenVR's SetOverlayRaw.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    public const int Width = 1024;
    public const int Height = 768;
    private const int RowHeight = 44;
    private const int HeaderHeight = 120;
    private const int Padding = 16;
    private const int FooterHeight = 64;
    private const int FilterBarHeight = 28;
    private const int ColumnHeaderHeight = 28;
    public const int MaxVisibleRows = (Height - HeaderHeight - FooterHeight - FilterBarHeight - ColumnHeaderHeight) / RowHeight;

    // Tab layout
    private const int TabY = 20;
    private const int TabHeight = 28;
    private const int TabWidth = 80;
    private const int TabStreamsX = Width - Padding - TabWidth * 2 - 8;
    private const int TabHistoryX = Width - Padding - TabWidth;

    // Filter checkbox layout
    private const int FilterY = HeaderHeight + 4;
    private const int CheckboxSize = 16;
    private const int FilterItemWidth = 90;
    private const int FilterStartX = Padding;

    // Scroll button layout
    private const int ScrollBtnWidth = 80;
    private const int ScrollBtnHeight = 36;
    private const int ScrollBtnY = Height - FooterHeight + 8;
    private const int ScrollUpBtnX = Width / 2 - ScrollBtnWidth - 8;
    private const int ScrollDownBtnX = Width / 2 + 8;

    private readonly SKSurface _surface;
    private readonly SKImageInfo _imageInfo;

    // Filter definitions: (label, state getter key)
    private static readonly string[] FilterLabels = ["YT-DLP", "MUXED", "VIDEO", "AUDIO"];

    // Colors
    private static readonly SKColor BgColor = new(30, 30, 40);
    private static readonly SKColor HeaderBgColor = new(40, 40, 55);
    private static readonly SKColor AccentColor = new(88, 166, 255);
    private static readonly SKColor TextColor = new(230, 230, 230);
    private static readonly SKColor SubTextColor = new(150, 150, 160);
    private static readonly SKColor SelectedBgColor = new(55, 55, 75);
    private static readonly SKColor VideoBadgeColor = new(66, 135, 245);
    private static readonly SKColor AudioBadgeColor = new(245, 158, 66);
    private static readonly SKColor DashBadgeColor = new(186, 120, 255);
    private static readonly SKColor HlsBadgeColor = new(255, 100, 100);
    private static readonly SKColor MuxedBadgeColor = new(102, 187, 106);
    private static readonly SKColor SuccessColor = new(102, 187, 106);
    private static readonly SKColor ProcessingColor = new(255, 213, 79);
    private static readonly SKColor ErrorColor = new(255, 100, 100);

    // CJK-capable typeface (resolved once at startup)
    private static readonly SKTypeface Typeface = ResolveTypeface();

    private static SKTypeface ResolveTypeface()
    {
        // Find a system font that supports CJK characters
        var cjk = SKFontManager.Default.MatchCharacter('\u3042'); // 'あ'
        if (cjk != null && cjk.FamilyName != SKTypeface.Default.FamilyName)
            return cjk;
        return SKTypeface.Default;
    }

    public OverlayRenderer()
    {
        _imageInfo = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(_imageInfo);
    }

    public byte[] Render(AppState state)
    {
        var canvas = _surface.Canvas;
        canvas.Clear(BgColor);

        DrawHeader(canvas, state);

        if (state.ActiveTab == ViewTab.Streams)
        {
            DrawFilterBar(canvas, state);
            DrawStreamList(canvas, state);
        }
        else
        {
            DrawHistoryList(canvas, state);
        }

        DrawFooter(canvas, state);

        var pixmap = _surface.PeekPixels();
        var data = new byte[Width * Height * 4];
        pixmap.GetPixelSpan().CopyTo(data);
        return data;
    }

    private static void DrawHeader(SKCanvas canvas, AppState state)
    {
        using var headerPaint = new SKPaint { Color = HeaderBgColor };
        canvas.DrawRect(0, 0, Width, HeaderHeight, headerPaint);

        // Title
        using var titlePaint = new SKPaint { Color = AccentColor, IsAntialias = true };
        using var titleFont = new SKFont(Typeface, 24) { Edging = SKFontEdging.SubpixelAntialias };
        canvas.DrawText("YTM URL Sharp", Padding, 36, titleFont, titlePaint);

        // Tabs
        DrawTab(canvas, TabStreamsX, TabY, "Streams", state.ActiveTab == ViewTab.Streams);
        DrawTab(canvas, TabHistoryX, TabY, "History", state.ActiveTab == ViewTab.History);

        // yt-dlp badge
        DrawYtDlpBadge(canvas, state);

        // Status
        if (state.IsProcessing)
        {
            using var statusPaint = new SKPaint { Color = ProcessingColor, IsAntialias = true };
            using var statusFont = new SKFont(Typeface, 16) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText("Processing...", Padding, 64, statusFont, statusPaint);
        }
        else if (state.StatusMessage != null)
        {
            using var statusPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            using var statusFont = new SKFont(Typeface, 14) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText(state.StatusMessage, Padding, 64, statusFont, statusPaint);
        }

        // Video title
        if (state.VideoTitle != null)
        {
            using var vtPaint = new SKPaint { Color = TextColor, IsAntialias = true };
            using var vtFont = new SKFont(Typeface, 16) { Edging = SKFontEdging.SubpixelAntialias };
            var title = TruncateText(state.VideoTitle, vtFont, Width - Padding * 2);
            canvas.DrawText(title, Padding, 90, vtFont, vtPaint);
        }

        // URL
        if (state.DetectedYouTubeUrl != null)
        {
            using var urlPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            using var urlFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
            var url = TruncateText(state.DetectedYouTubeUrl, urlFont, Width - Padding * 2);
            canvas.DrawText(url, Padding, 110, urlFont, urlPaint);
        }

        // Separator
        using var sepPaint = new SKPaint { Color = AccentColor.WithAlpha(80) };
        canvas.DrawRect(0, HeaderHeight - 1, Width, 1, sepPaint);
    }

    private static void DrawTab(SKCanvas canvas, int x, int y, string text, bool active)
    {
        var bgColor = active ? AccentColor.WithAlpha(60) : SubTextColor.WithAlpha(20);
        var textColor = active ? AccentColor : SubTextColor;

        using var bgPaint = new SKPaint { Color = bgColor };
        canvas.DrawRoundRect(x, y, TabWidth, TabHeight, 4, 4, bgPaint);

        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        using var font = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
        var textWidth = font.MeasureText(text);
        canvas.DrawText(text, x + (TabWidth - textWidth) / 2f, y + 18, font, textPaint);
    }

    private static void DrawYtDlpBadge(SKCanvas canvas, AppState state)
    {
        var (text, color) = state.YtDlpState switch
        {
            YtDlpStatus.Present => ("yt-dlp: ready", SuccessColor),
            YtDlpStatus.Checking => ("yt-dlp: checking...", ProcessingColor),
            YtDlpStatus.Downloading => ($"yt-dlp: downloading {state.YtDlpDownloadPercent}%", ProcessingColor),
            YtDlpStatus.Downloaded => ("yt-dlp: updated", SuccessColor),
            YtDlpStatus.NotFound => ("yt-dlp: not found", ErrorColor),
            YtDlpStatus.Failed => ("yt-dlp: download failed", ErrorColor),
            _ => (null, SubTextColor),
        };

        if (text == null) return;

        using var font = new SKFont(Typeface, 11) { Edging = SKFontEdging.SubpixelAntialias };
        var textWidth = font.MeasureText(text);
        var badgeX = Width - Padding - textWidth - 16;
        const int badgeY = 52;

        using var bgPaint = new SKPaint { Color = color.WithAlpha(30) };
        canvas.DrawRoundRect(badgeX, badgeY, textWidth + 16, 20, 4, 4, bgPaint);

        using var textPaint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, badgeX + 8, badgeY + 14, font, textPaint);
    }

    private static void DrawFilterBar(SKCanvas canvas, AppState state)
    {
        using var labelFont = new SKFont(Typeface, 11) { Edging = SKFontEdging.SubpixelAntialias };
        using var labelPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };

        canvas.DrawText("Show:", FilterStartX, FilterY + 16, labelFont, labelPaint);

        var filters = new[] { state.ShowYtDlp, state.ShowMuxed, state.ShowVideo, state.ShowAudio };

        for (var i = 0; i < FilterLabels.Length; i++)
        {
            var x = FilterStartX + 40 + i * FilterItemWidth;
            var isChecked = filters[i];

            DrawCheckbox(canvas, x, FilterY + 4, FilterLabels[i], isChecked, labelFont);
        }
    }

    private static void DrawCheckbox(SKCanvas canvas, float x, float y, string label, bool isChecked, SKFont font)
    {
        // Checkbox border
        using var borderPaint = new SKPaint
        {
            Color = isChecked ? AccentColor : SubTextColor.WithAlpha(100),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
        };
        canvas.DrawRoundRect(x, y, CheckboxSize, CheckboxSize, 3, 3, borderPaint);

        // Checked fill
        if (isChecked)
        {
            using var fillPaint = new SKPaint { Color = AccentColor.WithAlpha(60) };
            canvas.DrawRoundRect(x + 1, y + 1, CheckboxSize - 2, CheckboxSize - 2, 2, 2, fillPaint);

            // Checkmark
            using var checkPaint = new SKPaint
            {
                Color = AccentColor,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                StrokeCap = SKStrokeCap.Round,
            };
            using var path = new SKPath();
            path.MoveTo(x + 3, y + 8);
            path.LineTo(x + 6.5f, y + 12);
            path.LineTo(x + 13, y + 5);
            canvas.DrawPath(path, checkPaint);
        }

        // Label
        using var textPaint = new SKPaint { Color = isChecked ? TextColor : SubTextColor, IsAntialias = true };
        canvas.DrawText(label, x + CheckboxSize + 4, y + 13, font, textPaint);
    }

    /// <summary>
    /// Returns the content list start Y depending on whether filters are shown.
    /// </summary>
    private static int GetListStartY(bool showFilters)
    {
        var y = HeaderHeight;
        if (showFilters) y += FilterBarHeight;
        return y;
    }

    private static void DrawStreamList(SKCanvas canvas, AppState state)
    {
        var filtered = state.FilteredStreams;

        if (filtered.Count == 0)
        {
            if (!state.IsProcessing)
            {
                using var emptyPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
                using var emptyFont = new SKFont(Typeface, 16) { Edging = SKFontEdging.SubpixelAntialias };
                var text = state.Streams.Count == 0
                    ? "Copy a YouTube URL to get started"
                    : "No streams match filters";
                canvas.DrawText(text, Width / 2f - 150, Height / 2f, emptyFont, emptyPaint);
            }
            return;
        }

        // Column headers
        var y = GetListStartY(true) + 8;
        using var colPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
        using var colFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
        canvas.DrawText("TYPE", Padding, y + 14, colFont, colPaint);
        canvas.DrawText("QUALITY", 100, y + 14, colFont, colPaint);
        canvas.DrawText("CODEC", 280, y + 14, colFont, colPaint);
        canvas.DrawText("CONTAINER", 460, y + 14, colFont, colPaint);
        canvas.DrawText("SIZE", 600, y + 14, colFont, colPaint);
        canvas.DrawText("ACTION", Width - 100, y + 14, colFont, colPaint);

        y += ColumnHeaderHeight;

        var visibleStreams = filtered
            .Skip(state.ScrollOffset)
            .Take(MaxVisibleRows)
            .ToList();

        using var rowFont = new SKFont(Typeface, 14) { Edging = SKFontEdging.SubpixelAntialias };

        for (var i = 0; i < visibleStreams.Count; i++)
        {
            var stream = visibleStreams[i];
            var globalIdx = i + state.ScrollOffset;
            var rowY = y + i * RowHeight;

            if (globalIdx == state.SelectedIndex)
            {
                using var selPaint = new SKPaint { Color = SelectedBgColor };
                canvas.DrawRoundRect(Padding - 4, rowY, Width - Padding * 2 + 8, RowHeight - 4, 6, 6, selPaint);
            }

            DrawTypeBadge(canvas, stream.Type, Padding, rowY);

            using var qualPaint = new SKPaint { Color = TextColor, IsAntialias = true };
            canvas.DrawText(stream.Quality, 100, rowY + 24, rowFont, qualPaint);

            using var codecPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            canvas.DrawText(stream.Codec, 280, rowY + 24, rowFont, codecPaint);
            canvas.DrawText(stream.Container, 460, rowY + 24, rowFont, codecPaint);

            if (stream.Size.HasValue)
                canvas.DrawText(FormatSize(stream.Size.Value), 600, rowY + 24, rowFont, codecPaint);

            DrawCopyButton(canvas, rowY);
        }
    }

    private static void DrawHistoryList(SKCanvas canvas, AppState state)
    {
        if (state.History.Count == 0)
        {
            using var emptyPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            using var emptyFont = new SKFont(Typeface, 16) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText("No conversion history yet", Width / 2f - 110, Height / 2f, emptyFont, emptyPaint);
            return;
        }

        var y = GetListStartY(false) + 8;
        using var colPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
        using var colFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
        canvas.DrawText("TITLE", Padding, y + 14, colFont, colPaint);
        canvas.DrawText("STREAMS", 600, y + 14, colFont, colPaint);
        canvas.DrawText("TIME", 720, y + 14, colFont, colPaint);
        canvas.DrawText("", Width - 100, y + 14, colFont, colPaint);

        y += ColumnHeaderHeight;

        var visibleEntries = state.History
            .Skip(state.HistoryScrollOffset)
            .Take(MaxVisibleRows)
            .ToList();

        using var rowFont = new SKFont(Typeface, 14) { Edging = SKFontEdging.SubpixelAntialias };

        for (var i = 0; i < visibleEntries.Count; i++)
        {
            var entry = visibleEntries[i];
            var globalIdx = i + state.HistoryScrollOffset;
            var rowY = y + i * RowHeight;

            if (globalIdx == state.HistorySelectedIndex)
            {
                using var selPaint = new SKPaint { Color = SelectedBgColor };
                canvas.DrawRoundRect(Padding - 4, rowY, Width - Padding * 2 + 8, RowHeight - 4, 6, 6, selPaint);
            }

            // Video title
            using var titlePaint = new SKPaint { Color = TextColor, IsAntialias = true };
            var title = TruncateText(entry.VideoTitle, rowFont, 560);
            canvas.DrawText(title, Padding, rowY + 24, rowFont, titlePaint);

            // Stream count
            using var countPaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            canvas.DrawText($"{entry.Streams.Count}", 600, rowY + 24, rowFont, countPaint);

            // Time
            var elapsed = DateTime.UtcNow - entry.ConvertedAt;
            var timeText = elapsed.TotalMinutes < 1 ? "just now"
                : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
                : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
                : $"{(int)elapsed.TotalDays}d ago";
            using var timePaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            using var timeFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText(timeText, 720, rowY + 24, timeFont, timePaint);

            // Open button
            using var btnPaint = new SKPaint { Color = AccentColor.WithAlpha(60) };
            canvas.DrawRoundRect(Width - 116, rowY + 6, 80, 28, 4, 4, btnPaint);
            using var btnTextPaint = new SKPaint { Color = AccentColor, IsAntialias = true };
            using var btnFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText("OPEN", Width - 92, rowY + 24, btnFont, btnTextPaint);
        }
    }

    private static void DrawTypeBadge(SKCanvas canvas, string type, int x, int rowY)
    {
        var (badgeColor, badgeText) = type switch
        {
            "yt-video" => (HlsBadgeColor, "YT-DL"),
            "yt-audio" => (HlsBadgeColor, "YT-DL"),
            "dash" => (DashBadgeColor, "DASH"),
            "hls" => (HlsBadgeColor, "HLS"),
            "video" => (VideoBadgeColor, "VIDEO"),
            "audio" => (AudioBadgeColor, "AUDIO"),
            _ => (MuxedBadgeColor, "MUXED")
        };
        using var badgePaint = new SKPaint { Color = badgeColor.WithAlpha(40) };
        var badgeWidth = badgeText.Length > 5 ? 72 : 64;
        canvas.DrawRoundRect(x, rowY + 6, badgeWidth, 24, 4, 4, badgePaint);
        using var badgeTextPaint = new SKPaint { Color = badgeColor, IsAntialias = true };
        using var badgeFont = new SKFont(Typeface, 11) { Edging = SKFontEdging.SubpixelAntialias };
        canvas.DrawText(badgeText, x + 8, rowY + 22, badgeFont, badgeTextPaint);
    }

    private static void DrawCopyButton(SKCanvas canvas, int rowY)
    {
        using var btnPaint = new SKPaint { Color = AccentColor.WithAlpha(60) };
        canvas.DrawRoundRect(Width - 116, rowY + 6, 80, 28, 4, 4, btnPaint);
        using var btnTextPaint = new SKPaint { Color = AccentColor, IsAntialias = true };
        using var btnFont = new SKFont(Typeface, 12) { Edging = SKFontEdging.SubpixelAntialias };
        canvas.DrawText("COPY", Width - 92, rowY + 24, btnFont, btnTextPaint);
    }

    private static void DrawFooter(SKCanvas canvas, AppState state)
    {
        using var footerBgPaint = new SKPaint { Color = HeaderBgColor };
        canvas.DrawRect(0, Height - FooterHeight, Width, FooterHeight, footerBgPaint);

        using var sepPaint = new SKPaint { Color = AccentColor.WithAlpha(80) };
        canvas.DrawRect(0, Height - FooterHeight, Width, 1, sepPaint);

        // Copied feedback
        if (state.CopiedUrl != null && state.CopiedAt.HasValue
            && (DateTime.UtcNow - state.CopiedAt.Value).TotalSeconds < 3)
        {
            using var copyPaint = new SKPaint { Color = SuccessColor, IsAntialias = true };
            using var copyFont = new SKFont(Typeface, 14) { Edging = SKFontEdging.SubpixelAntialias };
            canvas.DrawText("URL copied to clipboard!", Padding, ScrollBtnY + 24, copyFont, copyPaint);
        }

        // Scroll buttons
        var itemCount = state.ActiveTab == ViewTab.Streams
            ? state.FilteredStreams.Count
            : state.History.Count;
        var scrollOffset = state.ActiveTab == ViewTab.Streams
            ? state.ScrollOffset
            : state.HistoryScrollOffset;

        if (itemCount > MaxVisibleRows)
        {
            var maxOffset = itemCount - MaxVisibleRows;
            var canScrollUp = scrollOffset > 0;
            var canScrollDown = scrollOffset < maxOffset;

            DrawScrollButton(canvas, ScrollUpBtnX, ScrollBtnY, "\u25B2 UP", canScrollUp);
            DrawScrollButton(canvas, ScrollDownBtnX, ScrollBtnY, "\u25BC DOWN", canScrollDown);

            using var pagePaint = new SKPaint { Color = SubTextColor, IsAntialias = true };
            using var pageFont = new SKFont(Typeface, 11) { Edging = SKFontEdging.SubpixelAntialias };
            var info = $"{scrollOffset + 1}-{Math.Min(scrollOffset + MaxVisibleRows, itemCount)} / {itemCount}";
            canvas.DrawText(info, Width - Padding - 120, ScrollBtnY + 24, pageFont, pagePaint);
        }

        // Auto-start checkbox
        using var startupFont = new SKFont(Typeface, 11) { Edging = SKFontEdging.SubpixelAntialias };
        DrawCheckbox(canvas, Width - Padding - 120, Height - FooterHeight + 40, "Auto Start", state.AutoStartEnabled, startupFont);
    }

    private static void DrawScrollButton(SKCanvas canvas, int x, int y, string text, bool enabled)
    {
        var bgColor = enabled ? AccentColor.WithAlpha(60) : SubTextColor.WithAlpha(20);
        var textColor = enabled ? AccentColor : SubTextColor.WithAlpha(60);

        using var bgPaint = new SKPaint { Color = bgColor };
        canvas.DrawRoundRect(x, y, ScrollBtnWidth, ScrollBtnHeight, 6, 6, bgPaint);

        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        using var font = new SKFont(Typeface, 13) { Edging = SKFontEdging.SubpixelAntialias };
        var textWidth = font.MeasureText(text);
        canvas.DrawText(text, x + (ScrollBtnWidth - textWidth) / 2f, y + 24, font, textPaint);
    }

    public enum HitResult { None, Row, CopyButton, ScrollUp, ScrollDown, TabStreams, TabHistory, Filter, AutoStart }

    /// <summary>
    /// Hit test. Returns the hit result and associated index.
    /// For Filter, RowIndex is the filter index (0=YtDlp, 1=Muxed, 2=Video, 3=Audio).
    /// </summary>
    public (HitResult Result, int RowIndex) HitTest(float x, float y, int scrollOffset, ViewTab activeTab = ViewTab.Streams)
    {
        // Tab hit test
        if (y >= TabY && y <= TabY + TabHeight)
        {
            if (x >= TabStreamsX && x <= TabStreamsX + TabWidth)
                return (HitResult.TabStreams, -1);
            if (x >= TabHistoryX && x <= TabHistoryX + TabWidth)
                return (HitResult.TabHistory, -1);
        }

        // Filter checkbox hit test
        if (y >= FilterY && y <= FilterY + FilterBarHeight)
        {
            for (var i = 0; i < FilterLabels.Length; i++)
            {
                var fx = FilterStartX + 40 + i * FilterItemWidth;
                if (x >= fx && x <= fx + CheckboxSize + 50)
                    return (HitResult.Filter, i);
            }
        }

        // Auto-start checkbox hit test
        var autoStartX = Width - Padding - 120;
        var autoStartY = Height - FooterHeight + 40;
        if (x >= autoStartX && x <= autoStartX + CheckboxSize + 70
            && y >= autoStartY && y <= autoStartY + CheckboxSize)
            return (HitResult.AutoStart, -1);

        // Scroll button hit test
        if (y >= ScrollBtnY && y <= ScrollBtnY + ScrollBtnHeight)
        {
            if (x >= ScrollUpBtnX && x <= ScrollUpBtnX + ScrollBtnWidth)
                return (HitResult.ScrollUp, -1);
            if (x >= ScrollDownBtnX && x <= ScrollDownBtnX + ScrollBtnWidth)
                return (HitResult.ScrollDown, -1);
        }

        // Row hit test
        var showFilters = activeTab == ViewTab.Streams;
        var listStartY = GetListStartY(showFilters) + 8 + ColumnHeaderHeight;
        if (y < listStartY || y > Height - FooterHeight)
            return (HitResult.None, -1);

        var rowIndex = (int)((y - listStartY) / RowHeight) + scrollOffset;
        var isCopy = x >= Width - 116 && x <= Width - 36;

        return (isCopy ? HitResult.CopyButton : HitResult.Row, rowIndex);
    }

    private static string TruncateText(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
            return text;

        while (text.Length > 3 && font.MeasureText(text + "...") > maxWidth)
            text = text[..^1];

        return text + "...";
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    public void Dispose()
    {
        _surface.Dispose();
    }
}
