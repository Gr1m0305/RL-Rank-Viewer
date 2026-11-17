using System.Text.Json;

namespace RLOverlay.Core.StatsApi;

/// <summary>
/// Splits the Stats API TCP byte stream into individual JSON messages.
/// The game writes concatenated JSON objects with no delimiter or length prefix, so this finds the end of each
/// top-level object with <see cref="Utf8JsonReader"/> (string-aware, unlike brace counting). Incomplete objects
/// wait for more bytes. Garbage between objects is skipped up to the next <c>{</c>.
/// </summary>
public sealed class JsonStreamSplitter
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128,
    };

    private byte[] _buffer;
    private int _start;
    private int _count;

    public JsonStreamSplitter(int maxMessageBytes = 8 * 1024 * 1024)
    {
        MaxMessageBytes = maxMessageBytes;
        _buffer = new byte[16 * 1024];
    }

    /// <summary>A single message larger than this is treated as a corrupt stream and dropped.</summary>
    public int MaxMessageBytes { get; }

    /// <summary>Number of bytes skipped because they were not valid JSON (for diagnostics).</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Bytes currently buffered (an incomplete message).</summary>
    public int BufferedBytes => _count;

    /// <summary>Appends bytes and returns every complete message now available.</summary>
    public List<byte[]> Feed(ReadOnlySpan<byte> data)
    {
        Append(data);
        var messages = new List<byte[]>();
        while (TryReadNext(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_count + data.Length);
        data.CopyTo(_buffer.AsSpan(_start + _count));
        _count += data.Length;
    }

    /// <summary>Removes and returns the next complete JSON object, if one is buffered.</summary>
    public bool TryReadNext(out byte[] message)
    {
        message = [];
        while (true)
        {
            SkipToObjectStart();
            if (_count == 0)
            {
                return false;
            }

            var span = _buffer.AsSpan(_start, _count);
            var reader = new Utf8JsonReader(span, isFinalBlock: false, new JsonReaderState(ReaderOptions));
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                    {
                        var length = (int)reader.BytesConsumed;
                        message = span[..length].ToArray();
                        Consume(length);
                        return true;
                    }
                }

                // Ran out of bytes inside the object: wait for more data, unless it is impossibly large,
                // in which case the stream is out of sync: drop this object's opening brace and resync.
                if (_count > MaxMessageBytes)
                {
                    DiscardedBytes += 1;
                    Consume(1);
                    continue;
                }

                return false;
            }
            catch (JsonException)
            {
                // Malformed object: drop the opening brace and resync on the next one.
                DiscardedBytes += 1;
                Consume(1);
            }
        }
    }

    private void SkipToObjectStart()
    {
        var span = _buffer.AsSpan(_start, _count);
        var index = span.IndexOf((byte)'{');
        if (index < 0)
        {
            // Keep nothing: whitespace or garbage only.
            DiscardedBytes += CountNonWhitespace(span);
            _start = 0;
            _count = 0;
            return;
        }

        if (index > 0)
        {
            DiscardedBytes += CountNonWhitespace(span[..index]);
            Consume(index);
        }
    }

    private static int CountNonWhitespace(ReadOnlySpan<byte> span)
    {
        var n = 0;
        foreach (var b in span)
        {
            if (b is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0))
            {
                n++;
            }
        }

        return n;
    }

    private void Consume(int length)
    {
        _start += length;
        _count -= length;
        if (_count == 0)
        {
            _start = 0;
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (_start + needed <= _buffer.Length)
        {
            return;
        }

        if (needed <= _buffer.Length)
        {
            // Compact in place.
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
            _start = 0;
            return;
        }

        var size = _buffer.Length;
        while (size < needed)
        {
            size *= 2;
        }

        var bigger = new byte[size];
        Buffer.BlockCopy(_buffer, _start, bigger, 0, _count);
        _buffer = bigger;
        _start = 0;
    }
}
