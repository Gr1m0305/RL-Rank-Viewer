using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RLOverlay.Core.Scenarios;
using RLOverlay.Core.StatsApi;

namespace RLOverlay.App.Services;

/// <summary>
/// Plays a scenario in-process (for <c>--demo</c> and the settings Preview): builds the same Stats API envelopes the
/// game would send and pushes them through the real parser on the UI thread.
/// </summary>
internal sealed class ScenarioFeed : IDisposable
{
    private readonly IReadOnlyList<TimedMessage> _timeline;
    private readonly Action<StatsMessage> _sink;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private int _next;

    public ScenarioFeed(Scenario scenario, Action<StatsMessage> sink, ILogger logger)
    {
        _timeline = ScenarioPlayer.BuildTimeline(scenario);
        _sink = sink;
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
        _timer.Tick += (_, _) => Pump();
    }

    public bool IsFinished => _next >= _timeline.Count;

    /// <summary>Loads a scenario JSON embedded in the app (e.g. <c>demo_lobby.json</c>).</summary>
    public static Scenario LoadEmbedded(string fileName)
    {
        var assembly = typeof(ScenarioFeed).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new FileNotFoundException($"Embedded scenario {fileName} not found");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return Scenario.Parse(reader.ReadToEnd());
    }

    public void Start()
    {
        _next = 0;
        _clock.Restart();
        _timer.Start();
        Pump();
    }

    public void Stop()
    {
        _timer.Stop();
        _clock.Stop();
    }

    private void Pump()
    {
        var now = _clock.ElapsedMilliseconds;
        while (_next < _timeline.Count && _timeline[_next].AtMs <= now)
        {
            var json = _timeline[_next++].Json;
            if (StatsMessageParser.TryParse(json, out var error) is { } message)
            {
                _sink(message);
            }
            else
            {
                _logger.LogWarning("Scenario message did not parse: {Error}", error);
            }
        }

        if (IsFinished)
        {
            _timer.Stop();
        }
    }

    public void Dispose() => Stop();
}
