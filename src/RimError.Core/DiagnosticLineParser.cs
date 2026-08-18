using System.Globalization;
using System.Text.RegularExpressions;

namespace RimError.Core;

internal sealed class ParsedDiagnosticLine
{
    public required string Raw { get; init; }

    public required string Content { get; init; }

    public required string Message { get; init; }

    public string? ExceptionType { get; init; }

    public DiagnosticSeverity Severity { get; init; }

    public string? Category { get; init; }

    public string? StackFrame { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public bool IsBlank { get; init; }

    public bool IsStackFrame => StackFrame is not null;

    public bool IsIndented { get; init; }

    public bool IsContinuationMarker { get; init; }

    public bool IsStrongStart { get; init; }

    public bool IsTruncated { get; init; }
}

internal static class DiagnosticLineParser
{
    private static readonly Regex TimestampPrefixPattern = new(
        @"^\s*(?:\[(?<timestamp>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?)\]|(?<timestamp>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?))\s*(?:[-|]\s*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExceptionPattern = new(
        @"(?<type>(?:[A-Za-z_][\w]*\.)*[A-Za-z_][\w]*(?:Exception|Error))\s*:\s*(?<detail>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExceptionOnlyPattern = new(
        @"^(?<type>(?:[A-Za-z_][\w]*\.)*[A-Za-z_][\w]*(?:Exception|Error))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SeverityPattern = new(
        @"^(?:\[(?<level>TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL)\]|(?<level>TRACE|DEBUG|INFO|WARN(?:ING)?|ERROR|FATAL))\b\s*(?:(?:[:\-])\s*)?(?<detail>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ParsedDiagnosticLine Parse(
        BoundedLine line,
        bool isFirstLine)
    {
        var raw = isFirstLine ? line.Text.TrimStart('\uFEFF') : line.Text;
        var content = RemoveTimestamp(raw, out var timestamp);
        var trimmed = content.Trim();

        if (trimmed.Length == 0)
        {
            return new ParsedDiagnosticLine
            {
                Raw = raw,
                Content = content,
                Message = string.Empty,
                IsBlank = true,
                Timestamp = timestamp,
                IsTruncated = line.WasTruncated
            };
        }

        var severityFound = TryParseSeverity(trimmed, out var severity, out var severityMessage);
        var exceptionText = severityFound ? severityMessage : trimmed;
        var exceptionFound = TryParseException(
            exceptionText,
            out var exceptionType,
            out var exceptionMessage);
        var isIndented = content.Length != content.TrimStart().Length;
        var continuationMarker = IsContinuationMarker(trimmed);

        if (trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedDiagnosticLine
            {
                Raw = raw,
                Content = content,
                Message = trimmed,
                StackFrame = trimmed,
                Timestamp = timestamp,
                IsIndented = isIndented,
                IsContinuationMarker = true,
                IsTruncated = line.WasTruncated
            };
        }

        var message = exceptionFound
            ? exceptionMessage
            : severityFound
                ? severityMessage
                : trimmed;

        return new ParsedDiagnosticLine
        {
            Raw = raw,
            Content = content,
            Message = message,
            ExceptionType = exceptionType,
            Severity = exceptionFound
                ? (severityFound ? severity : DiagnosticSeverity.Error)
                : severity,
            Category = exceptionFound
                ? "Exception"
                : severityFound ? SeverityCategory(severity) : null,
            Timestamp = timestamp,
            IsIndented = isIndented,
            IsContinuationMarker = continuationMarker,
            IsStrongStart = exceptionFound || severityFound,
            IsTruncated = line.WasTruncated
        };
    }

    private static string RemoveTimestamp(string raw, out DateTimeOffset? timestamp)
    {
        var match = TimestampPrefixPattern.Match(raw);
        if (!match.Success ||
            !DateTimeOffset.TryParse(
                match.Groups["timestamp"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            timestamp = null;
            return raw;
        }

        timestamp = parsed;
        return raw[match.Length..];
    }

    private static bool TryParseException(
        string text,
        out string? exceptionType,
        out string message)
    {
        var match = ExceptionPattern.Match(text);
        if (!match.Success)
        {
            match = ExceptionOnlyPattern.Match(text);
            if (!match.Success)
            {
                exceptionType = null;
                message = text;
                return false;
            }

            exceptionType = match.Groups["type"].Value;
            message = exceptionType;
            return true;
        }

        exceptionType = match.Groups["type"].Value;
        var prefix = text[..match.Index].Trim().TrimEnd(':', '-', ' ').Trim();
        var detail = match.Groups["detail"].Value.Trim();
        message = prefix.Length == 0 ? detail : $"{prefix}: {detail}";
        return true;
    }

    private static bool TryParseSeverity(
        string text,
        out DiagnosticSeverity severity,
        out string message)
    {
        var match = SeverityPattern.Match(text);
        if (!match.Success)
        {
            severity = DiagnosticSeverity.Unknown;
            message = text;
            return false;
        }

        severity = match.Groups["level"].Value.ToUpperInvariant() switch
        {
            "TRACE" => DiagnosticSeverity.Trace,
            "DEBUG" => DiagnosticSeverity.Debug,
            "INFO" => DiagnosticSeverity.Info,
            "WARN" or "WARNING" => DiagnosticSeverity.Warning,
            "FATAL" => DiagnosticSeverity.Fatal,
            _ => DiagnosticSeverity.Error
        };
        message = match.Groups["detail"].Value.Trim();
        return true;
    }

    private static bool IsContinuationMarker(string text) =>
        text.StartsWith("[Ref", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("InnerException", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("--->", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("---", StringComparison.OrdinalIgnoreCase);

    private static string? SeverityCategory(DiagnosticSeverity severity) =>
        severity == DiagnosticSeverity.Unknown ? null : severity.ToString();
}
