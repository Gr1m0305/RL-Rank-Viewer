using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RLOverlay.Core;

namespace RLOverlay.App.Services;

/// <summary>Opens tracker profile pages in the default browser (or only logs them with <c>--dry-run-links</c>).</summary>
internal sealed class LinkOpener(bool dryRun, ILogger<LinkOpener> logger)
{
    /// <summary>Raised for every accepted URL, opened or dry-run.</summary>
    public event Action<string>? Opened;

    public void Open(string url)
    {
        // Only ever open links built by TrackerUrls; never a URL that came from data.
        if (!url.StartsWith(TrackerUrls.ProfileBase, StringComparison.Ordinal))
        {
            logger.LogWarning("Refusing to open unexpected URL {Url}", url);
            return;
        }

        if (dryRun)
        {
            logger.LogInformation("Profile link (dry run): {Url}", url);
        }
        else
        {
            logger.LogInformation("Opening profile {Url}", url);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning("Could not open the browser: {Error}", ex.Message);
            }
        }

        Opened?.Invoke(url);
    }
}
