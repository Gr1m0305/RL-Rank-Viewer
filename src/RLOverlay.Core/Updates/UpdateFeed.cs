using System.Text.Json;

namespace RLOverlay.Core.Updates;

/// <summary>A published release: its version and the setup file to download.</summary>
/// <param name="Sha256">The setup file's SHA-256 (lower-case hex) when the feed lists it, else null.</param>
public sealed record ReleaseInfo(Version Version, string Tag, Uri SetupUrl, long Size, string? Sha256);

/// <summary>
/// Reads GitHub's "latest release" answer for this app's repository and decides whether it is a newer version whose
/// setup file may be downloaded. Only the repository's own release downloads are accepted (or, for a test feed given
/// on the command line, files from that same server).
/// </summary>
public static class UpdateFeed
{
    public const string Repository = "Gr1m0305/RL-Rank-Viewer";

    /// <summary>The setup file attached to every release (the same exe the app runs, under a setup name).</summary>
    public const string SetupAssetName = "Gr1msRLOverlay-Setup.exe";

    /// <summary>Downloads bigger than this are refused (the setup is about 70 MB).</summary>
    public const long MaxSetupSize = 300L * 1024 * 1024;

    public static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    private const string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";

    /// <summary>"v0.5.0", "0.5" or "0.5.0.0" → 0.5.0; anything after a '-' or '+' (pre-release labels) is ignored.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var t = text?.Trim() ?? "";
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        var suffix = t.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            t = t[..suffix];
        }

        if (!Version.TryParse(t, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is a later version than <paramref name="current"/> (0.5 = 0.5.0).</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>
    /// The release in a "latest release" JSON answer, or null when there is nothing to download: a draft or
    /// pre-release, a tag that isn't a version, no setup file, or a setup file from anywhere else than
    /// <see cref="IsTrustedDownload"/> allows.
    /// </summary>
    public static ReleaseInfo? Parse(string json, Uri feed)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Flag(root, "draft") || Flag(root, "prerelease"))
            {
                return null;
            }

            var tag = Text(root, "tag_name");
            if (tag is null || !TryParseVersion(tag, out var version)
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(Text(asset, "name"), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url) || !IsTrustedDownload(url, feed))
                {
                    return null;
                }

                var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                if (size <= 0 || size > MaxSetupSize)
                {
                    return null;
                }

                return new ReleaseInfo(version, tag, url, size, Sha256(Text(asset, "digest")));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads from GitHub's feed must be this repository's release files (over https). A test feed (only ever given
    /// on the command line) may serve files from its own server.
    /// </summary>
    public static bool IsTrustedDownload(Uri url, Uri feed)
    {
        if (feed == LatestRelease)
        {
            return url.Scheme == Uri.UriSchemeHttps && url.AbsoluteUri.StartsWith(DownloadPrefix, StringComparison.Ordinal);
        }

        return url.Scheme == feed.Scheme && string.Equals(url.Host, feed.Host, StringComparison.OrdinalIgnoreCase) && url.Port == feed.Port;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? Sha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
