using System.Globalization;
using System.Text;

namespace RimLiaison.Observability;

public static class AgentObservabilityTime
{
    private const long MinimumUnixMilliseconds = 1;
    private const long MaximumUnixMilliseconds = 253402300799999;

    public static bool IsValid(long? timestamp) =>
        timestamp is >= MinimumUnixMilliseconds and <= MaximumUnixMilliseconds;

    public static long SortValue(long? timestamp) =>
        IsValid(timestamp) ? timestamp!.Value : long.MinValue;

    public static string FormatLocal(long? timestamp) =>
        IsValid(timestamp)
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp!.Value)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : "—";
}

public static class AgentObservabilityRecordIdentity
{
    public static string ForRecommendation(AgentIssue issue)
    {
        string? stable = FirstStable(issue.Fingerprint, issue.CapabilityId, issue.OperationKey);
        return stable is null
            ? "recommendation|issue|" + issue.Id
            : "recommendation|" + issue.Category + "|" + Normalize(stable);
    }

    public static string ForIssue(
        AgentIssue issue,
        string? structuredFingerprint = null)
    {
        string? stable = FirstStable(
            issue.Fingerprint,
            structuredFingerprint,
            issue.OperationKey);
        return stable is null
            ? "issue|record|" + issue.Id
            : "issue|" + issue.Category + "|" + Normalize(stable);
    }

    private static string? FirstStable(
        string? first,
        string? second,
        string? third = null)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        if (!string.IsNullOrWhiteSpace(second))
        {
            return second;
        }

        return string.IsNullOrWhiteSpace(third) ? null : third;
    }
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingWhitespace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
