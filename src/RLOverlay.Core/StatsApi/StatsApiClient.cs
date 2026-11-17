using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RLOverlay.Core.StatsApi;

public enum StatsConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed record StatsApiClientOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 49123;
    public TimeSpan MinReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Connects to the Rocket League Stats API TCP socket (the game listens; we connect), splits the stream into JSON
/// messages and raises <see cref="MessageReceived"/> for each parsed message. Reconnects forever with a capped backoff,
/// so it can be started before the game. Read-only: never writes to the socket.
/// Events are raised on a background thread.
/// </summary>
public sealed class StatsApiClient
{
    private readonly StatsApiClientOptions _options;
    private readonly ILogger _logger;
    private StatsConnectionState _state = StatsConnectionState.Disconnected;

    public StatsApiClient(StatsApiClientOptions options, ILogger<StatsApiClient>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<StatsApiClient>.Instance;
    }

    public event Action<StatsMessage>? MessageReceived;

    public event Action<StatsConnectionState>? ConnectionStateChanged;

    public StatsConnectionState State => _state;

    public int Port => _options.Port;

    /// <summary>Total messages that could not be parsed (diagnostics).</summary>
    public long ParseErrors { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = _options.MinReconnectDelay;
        var loggedRefused = false;
        while (!ct.IsCancellationRequested)
        {
            SetState(StatsConnectionState.Connecting);
            using var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                // Expected while the game is not running or the Stats API is disabled.
                if (!loggedRefused)
                {
                    _logger.LogInformation(
                        "Stats API not reachable on {Host}:{Port} ({Error}); retrying quietly",
                        _options.Host, _options.Port, ex.SocketErrorCode);
                    loggedRefused = true;
                }

                SetState(StatsConnectionState.Disconnected);
                await DelayAsync(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _options.MaxReconnectDelay.TotalMilliseconds));
                continue;
            }

            loggedRefused = false;
            delay = _options.MinReconnectDelay;
            _logger.LogInformation("Connected to Stats API on {Host}:{Port}", _options.Host, _options.Port);
            SetState(StatsConnectionState.Connected);
            try
            {
                await ReadLoopAsync(tcp.GetStream(), ct).ConfigureAwait(false);
                _logger.LogInformation("Stats API closed the connection");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _logger.LogInformation("Stats API connection lost: {Error}", ex.Message);
            }

            SetState(StatsConnectionState.Disconnected);
            await DelayAsync(delay, ct).ConfigureAwait(false);
        }

        SetState(StatsConnectionState.Disconnected);
    }

    /// <summary>Reads until the stream ends. Exposed for tests (any stream works).</summary>
    internal async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var splitter = new JsonStreamSplitter();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            foreach (var raw in splitter.Feed(buffer.AsSpan(0, read)))
            {
                var message = StatsMessageParser.TryParse(raw, out var error);
                if (message is null)
                {
                    ParseErrors++;
                    _logger.LogDebug("Skipping unparseable Stats API message: {Error}", error);
                    continue;
                }

                try
                {
                    MessageReceived?.Invoke(message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stats API message handler failed for {Event}", message.EventName);
                }
            }
        }
    }

    private void SetState(StatsConnectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        ConnectionStateChanged?.Invoke(state);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
