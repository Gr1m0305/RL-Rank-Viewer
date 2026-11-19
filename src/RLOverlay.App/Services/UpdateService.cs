using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.App.Setup;
using RLOverlay.Core.Updates;

namespace RLOverlay.App.Services;

/// <summary>What the update checker last found, in words, for the settings window.</summary>
/// <param name="ReadyVersion">A downloaded version that can be installed now, else null.</param>
/// <param name="CanCheck">False while checking or downloading, and when update checks are off for this run.</param>
public sealed record UpdateState(string Text, string? ReadyVersion, bool CanCheck)
{
    public static UpdateState Off { get; } = new("Update checks are off for this run.", null, false);
}

/// <summary>Where the updater looks and what it passes on to setup.</summary>
/// <param name="Enabled">False for demo and test runs (unless they name a test feed) and with --no-update-check.</param>
/// <param name="SetupSandbox">Testing: the sandbox install to update instead of the real one.</param>
/// <param name="DataDir">Testing: the data folder setup and the updated copy use too.</param>
internal sealed record UpdateOptions(Uri Feed, bool Enabled, string? SetupSandbox, string? DataDir);

/// <summary>
/// Keeps the app up to date from its GitHub releases. A minute after starting and every six hours it asks GitHub for
/// the latest release; a newer one's setup file is downloaded into the data folder and checked (size, SHA-256 when
/// GitHub lists it, and that it is this app at that version). The installed copy then installs it by itself the next
/// time nothing is going on (Rocket League closed, no Preview, settings closed): setup runs quietly, replaces the exe
/// and starts the new version, which says so in a notice. With "Update automatically" off, or for a copy that isn't
/// the installed one, a notice says the update is ready and Settings → General installs it. An update installed from
/// the settings window opens it again in the new version (see <see cref="UpdateMarker"/>); if setup fails, it starts
/// the old copy again instead.
/// </summary>
/// <remarks>
/// Only GitHub's API and this repository's release files are contacted, with a plain "RLOverlay/version" User-Agent;
/// nothing about the user is sent.
/// </remarks>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InstallCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private static readonly HttpClient s_http = CreateClient();

    private readonly UpdateOptions _options;
    private readonly Func<bool> _updateAutomatically;
    private readonly Func<bool> _isQuietMoment;
    private readonly Action<string, string> _notify;
    private readonly Action _quit;
    private readonly Action _reopenSettings;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _checkTimer = new();
    private readonly DispatcherTimer _installTimer = new() { Interval = InstallCheckInterval };
    private readonly Version _current;
    private string _activity = "";
    private DateTime? _lastChecked;
    private string? _problem;
    private string? _readyPath;
    private Version? _readyVersion;
    private Version? _announced;
    private bool _installing;
    private bool _disposed;

    // What the note left by the copy before this one said (see AnnounceFinishedUpdate).
    private string? _updatedFrom;
    private DateTime _updatedAt;
    private string? _failedTo;

    public UpdateService(UpdateOptions options, Func<bool> updateAutomatically, Func<bool> isQuietMoment, Action<string, string> notify, Action quit,
        Action reopenSettings, ILogger<UpdateService> logger)
    {
        _options = options;
        _updateAutomatically = updateAutomatically;
        _isQuietMoment = isQuietMoment;
        _notify = notify;
        _quit = quit;
        _reopenSettings = reopenSettings;
        _logger = logger;
        UpdateFeed.TryParseVersion(AppInfo.Version, out _current);
        _checkTimer.Tick += async (_, _) =>
        {
            _checkTimer.Interval = CheckInterval;
            await CheckAsync();
        };
        _installTimer.Tick += (_, _) => InstallIfQuiet();
    }

    public event Action? StateChanged;

    public UpdateState State
    {
        get
        {
            var updated = _updatedFrom is { } from ? $"Updated from version {from} at {_updatedAt:t}. " : "";
            if (!_options.Enabled)
            {
                // Setup starts the old copy without update checks after a failed update, so it doesn't try again at once.
                return _failedTo is { } failed
                    ? new UpdateState($"Version {failed} couldn't be installed, so you still have version {_current}. It tries again the next time the overlay starts.", null, false)
                    : updated.Length > 0 ? UpdateState.Off with { Text = updated + UpdateState.Off.Text }
                    : UpdateState.Off;
            }

            var busy = _activity.Length > 0;
            if (busy)
            {
                return new UpdateState(_activity, null, false);
            }

            if (_readyVersion is { } ready)
            {
                var how = !IsInstalledCopy() ? " This copy isn't the installed one, so Install opens setup."
                    : _updateAutomatically() ? " It installs by itself the next time Rocket League is closed, or install it now."
                    : " Install it whenever you like.";
                return new UpdateState($"Version {ready} is downloaded.{how}", ready.ToString(3), true);
            }

            var text = _problem is { } problem ? $"{updated}You have version {_current}. Couldn't look for a new one ({problem}); it tries again later."
                : _lastChecked is { } at ? $"{updated}You have the latest version ({_current}). Last checked at {at:t}."
                : $"{updated}You have version {_current}. New versions are looked for on GitHub a minute after start and every 6 hours.";
            return new UpdateState(text, null, true);
        }
    }

    /// <summary>Starts the regular checks (the first after <paramref name="firstCheck"/>) and cleans up after an update.</summary>
    public void Start(TimeSpan firstCheck)
    {
        AnnounceFinishedUpdate();
        if (!_options.Enabled)
        {
            return;
        }

        _checkTimer.Interval = firstCheck;
        _checkTimer.Start();
    }

    /// <summary>Asks the feed for the latest release and downloads it if it is newer (one check at a time).</summary>
    public async Task CheckAsync()
    {
        if (!_options.Enabled || _activity.Length > 0 || _installing || _disposed)
        {
            return;
        }

        SetActivity("Looking for a new version on GitHub…");
        try
        {
            var release = await FetchLatestAsync();
            _lastChecked = DateTime.Now;
            _problem = null;
            if (release is null || !UpdateFeed.IsNewer(release.Version, _current))
            {
                _logger.LogInformation("Updates: {Version} is the latest version", release?.Version.ToString(3) ?? AppInfo.Version);
                return;
            }

            if (_readyVersion != release.Version)
            {
                SetActivity($"Downloading version {release.Version.ToString(3)}…");
                _readyPath = await Task.Run(() => DownloadAsync(release));
                _readyVersion = release.Version;
                _logger.LogInformation("Updates: version {Version} downloaded to {Path}", release.Version.ToString(3), _readyPath);
            }

            OnReady();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _problem = Describe(ex);
            _logger.LogWarning("Updates: couldn't check or download ({Problem}): {Error}", _problem, ex.Message);
        }
        finally
        {
            SetActivity("");
        }
    }

    /// <summary>
    /// Installs the downloaded version now. The installed copy runs setup quietly and quits (setup starts the new
    /// version, which opens its settings again when <paramref name="reopenSettings"/>); any other copy opens the setup
    /// window.
    /// </summary>
    /// <param name="reopenSettings">Started from the settings window (its Install button).</param>
    public void InstallNow(bool reopenSettings = false)
    {
        if (_readyPath is not { } setup || !File.Exists(setup) || _installing)
        {
            return;
        }

        if (!IsInstalledCopy())
        {
            Run(setup, "--setup" + PassOn());
            return;
        }

        _installing = true;
        _installTimer.Stop();
        try
        {
            Directory.CreateDirectory(AppPaths.UpdatesDir);
            var marker = new UpdateMarker(AppInfo.Version, _readyVersion?.ToString(3), reopenSettings);
            File.WriteAllText(Path.Combine(AppPaths.UpdatesDir, UpdateMarker.FileName), marker.Format());
            Run(setup, $"--setup --quiet --launch --wait-for {Environment.ProcessId}{PassOn()}");
            _logger.LogInformation("Updates: installing version {Version}; quitting so setup can replace the app (then {Then})",
                _readyVersion?.ToString(3), reopenSettings ? "settings open again" : "back in the notification area");
            _quit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _installing = false;
            _problem = "setup didn't start";
            _logger.LogWarning(ex, "Updates: couldn't start setup");
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _checkTimer.Stop();
        _installTimer.Stop();
    }

    private void OnReady()
    {
        if (IsInstalledCopy() && _updateAutomatically())
        {
            _installTimer.Start();
            InstallIfQuiet();
            return;
        }

        if (_announced != _readyVersion)
        {
            _announced = _readyVersion;
            _notify($"Version {_readyVersion?.ToString(3)} is ready", "Open Settings → General to install it (the overlay restarts).");
        }
    }

    private void InstallIfQuiet()
    {
        if (_readyPath is null || !_updateAutomatically() || !IsInstalledCopy())
        {
            _installTimer.Stop();
            return;
        }

        if (_isQuietMoment())
        {
            InstallNow(reopenSettings: false); // nothing was open: the new version starts in the notification area
        }
    }

    /// <summary>
    /// The copy that just started was put there by an update (or started again after a failed one): say so once, open
    /// the settings window again if the update was started from it, and tidy the downloads folder.
    /// </summary>
    private void AnnounceFinishedUpdate()
    {
        var path = Path.Combine(AppPaths.UpdatesDir, UpdateMarker.FileName);
        try
        {
            if (File.Exists(path))
            {
                var marker = UpdateMarker.Parse(File.ReadAllText(path));
                File.Delete(path);
                switch (marker?.OutcomeFor(AppInfo.Version))
                {
                    case UpdateOutcome.Updated:
                        _updatedFrom = marker.From;
                        _updatedAt = DateTime.Now;
                        _logger.LogInformation("Updates: updated from {From} to {Version}{Reopen}", marker.From, AppInfo.Version,
                            marker.ReopenSettings ? "; opening the settings again" : "");
                        if (!marker.ReopenSettings)
                        {
                            _notify($"Updated to version {AppInfo.Version}", $"{AppInfo.Name} updated itself from version {marker.From}.");
                        }

                        break;
                    case UpdateOutcome.Failed:
                        _failedTo = marker.To;
                        _logger.LogWarning("Updates: version {To} wasn't installed; this is still {Version}", marker.To, AppInfo.Version);
                        _notify($"Version {marker.To} couldn't be installed",
                            $"{AppInfo.Name} is still on version {AppInfo.Version}. It tries again the next time it starts.");
                        break;
                }

                if (marker is { ReopenSettings: true } && marker.OutcomeFor(AppInfo.Version) != UpdateOutcome.None)
                {
                    _reopenSettings();
                }
            }

            if (Directory.Exists(AppPaths.UpdatesDir))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.UpdatesDir))
                {
                    TryDelete(file); // old setup files (one still running is left for next time)
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Updates: couldn't tidy the downloads folder");
        }
    }

    private async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var timeout = new CancellationTokenSource(FeedTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.Feed);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await s_http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no release published
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        var release = UpdateFeed.Parse(json, _options.Feed);
        if (release is null)
        {
            _logger.LogInformation("Updates: the latest release has no setup file to download");
        }

        return release;
    }

    /// <summary>Downloads and checks the setup file (kept as Gr1msRLOverlay-Setup-x.y.z.exe; a good earlier download is reused).</summary>
    private static async Task<string> DownloadAsync(ReleaseInfo release)
    {
        Directory.CreateDirectory(AppPaths.UpdatesDir);
        var target = Path.Combine(AppPaths.UpdatesDir, $"Gr1msRLOverlay-Setup-{release.Version.ToString(3)}.exe");
        if (File.Exists(target))
        {
            try
            {
                Verify(target, release, await HashFileAsync(target));
                return target;
            }
            catch (InvalidDataException)
            {
                TryDelete(target);
            }
        }

        var part = target + ".part";
        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout);
            using var response = await s_http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > release.Size)
                    {
                        throw new InvalidDataException("the download is bigger than the release says");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            Verify(part, release, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            File.Move(part, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static void Verify(string path, ReleaseInfo release, string sha256)
    {
        if (new FileInfo(path).Length != release.Size)
        {
            throw new InvalidDataException("the download is incomplete");
        }

        if (release.Sha256 is { } expected && !string.Equals(sha256, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("the download doesn't match GitHub's checksum");
        }

        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.ProductName != AppInfo.Name || !UpdateFeed.TryParseVersion(info.FileVersion, out var version) || version != release.Version)
        {
            throw new InvalidDataException($"the download isn't {AppInfo.Name} {release.Version.ToString(3)}");
        }
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)).ToLowerInvariant();
    }

    /// <summary>True when this process is the installed copy (the exe registered under Apps &amp; features).</summary>
    private bool IsInstalledCopy()
    {
        var locations = _options.SetupSandbox is { } sandbox ? InstallLocations.Sandbox(sandbox) : InstallLocations.Real();
        return new Installer(locations).FindExisting() is { } installed
            && Environment.ProcessPath is { } self
            && string.Equals(Path.GetFullPath(installed.ExePath), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase);
    }

    private string PassOn() =>
        (_options.SetupSandbox is { } sandbox ? $" --setup-sandbox \"{sandbox}\"" : "")
        + (_options.DataDir is { } data ? $" --data-dir \"{Path.GetFullPath(data)}\"" : "");

    private static void Run(string exe, string arguments) =>
        Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();

    private void SetActivity(string text)
    {
        _activity = text;
        StateChanged?.Invoke();
    }

    private static string Describe(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        TaskCanceledException => "GitHub didn't answer in time",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => "GitHub is limiting requests",
        HttpRequestException { StatusCode: { } code } => $"GitHub answered {(int)code}",
        HttpRequestException => "no connection to GitHub",
        _ => "couldn't save the download",
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // each request has its own
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"RLOverlay/{AppInfo.Version}");
        return client;
    }
}
