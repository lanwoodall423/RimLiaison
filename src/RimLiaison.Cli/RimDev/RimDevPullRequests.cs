using System.Text.Json;
using System.Globalization;

namespace RimLiaison.RimDev;

public sealed class SystemRimDevPullRequestProvider : IRimDevPullRequestProvider
{
    private readonly IRimDevProcessRunner processRunner;

    public SystemRimDevPullRequestProvider(IRimDevProcessRunner? processRunner = null)
    {
        this.processRunner = processRunner ?? new SystemRimDevProcessRunner();
    }

    public async Task<RimDevPullRequestQueryResult> FindAsync(
        string repositoryPath,
        string branch,
        CancellationToken cancellationToken = default)
    {
        RimDevProcessResult result = await processRunner.RunAsync(
                repositoryPath,
                "gh",
                [
                    "pr", "list", "--state", "open", "--head", branch, "--limit", "20",
                    "--json", "number,title,headRefName,baseRefName,headRefOid,baseRefOid,isDraft,mergeable,statusCheckRollup,url"
                ],
                TimeSpan.FromSeconds(20),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new(
                false,
                [],
                "GITHUB_PR_QUERY_UNAVAILABLE",
                Bound(result.StartError ?? result.Stderr));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new(false, [], "GITHUB_PR_RESPONSE_INVALID", "GitHub CLI returned a non-array pull-request result.");
            }

            var pullRequests = new List<RimDevPullRequest>();
            foreach (JsonElement value in document.RootElement.EnumerateArray())
            {
                if (!TryGetInt(value, "number", out int number) ||
                    !TryGetString(value, "headRefName", out string? head) ||
                    !TryGetString(value, "baseRefName", out string? baseBranch))
                {
                    return new(false, [], "GITHUB_PR_RESPONSE_INVALID", "A pull-request candidate omitted its branch identity.");
                }

                string title = GetString(value, "title") ?? "(untitled)";
                string? headSha = GetString(value, "headRefOid");
                string? baseSha = GetString(value, "baseRefOid");
                bool draft = GetBoolean(value, "isDraft");
                string? mergeable = GetString(value, "mergeable");
                string? url = GetString(value, "url");
                string[] checks = ReadCheckStates(value);
                pullRequests.Add(new(
                    number,
                    title,
                    head!,
                    baseBranch!,
                    headSha,
                    baseSha,
                    draft,
                    mergeable,
                    checks,
                    url));
            }

            return new(true, pullRequests);
        }
        catch (JsonException exception)
        {
            return new(false, [], "GITHUB_PR_RESPONSE_INVALID", Bound(exception.Message));
        }
    }

    public Task<RimDevProcessResult> MergeAsync(
        string repositoryPath,
        RimDevPullRequest pullRequest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pullRequest.HeadSha))
        {
            return Task.FromResult(new RimDevProcessResult(
                null,
                string.Empty,
                string.Empty,
                StartError: "The pull request head identity is required before merging."));
        }

        var arguments = new List<string>
        {
            "pr",
            "merge",
            pullRequest.Number.ToString(CultureInfo.InvariantCulture),
            "--merge",
            "--match-head-commit",
            pullRequest.HeadSha
        };
        return processRunner.RunAsync(
            repositoryPath,
            "gh",
            arguments,
            TimeSpan.FromMinutes(2),
            cancellationToken);
    }

    private static string[] ReadCheckStates(JsonElement value)
    {
        if (!value.TryGetProperty("statusCheckRollup", out JsonElement checks) ||
            checks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var states = new List<string>();
        foreach (JsonElement check in checks.EnumerateArray())
        {
            string? state = GetString(check, "conclusion") ??
                GetString(check, "state") ??
                GetString(check, "status");
            if (!string.IsNullOrWhiteSpace(state))
            {
                states.Add(state.ToUpperInvariant());
            }
        }

        return states.ToArray();
    }

    private static bool TryGetInt(JsonElement value, string property, out int result)
    {
        result = 0;
        return value.TryGetProperty(property, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out result);
    }

    private static bool TryGetString(JsonElement value, string property, out string? result)
    {
        result = GetString(value, property);
        return !string.IsNullOrWhiteSpace(result);
    }

    private static string? GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool GetBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out JsonElement element) &&
        element.ValueKind == JsonValueKind.True;

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 2048 ? trimmed : trimmed[..2048];
    }
}
