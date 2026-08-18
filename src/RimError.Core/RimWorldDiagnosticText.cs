using System.Text;
using System.Text.RegularExpressions;

namespace RimError.Core;

internal static class RimWorldDiagnosticText
{
    private static readonly Regex FailurePattern = new(
        @"\b(?:error|failed|failure|exception|could not|cannot|unable|missing|not found|invalid|undefined|malformed|unresolved|mismatch|threw|throw)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DefPattern = new(
        @"\b(?<type>[A-Za-z_][\w.]*(?:Def|DefOf))\s+(?:named|called|name(?:d)?)\s*[:=]?\s*['""]?(?<name>[A-Za-z_][\w.\-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DefNamePattern = new(
        @"\bDefName\s*[:=]\s*['""]?(?<name>[A-Za-z_][\w.\-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DuplicateDefPattern = new(
        @"\bduplicate\s+(?:def\s*name|def)\s*[:=]?\s*(?:(?<type>[A-Za-z_][\w.]*Def)[.:])?['""]?(?<name>[A-Za-z_][\w.\-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MissingMemberPattern = new(
        @"(?:method|field|member)\s+(?:not found|missing)\s*[:=]?\s*['""]?(?<member>[A-Za-z_][\w.<>+`:-]*(?:\([^)]*\))?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MemberNotFoundPattern = new(
        @"(?:method|field|member)\s+['""]?(?<member>[A-Za-z_][\w.<>+`:-]*(?:\([^)]*\))?)['""]?\s+(?:was\s+)?not found",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedValuePattern = new(
        "['\"](?<value>[^'\"]{1,240})['\"]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnquotedTargetPattern = new(
        @"(?:target\s+(?:method|type)|patch\s+target|patching\s+(?:method|type))\s*[:=]?\s*(?<value>[A-Za-z_][\w.`+<>]*(?:(?:::|\.)[A-Za-z_][\w`+<>]*)?(?:\([^)]*\))?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MethodNotFoundPattern = new(
        @"(?:could not find|missing)\s+method\s+(?<value>[A-Za-z_][\w.`+<>]*(?:(?:::|\.)[A-Za-z_][\w`+<>]*)?(?:\([^)]*\))?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NamedTypePattern = new(
        @"\b(?:type|class|initializer\s+for)\s*['""](?<type>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssetTokenPattern = new(
        @"\b(?:texture|shader|resource|asset|assetbundle)(?:\s+(?:at|named|name|path|from))?\s*[:=]?\s*(?<asset>(?:[A-Za-z]:[\\/]|[A-Za-z0-9_./\\-])+\.(?:png|jpg|jpeg|tga|shader|mat|assetbundle|prefab|asset))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PackagePattern = new(
        @"\bpackage\s*id\s*[:=]?\s*['""]?(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)['""]?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CodePattern = new(
        @"\b(?<code>(?:CS|MSB|NU)\d{4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AssemblyPattern = new(
        @"\b(?:file\s+or\s+)?assembly\s*['""](?<assembly>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DependencyPattern = new(
        @"\bdependency\s*(?:[:=]|['""])?\s*(?<dependency>[A-Za-z0-9._-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Combine(RawDiagnosticEvent diagnostic)
    {
        var builder = new StringBuilder(
            diagnostic.Message.Length + diagnostic.Sample.Length + 64);
        builder.Append(diagnostic.Message);
        builder.Append('\n');
        builder.Append(diagnostic.Sample);

        if (diagnostic.StackFrames is not null)
        {
            foreach (var frame in diagnostic.StackFrames)
            {
                builder.Append('\n');
                builder.Append(frame);
            }
        }

        return builder.ToString();
    }

    public static bool HasFailure(string text) => FailurePattern.IsMatch(text);

    public static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    public static bool ContainsWord(string text, string value) =>
        Regex.IsMatch(
            text,
            $@"\b{Regex.Escape(value)}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ContainsAll(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (!Contains(text, value))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ContainsAny(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (Contains(text, value))
            {
                return true;
            }
        }

        return false;
    }

    public static string SimpleName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lastSeparator = value.LastIndexOf('.');
        return DiagnosticNormalizer.NormalizeStableValue(
            lastSeparator >= 0 ? value[(lastSeparator + 1)..] : value);
    }

    public static (string? DefType, string? DefName) ExtractDef(string text)
    {
        var duplicate = DuplicateDefPattern.Match(text);
        if (duplicate.Success)
        {
            return (
                duplicate.Groups["type"].Success
                    ? CanonicalDefType(duplicate.Groups["type"].Value)
                    : null,
                DiagnosticNormalizer.NormalizeStableValue(duplicate.Groups["name"].Value));
        }

        var match = DefPattern.Match(text);
        if (match.Success)
        {
            return (
                CanonicalDefType(match.Groups["type"].Value),
                DiagnosticNormalizer.NormalizeStableValue(match.Groups["name"].Value));
        }

        match = DefNamePattern.Match(text);
        return match.Success
            ? (null, DiagnosticNormalizer.NormalizeStableValue(match.Groups["name"].Value))
            : (null, null);
    }

    public static string? ExtractMissingMember(string text)
    {
        var match = MissingMemberPattern.Match(text);
        if (!match.Success)
        {
            match = MemberNotFoundPattern.Match(text);
        }

        return match.Success
            ? DiagnosticNormalizer.NormalizeStableValue(match.Groups["member"].Value)
            : null;
    }

    public static (string? TargetType, string? TargetMethod) ExtractTarget(string text)
    {
        foreach (Match match in QuotedValuePattern.Matches(text))
        {
            var prefixStart = Math.Max(0, match.Index - 120);
            var prefix = text[prefixStart..match.Index];
            if (ContainsAny(prefix, "target", "patch", "method", "type"))
            {
                var target = ParseTarget(match.Groups["value"].Value);
                if (target.TargetType is not null || target.TargetMethod is not null)
                {
                    return target;
                }
            }
        }

        var unquoted = UnquotedTargetPattern.Match(text);
        if (!unquoted.Success)
        {
            unquoted = MethodNotFoundPattern.Match(text);
        }

        return unquoted.Success
            ? ParseTarget(unquoted.Groups["value"].Value)
            : (null, null);
    }

    public static (string? TargetType, string? TargetMethod) ParseTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var target = value.Trim().Trim('"', '\'', '`');
        var argumentIndex = target.IndexOf('(');
        if (argumentIndex >= 0)
        {
            target = target[..argumentIndex];
        }

        var lastSpace = target.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            target = target[(lastSpace + 1)..];
        }

        var doubleColon = target.LastIndexOf("::", StringComparison.Ordinal);
        if (doubleColon > 0 && doubleColon < target.Length - 2)
        {
            return (
                DiagnosticNormalizer.NormalizeStableValue(target[..doubleColon]),
                DiagnosticNormalizer.NormalizeStableValue(target[(doubleColon + 2)..]));
        }

        var dot = target.LastIndexOf('.');
        if (dot > 0 && dot < target.Length - 1)
        {
            return (
                DiagnosticNormalizer.NormalizeStableValue(target[..dot]),
                DiagnosticNormalizer.NormalizeStableValue(target[(dot + 1)..]));
        }

        return (DiagnosticNormalizer.NormalizeStableValue(target), null);
    }

    public static string? ExtractNamedType(string text)
    {
        var match = NamedTypePattern.Match(text);
        return match.Success
            ? DiagnosticNormalizer.NormalizeStableValue(match.Groups["type"].Value)
            : null;
    }

    public static string? ExtractAsset(string text)
    {
        foreach (Match match in QuotedValuePattern.Matches(text))
        {
            var prefixStart = Math.Max(0, match.Index - 100);
            var prefix = text[prefixStart..match.Index];
            if (ContainsAny(prefix, "texture", "shader", "resource", "asset", "assetbundle"))
            {
                return DiagnosticNormalizer.NormalizeStableValue(match.Groups["value"].Value);
            }
        }

        var token = AssetTokenPattern.Match(text);
        return token.Success
            ? DiagnosticNormalizer.NormalizeStableValue(token.Groups["asset"].Value)
            : null;
    }

    public static string? ExtractPackageId(string text)
    {
        var match = PackagePattern.Match(text);
        return match.Success
            ? DiagnosticNormalizer.NormalizeStableValue(match.Groups["id"].Value)
            : null;
    }

    public static string? ExtractCode(string text)
    {
        var match = CodePattern.Match(text);
        return match.Success ? match.Groups["code"].Value.ToUpperInvariant() : null;
    }

    public static string? ExtractAssembly(string text)
    {
        var match = AssemblyPattern.Match(text);
        return match.Success
            ? DiagnosticNormalizer.NormalizeStableValue(match.Groups["assembly"].Value)
            : null;
    }

    public static string? ExtractDependency(string text)
    {
        var match = DependencyPattern.Match(text);
        return match.Success
            ? DiagnosticNormalizer.NormalizeStableValue(match.Groups["dependency"].Value)
            : null;
    }

    private static string CanonicalDefType(string value)
    {
        var normalized = DiagnosticNormalizer.NormalizeStableValue(value);
        var lastSeparator = normalized.LastIndexOf('.');
        return lastSeparator >= 0 ? normalized[(lastSeparator + 1)..] : normalized;
    }
}
