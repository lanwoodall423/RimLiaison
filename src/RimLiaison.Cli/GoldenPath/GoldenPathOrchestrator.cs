using RimLiaison.Observability;
using RimLiaison.Validation;

namespace RimLiaison.GoldenPath;

/// <summary>
/// Canonical mod-development orchestration. The orchestrator owns ordering,
/// scoped dependency blocking, classification, and one safe retry; component
/// adapters retain ownership of their implementation details.
/// </summary>
public sealed class GoldenPathOrchestrator
{
    public async Task<GoldenPathRunResult> RunAsync(
        GoldenPathRunRequest request,
        AgentObservabilitySession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ValidateIdentity(request.Identity, session);
        ValidateOperations(request.Operations);

        Dictionary<string, GoldenPathStepResult> steps =
            new(StringComparer.Ordinal);
        session.Start("golden-path");
        session.SetProductionState(
            DevelopmentStage.Analysis,
            "preflight",
            "none");

        GoldenPathStepResult preflightStep = request.Preflight.Ready
            ? GoldenPathStepResult.Passed("Preflight is ready.")
            : GoldenPathStepResult.ToolingFailure(
                request.Preflight.ErrorCode ?? "PREFLIGHT_BLOCKED",
                request.Preflight.ErrorCode ?? "PREFLIGHT_BLOCKED",
                request.Preflight.ComponentOwner ?? "RimLiaison",
                retryable: false,
                affectedValidation: "golden-path-preflight",
                evidence: request.Preflight);
        steps["preflight"] = preflightStep;
        RecordStep(session, GoldenPathStage.Preflight, "preflight", preflightStep);

        foreach (GoldenPathOperation operation in request.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoldenPathOperationContext context = new(
                request.Identity,
                session,
                new Dictionary<string, GoldenPathStepResult>(steps),
                request.Preflight);
            session.SetProductionState(
                ToObservabilityStage(operation.Stage),
                operation.Id,
                BlockingStateFor(operation.Check));

            string[] dependencies = operation.DependsOn.Count == 0
                ? ["preflight"]
                : operation.DependsOn.ToArray();
            string? blockedDependency = dependencies.FirstOrDefault(dependency =>
                !steps.TryGetValue(dependency, out GoldenPathStepResult? value) ||
                value.State != GoldenPathStepState.Passed);
            GoldenPathStepResult result;
            if (blockedDependency is not null)
            {
                result = GoldenPathStepResult.NotExecuted(
                    $"Not executed because '{blockedDependency}' did not pass.");
            }
            else
            {
                result = await ExecuteBoundedAsync(
                        operation,
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            steps[operation.Id] = result;
            RecordStep(session, operation.Stage, operation.Id, result);
            RecordFinding(session, operation, result);
        }

        session.SetProductionState(
            DevelopmentStage.Testing,
            "classify",
            BlockingStateFor(steps));
        List<ValidationCheckObservation> validationObservations = steps
            .Where(pair => pair.Key != "preflight")
            .Select(pair => ToObservation(
                request.Operations.FirstOrDefault(operation =>
                    string.Equals(operation.Id, pair.Key, StringComparison.Ordinal))?.Check ??
                ValidationPolicyEvaluator.Define(
                    pair.Key,
                    ValidationClassification.REQUIRED,
                    ValidationRequirementSource.TASK_REQUIREMENT,
                    pair.Key,
                    "RimLiaison"),
                pair.Value))
            .ToList();
        if (preflightStep.State != GoldenPathStepState.Passed)
        {
            validationObservations.Add(ToObservation(
                ValidationPolicyEvaluator.Define(
                    "golden-path-preflight",
                    ValidationClassification.REQUIRED,
                    ValidationRequirementSource.TOOLCHAIN_CONTRACT,
                    "Golden Path preflight",
                    request.Preflight.ComponentOwner ?? "RimLiaison"),
                preflightStep));
        }

        ValidationPolicyResult validation = ValidationPolicyEvaluator.Evaluate(validationObservations);

        session.SetProductionState(
            DevelopmentStage.Complete,
            "completion",
            validation.PermitsProduction ? "none" : "required",
            validation.Status);
        session.Complete(validation, validation.PermitsProduction
            ? "Golden Path completed; defined mod requirements passed."
            : "Golden Path completed with validation claims scoped to the blocked requirements.");

        IReadOnlyList<AgentIssue> issues = session.Store.GetIssues(
            session.RunId,
            session.AgentId,
            session.ModId,
            includeRecovered: true,
            limit: 500);
        return new GoldenPathRunResult
        {
            Identity = request.Identity,
            Status = validation.Status,
            CompletionResult = validation.Status,
            Preflight = request.Preflight,
            Validation = validation,
            Steps = steps,
            ToolingIncidentIds = issues
                .Where(issue => issue.Category == AgentIssueCategory.ToolingFailure)
                .Select(issue => issue.Id)
                .ToArray(),
            RecommendationIds = issues
                .Where(issue => issue.Category == AgentIssueCategory.ToolingImprovement ||
                    issue.Category == AgentIssueCategory.OptionalValidationUnavailable)
                .Select(issue => issue.Id)
                .ToArray()
        };
    }

    private static async Task<GoldenPathStepResult> ExecuteBoundedAsync(
        GoldenPathOperation operation,
        GoldenPathOperationContext context,
        CancellationToken cancellationToken)
    {
        GoldenPathStepResult result;
        try
        {
            result = await operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = GoldenPathStepResult.ToolingFailure(
                exception.Message,
                "GOLDEN_PATH_OPERATION_FAILED",
                operation.Check.ComponentOwner ?? "RimLiaison",
                retryable: true,
                affectedValidation: operation.Id);
        }

        if (result.Finding != ValidationFindingKind.TOOLING_FAILURE || !result.Retryable)
        {
            return result;
        }

        context.Session.Record(
            ToObservabilityStage(operation.Stage),
            AgentEventTypes.RetryStarted,
            "Golden Path safely retrying a tooling operation.",
            new
            {
                operationKey = operation.Id,
                issueKind = "TOOLING_FAILURE",
                componentOwner = result.ComponentOwner ?? operation.Check.ComponentOwner,
                retryCount = 1,
                automaticToolRepair = false
            });
        GoldenPathStepResult retried;
        try
        {
            retried = operation.RetryAsync is null
                ? await operation.ExecuteAsync(context, cancellationToken).ConfigureAwait(false)
                : await operation.RetryAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            retried = GoldenPathStepResult.ToolingFailure(
                exception.Message,
                "GOLDEN_PATH_RETRY_FAILED",
                result.ComponentOwner ?? operation.Check.ComponentOwner ?? "RimLiaison",
                retryable: false,
                affectedValidation: operation.Id);
        }

        context.Session.Record(
            ToObservabilityStage(operation.Stage),
            AgentEventTypes.RetryCompleted,
            retried.State == GoldenPathStepState.Passed
                ? "Golden Path tooling retry succeeded."
                : "Golden Path tooling retry failed; no tooling development was started.",
            new
            {
                operationKey = operation.Id,
                retryCount = 1,
                recovered = retried.State == GoldenPathStepState.Passed,
                issueKind = retried.Finding?.ToString(),
                componentOwner = retried.ComponentOwner ?? result.ComponentOwner ?? operation.Check.ComponentOwner
            });
        return retried;
    }

    private static ValidationCheckObservation ToObservation(
        ValidationCheckDefinition check,
        GoldenPathStepResult result) => new()
        {
            Check = check,
            State = result.State switch
            {
                GoldenPathStepState.Passed => ValidationCheckState.PASSED,
                GoldenPathStepState.Failed => ValidationCheckState.FAILED,
                GoldenPathStepState.Unavailable => ValidationCheckState.NOT_AVAILABLE,
                _ => ValidationCheckState.NOT_EXECUTED
            },
            Finding = result.Finding,
            EvidenceReference = result.EvidenceReference,
            Recommendation = result.Recommendation,
            Summary = result.Summary
        };

    private static void RecordStep(
        AgentObservabilitySession session,
        GoldenPathStage stage,
        string operation,
        GoldenPathStepResult result)
    {
        session.Record(
            ToObservabilityStage(stage),
            result.State == GoldenPathStepState.Passed
                ? AgentEventTypes.ToolCompleted
                : result.State == GoldenPathStepState.NotExecuted
                    ? AgentEventTypes.InformationalProductionEvent
                    : AgentEventTypes.ToolFailed,
            result.Summary ?? $"Golden Path operation '{operation}' completed.",
            new
            {
                operationKey = operation,
                goldenPathStage = stage.ToString(),
                state = result.State.ToString(),
                outcome = result.State == GoldenPathStepState.Passed ? "success" : "failure",
                operationAttempted = result.OperationAttempted,
                errorCode = result.ErrorCode,
                evidenceReference = result.EvidenceReference,
                affectedValidation = result.AffectedValidation,
                componentOwner = result.ComponentOwner,
                issueKind = result.Finding?.ToString(),
                evidence = result.Evidence
            });
    }

    private static void RecordFinding(
        AgentObservabilitySession session,
        GoldenPathOperation operation,
        GoldenPathStepResult result)
    {
        if (result.Finding == ValidationFindingKind.TOOLING_IMPROVEMENT ||
            result.Finding == ValidationFindingKind.OPTIONAL_VALIDATION_UNAVAILABLE ||
            !string.IsNullOrWhiteSpace(result.Recommendation))
        {
            session.RecordToolingRecommendation(
                operation.Id,
                result.Summary ?? operation.Check.Summary,
                result.Recommendation ?? "Record the unavailable or newly discovered validation capability.",
                result.ComponentOwner ?? operation.Check.ComponentOwner,
                result.EvidenceReference,
                affectedCurrentTask: true,
                priority: "normal",
                evidence: result.Evidence);
        }

        if (result.Finding == ValidationFindingKind.TOOLING_FAILURE)
        {
            session.RecordToolingIncident(
                operation.Id,
                result.Summary ?? "Tooling operation failed.",
                result.ErrorCode,
                result.ComponentOwner ?? operation.Check.ComponentOwner ?? "RimLiaison",
                operation.Check.Classification,
                result.AffectedValidation ?? operation.Id,
                result.EvidenceReference,
                result.Retryable ? "retryable" : "retry-exhausted");
        }
    }

    private static string BlockingStateFor(ValidationCheckDefinition check) =>
        check.Classification == ValidationClassification.REQUIRED ? "required" : "optional";

    private static string BlockingStateFor(
        IReadOnlyDictionary<string, GoldenPathStepResult> steps) =>
        steps.Values.Any(value => value.Finding == ValidationFindingKind.MOD_DEFECT)
            ? "required"
            : steps.Values.Any(value => value.State is GoldenPathStepState.Unavailable or GoldenPathStepState.NotExecuted)
                ? "optional"
                : "none";

    private static DevelopmentStage ToObservabilityStage(GoldenPathStage stage) => stage switch
    {
        GoldenPathStage.Preflight or GoldenPathStage.Requirements or GoldenPathStage.Selection => DevelopmentStage.Analysis,
        GoldenPathStage.Build or GoldenPathStage.Deploy => DevelopmentStage.Packaging,
        GoldenPathStage.RuntimeStartup or GoldenPathStage.RuntimeValidation or GoldenPathStage.Evidence => DevelopmentStage.Testing,
        GoldenPathStage.Classification or GoldenPathStage.Publish or GoldenPathStage.Completion => DevelopmentStage.Complete,
        _ => DevelopmentStage.Research
    };

    private static void ValidateIdentity(
        GoldenPathIdentity identity,
        AgentObservabilitySession session)
    {
        if (!string.Equals(identity.RunId, session.RunId, StringComparison.Ordinal) ||
            !string.Equals(identity.AgentId, session.AgentId, StringComparison.Ordinal) ||
            !string.Equals(identity.ModId, session.ModId, StringComparison.Ordinal) ||
            !string.Equals(identity.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Golden Path identity does not match the production session.");
        }
    }

    private static void ValidateOperations(IReadOnlyList<GoldenPathOperation> operations)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (GoldenPathOperation operation in operations)
        {
            if (!ids.Add(operation.Id))
            {
                throw new InvalidOperationException($"Golden Path operation id is duplicated: {operation.Id}.");
            }
            ValidationPolicyEvaluator.EnsureContractValid(operation.Check);
        }
    }
}
