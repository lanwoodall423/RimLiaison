using System.Text;

namespace RimError.Core;

internal sealed record RawDiagnosticEvent
{
    public required string Source { get; init; }

    public DiagnosticIngestionMetadata? Metadata { get; init; }

    public required string Message { get; init; }

    public required string Sample { get; init; }

    public string? ExceptionType { get; init; }

    public DiagnosticSeverity Severity { get; init; }

    public string? Category { get; init; }

    public string[]? StackFrames { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public bool IsPartial { get; init; }

    public bool WasTruncated { get; init; }
}

internal sealed class DiagnosticEventFramer
{
    private readonly DiagnosticIngestionOptions _options;
    private readonly string _source;
    private readonly DiagnosticIngestionMetadata? _metadata;
    private DiagnosticEventBuilder? _current;
    private bool _isFirstLine = true;

    public DiagnosticEventFramer(
        string source,
        DiagnosticIngestionMetadata? metadata,
        DiagnosticIngestionOptions options)
    {
        _source = source;
        _metadata = metadata;
        _options = options;
    }

    public RawDiagnosticEvent? Push(BoundedLine line)
    {
        var parsed = DiagnosticLineParser.Parse(line, _isFirstLine);
        _isFirstLine = false;

        if (parsed.IsBlank)
        {
            return null;
        }

        if (parsed.IsStackFrame)
        {
            _current ??= DiagnosticEventBuilder.StartPartial(
                _source,
                _metadata,
                _options,
                parsed);
            _current.Append(parsed);
            return null;
        }

        if (_current is not null &&
            (parsed.IsContinuationMarker || parsed.IsIndented))
        {
            _current.Append(parsed);
            return null;
        }

        var completed = _current?.Build();
        _current = DiagnosticEventBuilder.Start(
            _source,
            _metadata,
            _options,
            parsed);
        return completed;
    }

    public RawDiagnosticEvent? Complete()
    {
        var completed = _current?.Build();
        _current = null;
        return completed;
    }
}

internal sealed class DiagnosticEventBuilder
{
    private readonly DiagnosticIngestionOptions _options;
    private readonly StringBuilder _message;
    private readonly StringBuilder _sample = new();
    private readonly List<string> _stackFrames = [];
    private readonly string _source;
    private readonly DiagnosticIngestionMetadata? _metadata;
    private int _continuationLines;
    private bool _wasTruncated;
    private DateTimeOffset? _timestamp;

    private DiagnosticEventBuilder(
        string source,
        DiagnosticIngestionMetadata? metadata,
        DiagnosticIngestionOptions options,
        string message,
        string sample,
        string? exceptionType,
        DiagnosticSeverity severity,
        string? category,
        DateTimeOffset? timestamp,
        bool isPartial)
    {
        _source = source;
        _metadata = metadata;
        _options = options;
        _message = new StringBuilder(TrimToLength(message, options.MaxMessageLength));
        _sample.Append(TrimToLength(sample, options.MaxRawSampleLength));
        ExceptionType = exceptionType;
        Severity = severity;
        Category = category;
        _timestamp = timestamp;
        IsPartial = isPartial;
    }

    public string? ExceptionType { get; private set; }

    public DiagnosticSeverity Severity { get; private set; }

    public string? Category { get; private set; }

    public bool IsPartial { get; }

    public static DiagnosticEventBuilder Start(
        string source,
        DiagnosticIngestionMetadata? metadata,
        DiagnosticIngestionOptions options,
        ParsedDiagnosticLine line) =>
        new(
            source,
            metadata,
            options,
            line.Message,
            line.Raw,
            line.ExceptionType,
            line.Severity,
            line.Category,
            line.Timestamp,
            isPartial: false)
        {
            _wasTruncated = line.IsTruncated
        };

    public static DiagnosticEventBuilder StartPartial(
        string source,
        DiagnosticIngestionMetadata? metadata,
        DiagnosticIngestionOptions options,
        ParsedDiagnosticLine line) =>
        new(
            source,
            metadata,
            options,
            "Unattributed stack trace",
            string.Empty,
            null,
            DiagnosticSeverity.Error,
            "StackTrace",
            line.Timestamp,
            isPartial: true)
        {
            _wasTruncated = line.IsTruncated
        };

    public void Append(ParsedDiagnosticLine line)
    {
        _wasTruncated |= line.IsTruncated;
        _timestamp ??= line.Timestamp;
        AppendSample(line.Raw);

        if (line.IsStackFrame)
        {
            if (_stackFrames.Count < _options.MaxStackDepth && line.StackFrame is not null)
            {
                _stackFrames.Add(TrimToLength(line.StackFrame, _options.MaxFrameLength));
            }

            return;
        }

        if (_continuationLines >= _options.MaxContinuationLines)
        {
            return;
        }

        _continuationLines++;
        if (line.Message.Length == 0 ||
            line.IsContinuationMarker && line.ExceptionType is null)
        {
            return;
        }

        AppendMessage(line.Message);
        if (ExceptionType is null && line.ExceptionType is not null)
        {
            ExceptionType = line.ExceptionType;
        }
    }

    public RawDiagnosticEvent Build() => new()
    {
        Source = _source,
        Metadata = _metadata,
        Message = _message.ToString(),
        Sample = _sample.ToString(),
        ExceptionType = ExceptionType,
        Severity = Severity,
        Category = Category,
        StackFrames = _stackFrames.Count == 0 ? null : _stackFrames.ToArray(),
        Timestamp = _timestamp,
        IsPartial = IsPartial,
        WasTruncated = _wasTruncated
    };

    private void AppendMessage(string value)
    {
        var remaining = _options.MaxMessageLength - _message.Length;
        if (remaining <= 0)
        {
            return;
        }

        if (_message.Length > 0)
        {
            _message.Append('\n');
            remaining--;
        }

        if (remaining > 0)
        {
            _message.Append(TrimToLength(value, remaining));
        }
    }

    private void AppendSample(string value)
    {
        var remaining = _options.MaxRawSampleLength - _sample.Length;
        if (remaining <= 0)
        {
            return;
        }

        if (_sample.Length > 0)
        {
            _sample.Append('\n');
            remaining--;
        }

        if (remaining > 0)
        {
            _sample.Append(TrimToLength(value, remaining));
        }
    }

    private static string TrimToLength(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
