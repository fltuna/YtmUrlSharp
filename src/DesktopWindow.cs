using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace YtmUrlSharp;

/// <summary>
/// Desktop window using WinForms + SkiaSharp rendering.
/// </summary>
public sealed class DesktopWindow : Form
{
    private readonly OverlayRenderer _renderer;
    private readonly AppState _state;
    private Bitmap? _backBuffer;

    public DesktopWindow(OverlayRenderer renderer, AppState state)
    {
        _renderer = renderer;
        _state = state;

        Text = "YTM URL Sharp";
        ClientSize = new Size(OverlayRenderer.Width, OverlayRenderer.Height);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;

        var icoStream = typeof(DesktopWindow).Assembly.GetManifestResourceStream("icon.ico");
        if (icoStream != null)
            Icon = new Icon(icoStream);
    }

    public void UpdateFrame()
    {
        if (!_state.NeedsRedraw && _backBuffer != null)
            return;

        var pixels = _renderer.Render(_state);

        var bmp = new Bitmap(OverlayRenderer.Width, OverlayRenderer.Height, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        // SkiaSharp RGBA -> GDI+ BGRA
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

        Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
        bmp.UnlockBits(bmpData);

        var old = _backBuffer;
        _backBuffer = bmp;
        old?.Dispose();

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_backBuffer != null)
            e.Graphics.DrawImageUnscaled(_backBuffer, 0, 0);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        HandleClick(e.X, e.Y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        HandleScroll(e.Delta > 0 ? 1 : -1);
    }

    private void HandleClick(float x, float y)
    {
        var scrollOffset = _state.ActiveTab == ViewTab.Streams
            ? _state.ScrollOffset
            : _state.HistoryScrollOffset;

        var (result, rowIndex) = _renderer.HitTest(x, y, scrollOffset, _state.ActiveTab);

        switch (result)
        {
            case OverlayRenderer.HitResult.TabStreams:
                _state.ActiveTab = ViewTab.Streams;
                _state.NeedsRedraw = true;
                break;

            case OverlayRenderer.HitResult.TabHistory:
                _state.ActiveTab = ViewTab.History;
                _state.NeedsRedraw = true;
                break;

            case OverlayRenderer.HitResult.Filter:
                ToggleFilter(rowIndex);
                break;

            case OverlayRenderer.HitResult.ScrollUp:
                ScrollBy(-1);
                break;

            case OverlayRenderer.HitResult.ScrollDown:
                ScrollBy(1);
                break;

            case OverlayRenderer.HitResult.CopyButton:
                HandleAction(rowIndex);
                break;

            case OverlayRenderer.HitResult.Row:
                HandleRowSelect(rowIndex);
                break;

            case OverlayRenderer.HitResult.AutoStart:
                StartupManager.Toggle();
                _state.AutoStartEnabled = StartupManager.IsRegistered;
                _state.NeedsRedraw = true;
                break;
        }
    }

    private void ToggleFilter(int filterIndex)
    {
        switch (filterIndex)
        {
            case 0: _state.ShowYtDlp = !_state.ShowYtDlp; break;
            case 1: _state.ShowMuxed = !_state.ShowMuxed; break;
            case 2: _state.ShowVideo = !_state.ShowVideo; break;
            case 3: _state.ShowAudio = !_state.ShowAudio; break;
        }
        _state.ScrollOffset = 0;
        _state.SelectedIndex = -1;
        _state.NeedsRedraw = true;
    }

    private void HandleAction(int rowIndex)
    {
        if (_state.ActiveTab == ViewTab.Streams)
        {
            var filtered = _state.FilteredStreams;
            if (rowIndex >= 0 && rowIndex < filtered.Count)
            {
                _state.SelectedIndex = rowIndex;
                _state.CopyStream(filtered[rowIndex]);
                _state.NeedsRedraw = true;
            }
        }
        else
        {
            // OPEN: restore history entry to streams view
            if (rowIndex >= 0 && rowIndex < _state.History.Count)
                _state.RestoreFromHistory(_state.History[rowIndex]);
        }
    }

    private void HandleRowSelect(int rowIndex)
    {
        if (_state.ActiveTab == ViewTab.Streams)
        {
            if (rowIndex >= 0 && rowIndex < _state.FilteredStreams.Count)
            {
                _state.SelectedIndex = rowIndex;
                _state.NeedsRedraw = true;
            }
        }
        else
        {
            // History row click: restore that entry
            if (rowIndex >= 0 && rowIndex < _state.History.Count)
                _state.RestoreFromHistory(_state.History[rowIndex]);
        }
    }

    private void HandleScroll(int direction)
    {
        ScrollBy(-direction);
    }

    private void ScrollBy(int delta)
    {
        if (_state.ActiveTab == ViewTab.Streams)
        {
            var maxOffset = Math.Max(0, _state.FilteredStreams.Count - OverlayRenderer.MaxVisibleRows);
            _state.ScrollOffset = Math.Clamp(_state.ScrollOffset + delta, 0, maxOffset);
        }
        else
        {
            var maxOffset = Math.Max(0, _state.History.Count - OverlayRenderer.MaxVisibleRows);
            _state.HistoryScrollOffset = Math.Clamp(_state.HistoryScrollOffset + delta, 0, maxOffset);
        }
        _state.NeedsRedraw = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _backBuffer?.Dispose();
        base.Dispose(disposing);
    }
}
