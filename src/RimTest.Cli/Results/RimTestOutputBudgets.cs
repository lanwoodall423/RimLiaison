using System.Text;

namespace RimTest.Results;

/// <summary>
/// Normal-output budgets for representative agent-loop responses.
/// These are intentionally small and are protected by output-contract tests.
/// Suite failures may grow with the number of failures; successful suites do not.
/// </summary>
public static class RimTestOutputBudgets
{
    public const int SingleTestPassMaxBytes = 256;
    public const int SingleTestFailureMaxBytes = 768;
    public const int SuitePassMaxBytes = 256;
    public const int AffectedSelectionMaxBytes = 384;
    public const int AffectedSuitePassMaxBytes = 2048;

    public static int Utf8Bytes(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Encoding.UTF8.GetByteCount(json);
    }
}
