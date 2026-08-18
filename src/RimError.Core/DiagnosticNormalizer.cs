using System.Text;
using System.Text.RegularExpressions;

namespace RimError.Core;

public static class DiagnosticNormalizer
{
    private static readonly Regex TimestampPattern = new(
        @"\b\d{4}[-/]\d{2}[-/]\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,7})?(?:Z|[+-]\d{2}:?\d{2})?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MemoryAddressPattern = new(
        @"\b0x[0-9a-f]{4,}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ThreadIdPattern = new(
        @"\b(?:thread(?:\s*(?:id|#))?|tid)\s*[\[\(]?\s*[:=#]?\s*\d+(?:\s*[\]\)])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AbsolutePathPattern = new(
        @"(?<![\w])(?:[a-z]:[\\/]|\\\\[^\\/\s]+[\\/]|/(?:users|home|tmp|var|private|mnt|workspace|build)[\\/])(?:[^\\/\s:]+[\\/])*[^\\/\s:]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PathLinePattern = new(
        @"(?<=\{path\}:)\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SourceLinePattern = new(
        @"\bline\s+\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CoordinatePattern = new(
        @"\b(?<label>cell|coord(?:inate)?|position|pos)\s*[:=]\s*\(?-?\d+\s*,\s*-?\d+(?:\s*,\s*-?\d+)?\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex GeneratedObjectPattern = new(
        @"\b(?<prefix>Thing|Pawn|Map|Lord|Job|Faction|WorldObject|ThingWithComps)[_#-]\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex InnerExceptionPattern = new(
        @"(?:--->|inner\s+exception\s*:?|caused\s+by\s*:?)\s*(?<type>(?:[A-Za-z_][\w]*\.)*[A-Za-z_][\w]*(?:Exception|Error))\s*(?::\s*(?<message>[^\r\n]*))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DefIdentityPattern = new(
        @"\b(?<type>[A-Za-z_][\w.]*(?:Def|DefOf))\s+(?:named|called|name(?:d)?)\s*[:=]?\s*['""]?(?<name>[A-Za-z_][\w.\-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DefNamePattern = new(
        @"\bDefName\s*[:=]\s*['""]?(?<name>[A-Za-z_][\w.\-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedMemberPattern = new(
        "['\"](?<member>[^'\"]+)['\"]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var normalized = message.Trim();
        normalized = TimestampPattern.Replace(normalized, "{time}");
        normalized = GuidPattern.Replace(normalized, "{guid}");
        normalized = MemoryAddressPattern.Replace(normalized, "{addr}");
        normalized = ThreadIdPattern.Replace(normalized, "thread={id}");
        normalized = NormalizeStackPath(normalized);
        normalized = AbsolutePathPattern.Replace(normalized, "{path}");
        normalized = PathLinePattern.Replace(normalized, "{line}");
        normalized = SourceLinePattern.Replace(normalized, "line {line}");
        normalized = CoordinatePattern.Replace(
            normalized,
            match =>
            {
                var label = match.Groups["label"].Value;
                return $"{label}={{coord}}";
            });
        normalized = GeneratedObjectPattern.Replace(
            normalized,
            match =>
            {
                var prefix = match.Groups["prefix"].Value;
                return $"{prefix}_{{id}}";
            });

        return CollapseWhitespace(normalized);
    }

    public static string NormalizeStableValue(string? value) =>
        CollapseWhitespace(value);

    public static (string? DefType, string? DefName) ExtractDefIdentity(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (null, null);
        }

        var match = DefIdentityPattern.Match(message);
        if (match.Success)
        {
            return (
                NormalizeStableValue(match.Groups["type"].Value),
                NormalizeStableValue(match.Groups["name"].Value));
        }

        match = DefNamePattern.Match(message);
        return match.Success
            ? (null, NormalizeStableValue(match.Groups["name"].Value))
            : (null, null);
    }

    public static string? ExtractMissingMember(string? message, string? exceptionType)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            string.IsNullOrWhiteSpace(exceptionType) ||
            !exceptionType.Contains("Missing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = QuotedMemberPattern.Match(message);
        return match.Success ? NormalizeStableValue(match.Groups["member"].Value) : null;
    }

    public static (string? Type, string? Message) ExtractInnerException(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (null, null);
        }

        var match = InnerExceptionPattern.Match(message);
        if (!match.Success)
        {
            return (null, null);
        }

        var type = NormalizeStableValue(match.Groups["type"].Value);
        var detail = match.Groups["message"].Success
            ? NormalizeMessage(match.Groups["message"].Value)
            : null;
        return (type.Length == 0 ? null : type, string.IsNullOrWhiteSpace(detail) ? null : detail);
    }

    public static (string? Type, string? Method) ExtractOrigin(string? frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
        {
            return (null, null);
        }

        var origin = frame.Trim();
        if (origin.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
        {
            origin = origin[3..].TrimStart();
        }

        var sourceIndex = origin.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        if (sourceIndex >= 0)
        {
            origin = origin[..sourceIndex];
        }

        var argumentIndex = origin.IndexOf('(');
        if (argumentIndex >= 0)
        {
            origin = origin[..argumentIndex];
        }

        var separator = origin.LastIndexOf('.');
        if (separator <= 0 || separator == origin.Length - 1)
        {
            return (null, NormalizeStableValue(origin));
        }

        return (
            NormalizeStableValue(origin[..separator].TrimEnd('.')),
            NormalizeStableValue(origin[(separator + 1)..]));
    }

    private static string NormalizeStackPath(string value)
    {
        var sourceIndex = value.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        if (sourceIndex < 0)
        {
            return value;
        }

        var path = value[(sourceIndex + 4)..].Trim();
        if (!IsAbsolutePath(path))
        {
            return value;
        }

        path = Regex.Replace(
            path,
            @":\s*(?:line\s*)?\d+\s*$",
            string.Empty,
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return $"{value[..(sourceIndex + 4)]}{{path}}";
    }

    private static bool IsAbsolutePath(string value) =>
        value.Length >= 2 &&
        ((char.IsLetter(value[0]) && value[1] == ':') ||
         value.StartsWith("/", StringComparison.Ordinal) ||
         value.StartsWith("\\\\", StringComparison.Ordinal));

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
