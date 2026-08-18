using System.Text;

namespace RimError.Core;

internal sealed class BoundedLineReader
{
    private const int BufferSize = 4_096;

    private readonly TextReader _reader;
    private readonly int _maxLineLength;
    private readonly char[] _buffer = new char[BufferSize];
    private int _bufferOffset;
    private int _bufferCount;
    private bool _skipLfAfterCr;

    public BoundedLineReader(TextReader reader, int maxLineLength)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _maxLineLength = maxLineLength;
    }

    public async ValueTask<BoundedLine?> ReadAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(_maxLineLength, 256));
        var truncated = false;
        var sawCharacter = false;

        while (true)
        {
            var character = await ReadCharacterAsync(cancellationToken).ConfigureAwait(false);
            if (character is null)
            {
                return sawCharacter
                    ? CreateLine(builder, truncated, hasTerminator: false)
                    : null;
            }

            var value = character.Value;
            if (_skipLfAfterCr)
            {
                _skipLfAfterCr = false;
                if (value == '\n')
                {
                    continue;
                }
            }

            if (value == '\r' || value == '\n')
            {
                _skipLfAfterCr = value == '\r';
                return CreateLine(builder, truncated, hasTerminator: true);
            }

            sawCharacter = true;
            if (builder.Length < _maxLineLength)
            {
                builder.Append(value);
            }
            else
            {
                truncated = true;
            }
        }
    }

    private async ValueTask<char?> ReadCharacterAsync(CancellationToken cancellationToken)
    {
        if (_bufferOffset >= _bufferCount)
        {
            _bufferCount = await _reader.ReadAsync(
                _buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            _bufferOffset = 0;

            if (_bufferCount == 0)
            {
                return null;
            }
        }

        return _buffer[_bufferOffset++];
    }

    private static BoundedLine CreateLine(
        StringBuilder builder,
        bool truncated,
        bool hasTerminator) =>
        new(
            builder.ToString(),
            truncated,
            hasTerminator,
            Encoding.UTF8.GetByteCount(builder.ToString()) + (hasTerminator ? 1 : 0));
}

internal readonly record struct BoundedLine(
    string Text,
    bool WasTruncated,
    bool HasTerminator,
    int EstimatedUtf8Bytes);
