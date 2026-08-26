using System.Text.Json;
using RimLiaison.Observability;
using RimLiaison.Validation;

namespace RimLiaison.Tests;

internal static class ValidationPolicyTests
{
    public static void RequiredFailureBlocksCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TASK_REQUIREMENT,
                ValidationCheckState.FAILED,
                ValidationFindingKind.MOD_DEFECT)
        ]);

        AssertEqual(ValidationPolicySchema.Fail, result.Status);
        Assert(!result.PermitsProduction, "A required failure must block production.");
    }

    public static void RequiredSuccessPermitsCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.REPOSITORY_POLICY,
                ValidationCheckState.PASSED)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        Assert(result.PermitsProduction, "A passing required check must permit production.");
    }

    public static void UnavailableBestEffortDoesNotBlockCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("runtime-deep-check", ValidationClassification.BEST_EFFORT,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.NOT_AVAILABLE,
                ValidationFindingKind.OPTIONAL_VALIDATION_UNAVAILABLE)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        AssertEqual(1, result.OptionalUnavailable);
        Assert(result.PermitsProduction, "Unavailable best-effort validation must not block.");
    }

    public static void DiscoveredRecommendationDoesNotBlockCompletion()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("deeper-runtime-proof", ValidationClassification.RECOMMENDED,
                ValidationRequirementSource.DISCOVERED,
                ValidationCheckState.RECORDED,
                ValidationFindingKind.TOOLING_IMPROVEMENT)
        ]);

        AssertEqual(ValidationPolicySchema.Pass, result.Status);
        AssertEqual(1, result.Recommendations);
        Assert(result.PermitsProduction, "A recommendation must not block production.");
    }

    public static void ExecutedOptionalModDefectIsStillSurfaced()
    {
        ValidationPolicyResult result = ValidationPolicyEvaluator.Evaluate([
            Observation("required", ValidationClassification.REQUIRED,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.PASSED),
            Observation("optional-runtime", ValidationClassification.BEST_EFFORT,
                ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                ValidationCheckState.FAILED,
                ValidationFindingKind.MOD_DEFECT)
        ]);

        AssertEqual(ValidationPolicySchema.Fail, result.Status);
        AssertEqual(1, result.OptionalDefects);
        Assert(!result.PermitsProduction, "An executed optional mod defect must remain blocking.");
    }

    public static void DiscoveredValidationCannotEscalateToRequired()
    {
        bool rejected = false;
        try
        {
            ValidationPolicyEvaluator.Define(
                "new-idea",
                ValidationClassification.REQUIRED,
                ValidationRequirementSource.DISCOVERED,
                "A newly noticed validation idea");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "A discovered validation must not silently become REQUIRED.");
    }

    public static void StructuredOutputSeparatesDefectAndRecommendation()
    {
        using var store = new AgentObservabilityStore();
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = "run",
            AgentId = "agent",
            ModId = "mod",
            ModName = "Example Mod",
            StartTime = 1
        });
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Optional runtime assertion failed.",
            new
            {
                operationKey = "test:optional-runtime",
                validationId = "optional-runtime",
                validationClassification = "BEST_EFFORT",
                issueKind = "MOD_DEFECT",
                blocking = true,
                errorCode = "MOD_ASSERTION_FAILED",
                evidenceReference = "evidence/mod-failure"
            }));
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationRecommendationRecorded,
            "Deep runtime test is unavailable.",
            new
            {
                operationKey = "validation:deep-runtime",
                validationId = "deep-runtime",
                validationClassification = "RECOMMENDED",
                issueKind = "TOOLING_IMPROVEMENT",
                blocking = false,
                componentOwner = "RimBridgeServer",
                recommendation = "Add a bounded deep runtime recipe."
            }));

        AgentIssue[] issues = store.GetIssues().ToArray();
        AgentIssue defect = issues.Single(issue => issue.Classification == "MOD_DEFECT");
        AgentIssue recommendation = issues.Single(issue =>
            issue.Classification == "TOOLING_IMPROVEMENT");
        AssertEqual(AgentIssueCategory.ModDefect, defect.Category);
        Assert(defect.Blocking, "The mod defect must be marked blocking.");
        AssertEqual("optional-runtime", defect.AffectedValidation);
        AssertEqual(AgentIssueCategory.ToolingImprovement, recommendation.Category);
        Assert(!recommendation.Blocking, "The tooling recommendation must be non-blocking.");
        AssertEqual("RimBridgeServer", recommendation.ComponentOwner);
        Assert(recommendation.Recommendation is not null,
            "The recommendation must remain queryable in structured output.");
    }

    public static void NonBlockingRecommendationPersistsAfterCompletion()
    {
        string directory = Path.Combine(Path.GetTempPath(), "rimliaison-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var store = new AgentObservabilityStore(directory))
            {
                store.RegisterAgent(new AgentSnapshot
                {
                    RunId = "run",
                    AgentId = "agent",
                    ModId = "mod",
                    ModName = "Example Mod",
                    StartTime = 1
                });
                store.AppendEvent(new AgentEventRequest(
                    "run",
                    "agent",
                    "mod",
                    DevelopmentStage.Testing,
                    AgentEventTypes.ValidationRecommendationRecorded,
                    "Optional runtime test is unavailable.",
                    new
                    {
                        operationKey = "validation:obscure-runtime",
                        validationId = "obscure-runtime",
                        validationClassification = "RECOMMENDED",
                        issueKind = "TOOLING_IMPROVEMENT",
                        blocking = false,
                        recommendation = "Add the obscure runtime test."
                    }));
            }

            using var reloaded = new AgentObservabilityStore(directory);
            AgentIssue issue = reloaded.GetIssues().Single();
            AssertEqual("obscure-runtime", issue.AffectedValidation);
            Assert(!issue.Blocking, "Persisted recommendations must remain non-blocking.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ValidationCheckObservation Observation(
        string id,
        ValidationClassification classification,
        ValidationRequirementSource source,
        ValidationCheckState state,
        ValidationFindingKind? finding = null) =>
        new()
        {
            Check = ValidationPolicyEvaluator.Define(
                id,
                classification,
                source,
                "Validation " + id),
            State = state,
            Finding = finding,
            Recommendation = finding == ValidationFindingKind.TOOLING_IMPROVEMENT
                ? "Record separately."
                : null
        };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
