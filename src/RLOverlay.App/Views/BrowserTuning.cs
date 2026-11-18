using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Windows.Foundation;
using Windows.Graphics.Capture;
using RLOverlay.Core;

namespace RLOverlay.App.Views;

/// <summary>
/// Keeps the embedded tracker.gg pages light, so loading a lobby costs the game as little as possible:
/// <list type="bullet">
/// <item>Only tracker.gg itself (its pages, images, fonts and data) and Cloudflare's browser check may load. The page's
/// ads, video players and trackers never showed on the overlay anyway, and they were most of the work: dozens of
/// extra hosts, frames and processes per page.</item>
/// <item>The browser's processes run at below-normal priority, so the game wins whenever both want the CPU.</item>
/// <item>At most <see cref="MaxLoadingAtOnce"/> pages load at the same time; the rest wait their turn.</item>
/// <item>A browser control that isn't on screen stops copying frames (see <see cref="FollowVisibility"/>).</item>
/// </list>
/// </summary>
internal static class BrowserTuning
{
    /// <summary>Pages that may be loading at once (each holds a turn until its row is shown or it gives up).</summary>
    public const int MaxLoadingAtOnce = 3;

    private static readonly SemaphoreSlim s_turns = new(MaxLoadingAtOnce, MaxLoadingAtOnce);
    private static readonly HashSet<int> s_lowered = [];
    private static readonly HashSet<CoreWebView2Environment> s_watched = [];

    /// <summary>Waits for a loading turn. Dispose the result to hand the turn on.</summary>
    public static async Task<IDisposable> TakeTurnAsync(CancellationToken token)
    {
        await s_turns.WaitAsync(token).ConfigureAwait(true);
        return new Turn();
    }

    /// <summary>Blocks every request the overlay doesn't need (see <see cref="TrackerPage.AllowsRequest"/>).</summary>
    public static void BlockExtras(CoreWebView2 core, ILogger logger)
    {
        var blocked = 0;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (TrackerPage.AllowsRequest(e.Request.Uri))
            {
                return;
            }

            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            if (++blocked == 1)
            {
                logger.LogDebug("Overlay: blocking requests to other sites than tracker.gg (first: {Url})", e.Request.Uri);
            }
        };
    }

    /// <summary>Puts every process of the shared browser below normal priority, now and whenever new ones start.</summary>
    public static void LowerPriority(CoreWebView2Environment environment, ILogger logger)
    {
        if (!s_watched.Add(environment))
        {
            return;
        }

        void Apply()
        {
            try
            {
                foreach (var info in environment.GetProcessInfos())
                {
                    if (!s_lowered.Add(info.ProcessId))
                    {
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById(info.ProcessId);
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Already gone, or not ours to change.
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not list the browser's processes");
            }
        }

        environment.ProcessInfosChanged += (_, _) => Apply();
        Apply();
    }

    private static readonly DependencyProperty? s_frameDivider =
        typeof(WebView2CompositionControl).GetField("FpsDividerProperty", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DependencyProperty;

    private static readonly FieldInfo? s_d3dImage = typeof(WebView2CompositionControl).GetField("_d3dImage", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Frames copied per display refresh while a control is off screen, if its copying can't be paused.</summary>
    private const int HiddenFrameDivider = 60;

    /// <summary>
    /// Copies the page's frames onto the overlay only while the control is on screen.
    /// </summary>
    /// <remarks>
    /// WebView2CompositionControl copies the page into WPF through a Windows.Graphics.Capture session that delivers a
    /// frame on every refresh of the display (240 a second on a 240 Hz screen) for as long as the control exists, even
    /// hidden and with nothing changing. Four rows cost about 40% of a CPU core that way. This WebView2 version has no
    /// public switch for it, so while the control is hidden its own frame handler is detached from the capture (and the
    /// frames that piled up are dropped when it's attached again, so capture carries on). Should those internals ever
    /// differ, only its (also internal) frame divider is raised, which still saves most of the drawing.
    /// </remarks>
    public static void FollowVisibility(WebView2CompositionControl web, ILogger logger)
    {
        web.IsVisibleChanged += (_, _) => Apply(web, logger);
        web.CoreWebView2InitializationCompleted += (_, _) => Apply(web, logger);
        Apply(web, logger);
    }

    private static void Apply(WebView2CompositionControl web, ILogger logger)
    {
        var live = web.IsVisible;
        if (s_frameDivider is not null)
        {
            web.SetValue(s_frameDivider, live ? 1 : HiddenFrameDivider);
        }

        try
        {
            if (s_d3dImage?.GetValue(web) is not { } image
                || image.GetType().GetField("_framePool", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(image) is not Direct3D11CaptureFramePool pool
                || Delegate.CreateDelegate(typeof(TypedEventHandler<Direct3D11CaptureFramePool, object>), image, "OnFrameArrived", false, false)
                    is not TypedEventHandler<Direct3D11CaptureFramePool, object> handler)
            {
                return;
            }

            pool.FrameArrived -= handler;
            if (live)
            {
                pool.FrameArrived += handler;
                while (pool.TryGetNextFrame() is { } waiting)
                {
                    waiting.Dispose(); // frees the pool, so new frames arrive again
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Overlay: could not pause a browser control's frames; only slowing them down");
        }
    }

    private sealed class Turn : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                s_turns.Release();
            }
        }
    }
}
