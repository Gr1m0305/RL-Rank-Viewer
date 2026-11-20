using System.Globalization;
using System.Text;

namespace RLOverlay.Core.Config;

/// <summary>The Stats API values found in one ini file (null = key not present).</summary>
public sealed record StatsApiIniValues(double? PacketSendRate, int? Port, int? WebPort, bool HasSection)
{
    /// <summary>The TCP port the game listens on (the official default when the key is missing).</summary>
    public int EffectivePort => Port ?? StatsApiIni.DefaultPort;

    /// <summary>The game sends data (rate &gt; 0) and listens on TCP (port &gt; 0). A missing rate means 0 = off.</summary>
    public bool IsEnabled => PacketSendRate is > 0 && EffectivePort > 0;
}

/// <summary>What happened to one ini file when the Stats API was enabled.</summary>
public sealed record StatsApiIniEditResult(string Path, bool Created, bool Changed, string? BackupPath, IReadOnlyList<string> Changes);

/// <summary>
/// Reads and edits the Stats API section of Rocket League's ini files.
/// Only <c>PacketSendRate</c> and <c>Port</c> in <c>[TAGame.MatchStatsExporter_TA]</c> are ever changed; every other
/// line, comment, line ending and the file's encoding are kept. A backup is written before any change.
/// </summary>
public static class StatsApiIni
{
    public const string Section = "TAGame.MatchStatsExporter_TA";
    public const int DefaultPort = 49123;

    /// <summary>The overlay only needs the roster, so 30 updates a second is plenty (the game allows up to 120).</summary>
    public const int RecommendedRate = 30;

    public static StatsApiIniValues Read(string text)
    {
        double? rate = null;
        int? port = null, webPort = null;
        var hasSection = false;
        var inSection = false;
        foreach (var raw in SplitLines(text, out _))
        {
            var line = raw.Trim();
            if (TrySectionName(line, out var name))
            {
                inSection = string.Equals(name, Section, StringComparison.OrdinalIgnoreCase);
                hasSection |= inSection;
                continue;
            }

            if (!inSection || !TryKeyValue(line, out var key, out var value))
            {
                continue;
            }

            if (key.Equals("PacketSendRate", StringComparison.OrdinalIgnoreCase))
            {
                rate = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                port = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
            }
            else if (key.Equals("WebPort", StringComparison.OrdinalIgnoreCase))
            {
                webPort = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0;
            }
        }

        return new StatsApiIniValues(rate, port, webPort, hasSection);
    }

    /// <summary>
    /// Returns the text with the Stats API turned on. A file that is already enabled is returned unchanged. Otherwise
    /// <c>PacketSendRate=30</c> is set when it is missing, 0 or invalid, and <c>Port=49123</c> when it is missing, 0 or
    /// invalid; a rate or port that is already valid is left alone, and <c>WebPort</c> is never touched. Adds the
    /// section if the file has none.
    /// </summary>
    public static string Enable(string text, out IReadOnlyList<string> changes)
    {
        // Already on (a missing Port means the default 49123): leave the file exactly as it is.
        if (Read(text).IsEnabled)
        {
            changes = [];
            return text;
        }

        var lines = SplitLines(text, out var newline);
        var endsWithNewline = text.EndsWith('\n');
        if (text.Length == 0)
        {
            lines.Clear();
        }
        else if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var log = new List<string>();
        var start = lines.FindIndex(l => TrySectionName(l.Trim(), out var n) && string.Equals(n, Section, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{Section}]");
            lines.Add($"PacketSendRate={RecommendedRate}");
            lines.Add($"Port={DefaultPort}");
            log.Add($"added [{Section}] with PacketSendRate={RecommendedRate} and Port={DefaultPort}");
        }
        else
        {
            var end = lines.FindIndex(start + 1, l => TrySectionName(l.Trim(), out _));
            if (end < 0)
            {
                end = lines.Count;
            }

            // Insert new keys after the section's last non-blank line.
            var insertAt = end;
            while (insertAt > start + 1 && lines[insertAt - 1].Trim().Length == 0)
            {
                insertAt--;
            }

            var inserted = 0;
            void Ensure(string key, string wanted, Func<string, bool> isValid)
            {
                var found = false;
                for (var i = start + 1; i < end + inserted; i++)
                {
                    if (TryKeyValue(lines[i].Trim(), out var k, out var v) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (!isValid(v))
                        {
                            lines[i] = $"{key}={wanted}";
                            log.Add($"{key}: {(v.Length == 0 ? "(empty)" : v)} → {wanted}");
                        }
                    }
                }

                if (!found)
                {
                    lines.Insert(insertAt + inserted, $"{key}={wanted}");
                    inserted++;
                    log.Add($"{key}: added {wanted}");
                }
            }

            Ensure("PacketSendRate", RecommendedRate.ToString(CultureInfo.InvariantCulture),
                v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 && double.IsFinite(r));
            Ensure("Port", DefaultPort.ToString(CultureInfo.InvariantCulture),
                v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535);
        }

        changes = log;
        if (log.Count == 0)
        {
            return text;
        }

        var result = string.Join(newline, lines);
        return endsWithNewline || text.Length == 0 ? result + newline : result;
    }

    /// <summary>
    /// Enables the Stats API in one file. Existing files are backed up first (<c>&lt;name&gt;.bak-yyyyMMdd-HHmmss</c>
    /// next to the original) and only written when something changes. A missing file is created only when
    /// <paramref name="createIfMissing"/> is set. Throws <see cref="UnauthorizedAccessException"/> when the folder
    /// needs admin rights (the caller then relaunches elevated).
    /// </summary>
    public static StatsApiIniEditResult EnableFile(string path, DateTimeOffset now, bool createIfMissing = false)
    {
        if (!File.Exists(path))
        {
            if (!createIfMissing)
            {
                return new StatsApiIniEditResult(path, Created: false, Changed: false, BackupPath: null, []);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fresh = Enable("", out var created);
            File.WriteAllText(path, fresh, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new StatsApiIniEditResult(path, Created: true, Changed: true, BackupPath: null, created);
        }

        var bytes = File.ReadAllBytes(path);
        var (encoding, bomLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        var updated = Enable(text, out var changes);
        if (changes.Count == 0)
        {
            return new StatsApiIniEditResult(path, Created: false, Changed: false, BackupPath: null, changes);
        }

        var backup = UniqueBackupPath(path, now);
        File.Copy(path, backup);

        var output = encoding.GetPreamble().Concat(encoding.GetBytes(updated)).ToArray();
        var temp = path + ".rloverlay-tmp";
        File.WriteAllBytes(temp, output);
        File.Move(temp, path, overwrite: true);
        return new StatsApiIniEditResult(path, Created: false, Changed: true, BackupPath: backup, changes);
    }

    /// <summary>Reads a file's Stats API values; null if it does not exist or cannot be read.</summary>
    public static StatsApiIniValues? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var (encoding, bomLength) = DetectEncoding(bytes);
            return Read(encoding.GetString(bytes, bomLength, bytes.Length - bomLength));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), 2);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    private static string UniqueBackupPath(string path, DateTimeOffset now)
    {
        var stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = $"{path}.bak-{stamp}";
        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = $"{path}.bak-{stamp}-{n}";
        }

        return candidate;
    }

    private static List<string> SplitLines(string text, out string newline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        return text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }

    private static bool TrySectionName(string line, out string name)
    {
        if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
        {
            name = line[1..^1].Trim();
            return true;
        }

        name = "";
        return false;
    }

    private static bool TryKeyValue(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length == 0 || line[0] is ';' or '#')
        {
            return false;
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return true;
    }
}
