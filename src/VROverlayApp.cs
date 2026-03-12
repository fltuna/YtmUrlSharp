using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OVRSharp;
using Valve.VR;
using Application = OVRSharp.Application;
using Overlay = OVRSharp.Overlay;

namespace YtmUrlSharp;

/// <summary>
/// Manages the SteamVR dashboard overlay lifecycle.
/// Created/destroyed by App when SteamVR connects/disconnects.
/// </summary>
public sealed class VROverlayManager : IDisposable
{
    private const string OverlayKey = "ytmurlsharp.overlay";
    private const string OverlayName = "YTM URL Sharp";

    private readonly OverlayRenderer _renderer;
    private readonly ILogger<VROverlayManager> _logger;
    private Application? _vrApp;
    private Overlay? _overlay;

    public bool IsConnected => _vrApp != null;

    public VROverlayManager(OverlayRenderer renderer, ILoggerFactory loggerFactory)
    {
        _renderer = renderer;
        _logger = loggerFactory.CreateLogger<VROverlayManager>();
    }

    /// <summary>
    /// Attempts to connect to SteamVR and create the dashboard overlay.
    /// Returns true if successful.
    /// </summary>
    public bool TryConnect()
    {
        if (_vrApp != null) return true;

        try
        {
            // Check if SteamVR process is actually running before calling OpenVR.Init()
            // OpenVR.Init() and even IsHmdPresent() can auto-launch SteamVR
            if (!IsSteamVRRunning())
                return false;

            _vrApp = new Application(Application.ApplicationType.Overlay);
            _overlay = new Overlay(OverlayKey, OverlayName, dashboardOverlay: true);
            _overlay.WidthInMeters = 2.5f;
            _overlay.MouseScale = new HmdVector2_t
            {
                v0 = OverlayRenderer.Width,
                v1 = OverlayRenderer.Height,
            };
            _overlay.InputMethod = VROverlayInputMethod.Mouse;

            // Set dashboard thumbnail icon from embedded resource
            SetThumbnailFromResource();

            _logger.LogInformation("SteamVR connected, dashboard overlay created");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SteamVR connection failed");
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// Disconnects from SteamVR and cleans up the overlay.
    /// </summary>
    public void Disconnect()
    {
        try { _overlay?.Destroy(); } catch { }
        _overlay = null;

        try { _vrApp?.Shutdown(); } catch { }
        _vrApp = null;
    }

    /// <summary>
    /// Polls overlay events and returns any actions to handle.
    /// Returns false if SteamVR disconnected.
    /// </summary>
    public bool PollEvents(AppState state)
    {
        if (_overlay == null) return false;

        try
        {
            var evt = new VREvent_t();
            var size = (uint)Marshal.SizeOf<VREvent_t>();

            while (OpenVR.Overlay.PollNextOverlayEvent(_overlay.Handle, ref evt, size))
            {
                switch ((EVREventType)evt.eventType)
                {
                    case EVREventType.VREvent_MouseButtonUp:
                        HandleMouseUp(evt, state);
                        break;
                    case EVREventType.VREvent_ScrollDiscrete:
                    case EVREventType.VREvent_ScrollSmooth:
                        HandleScroll(evt, state);
                        break;
                    case EVREventType.VREvent_Quit:
                        _logger.LogInformation("SteamVR is shutting down");
                        Disconnect();
                        return false;
                }
            }

            return true;
        }
        catch
        {
            _logger.LogInformation("SteamVR disconnected");
            Disconnect();
            return false;
        }
    }

    public void UpdateTexture(AppState state)
    {
        if (_overlay == null) return;

        try
        {
            var pixels = _renderer.Render(state);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                OpenVR.Overlay.SetOverlayRaw(
                    _overlay.Handle,
                    handle.AddrOfPinnedObject(),
                    (uint)OverlayRenderer.Width,
                    (uint)OverlayRenderer.Height,
                    4);
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            // Texture update failed — SteamVR may have disconnected
        }
    }

    private void HandleMouseUp(VREvent_t evt, AppState state)
    {
        var data = evt.data.mouse;
        var flippedY = OverlayRenderer.Height - data.y;
        var scrollOffset = state.ActiveTab == ViewTab.Streams
            ? state.ScrollOffset
            : state.HistoryScrollOffset;
        var (result, rowIndex) = _renderer.HitTest(data.x, flippedY, scrollOffset, state.ActiveTab);

        switch (result)
        {
            case OverlayRenderer.HitResult.TabStreams:
                state.ActiveTab = ViewTab.Streams;
                state.NeedsRedraw = true;
                break;

            case OverlayRenderer.HitResult.TabHistory:
                state.ActiveTab = ViewTab.History;
                state.NeedsRedraw = true;
                break;

            case OverlayRenderer.HitResult.Filter:
                ToggleFilter(state, rowIndex);
                break;

            case OverlayRenderer.HitResult.ScrollUp:
                ScrollBy(state, -1);
                break;

            case OverlayRenderer.HitResult.ScrollDown:
                ScrollBy(state, 1);
                break;

            case OverlayRenderer.HitResult.CopyButton:
                HandleAction(state, rowIndex);
                break;

            case OverlayRenderer.HitResult.Row:
                HandleRowSelect(state, rowIndex);
                break;
        }
    }

    private static void HandleScroll(VREvent_t evt, AppState state)
    {
        var scrollDelta = evt.data.scroll.ydelta;
        if (scrollDelta == 0) return;

        ScrollBy(state, scrollDelta > 0 ? -1 : 1);
    }

    private static void ToggleFilter(AppState state, int filterIndex)
    {
        switch (filterIndex)
        {
            case 0: state.ShowYtDlp = !state.ShowYtDlp; break;
            case 1: state.ShowMuxed = !state.ShowMuxed; break;
            case 2: state.ShowVideo = !state.ShowVideo; break;
            case 3: state.ShowAudio = !state.ShowAudio; break;
        }
        state.ScrollOffset = 0;
        state.SelectedIndex = -1;
        state.NeedsRedraw = true;
    }

    private void HandleAction(AppState state, int rowIndex)
    {
        if (state.ActiveTab == ViewTab.Streams)
        {
            var filtered = state.FilteredStreams;
            if (rowIndex < 0 || rowIndex >= filtered.Count) return;
            state.SelectedIndex = rowIndex;
            var stream = filtered[rowIndex];
            state.CopyStream(stream);
            _logger.LogInformation("Copied {Type} {Quality} URL ({Url}...)",
                stream.Type, stream.Quality,
                UrlMasker.MaskForLog(stream.Url[..Math.Min(80, stream.Url.Length)]));
            state.NeedsRedraw = true;
        }
        else
        {
            if (rowIndex < 0 || rowIndex >= state.History.Count) return;
            state.RestoreFromHistory(state.History[rowIndex]);
            _logger.LogInformation("Restored history: {Title}", state.VideoTitle);
        }
    }

    private static void HandleRowSelect(AppState state, int rowIndex)
    {
        if (state.ActiveTab == ViewTab.Streams)
        {
            if (rowIndex >= 0 && rowIndex < state.FilteredStreams.Count)
                state.SelectedIndex = rowIndex;
            state.NeedsRedraw = true;
        }
        else
        {
            // History row click: restore
            if (rowIndex >= 0 && rowIndex < state.History.Count)
                state.RestoreFromHistory(state.History[rowIndex]);
        }
    }

    private static void ScrollBy(AppState state, int delta)
    {
        if (state.ActiveTab == ViewTab.Streams)
        {
            var maxOffset = Math.Max(0, state.FilteredStreams.Count - OverlayRenderer.MaxVisibleRows);
            state.ScrollOffset = Math.Clamp(state.ScrollOffset + delta, 0, maxOffset);
        }
        else
        {
            var maxOffset = Math.Max(0, state.History.Count - OverlayRenderer.MaxVisibleRows);
            state.HistoryScrollOffset = Math.Clamp(state.HistoryScrollOffset + delta, 0, maxOffset);
        }
        state.NeedsRedraw = true;
    }

    private void SetThumbnailFromResource()
    {
        try
        {
            using var stream = typeof(VROverlayManager).Assembly.GetManifestResourceStream("icon.png");
            if (stream == null || _overlay == null) return;

            var tempPath = Path.Combine(Path.GetTempPath(), "ytmurlsharp_icon.png");
            using (var file = File.Create(tempPath))
                stream.CopyTo(file);

            _overlay.SetThumbnailTextureFromFile(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to set VR thumbnail: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Checks if SteamVR (vrserver) is running without calling any OpenVR API.
    /// This avoids auto-launching SteamVR.
    /// </summary>
    private static bool IsSteamVRRunning()
    {
        try
        {
            return Process.GetProcessesByName("vrserver").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => Disconnect();
}
