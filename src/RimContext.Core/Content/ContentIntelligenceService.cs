using System.Text.Json;
using System.Security.Cryptography;
using RimContext.Core.Contracts;
using System.Text;
using RimContext.Core.Configuration;
using RimContext.Core.Model;
using RimContext.Core.Storage;

namespace RimContext.Core.Content;

public sealed class ContentIntelligenceService
{
    public ContentIntelligenceService(ContentIntelligenceStore? store = null)
    {
        Store = store ?? new ContentIntelligenceStore(
            ContentIntelligenceStorage.ResolveDefaultPath());
    }

    public ContentIntelligenceStore Store { get; }

    public ContentBlueprint CaptureBlueprint(ContentBlueprintCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            throw new ArgumentException("A content blueprint requires a workspace root.", nameof(request));
        }

        WorkspaceConfiguration configuration = WorkspaceConfiguration.Resolve(request.RootPath, request.StorePath);
        DerivedFacts facts = DeriveFacts(configuration, request.ChangedPaths);
        string repository = request.Repository ?? new DirectoryInfo(configuration.RootPath).Name;
        string capturedAt = request.CapturedAtUtc ?? DateTimeOffset.UtcNow.ToString("O");
        ContentSourceIdentity sourceIdentity = new(
            repository,
            request.Project,
            request.Commit,
            facts.SourceFingerprint,
            facts.WorkspaceIdentity,
            facts.ToolVersion ?? IndexConstants.ToolVersion,
            request.RimWorldVersion);
        string blueprintId = StableEntityId.Create(
            "content-blueprint",
            repository,
            string.Join('\0',
                request.Project,
                request.RunId,
                request.Intent.ContentKind,
                request.Intent.GameplayRole,
                sourceIdentity.SourceFingerprint));

        ContentBlueprint blueprint = new(
            ContentIntelligenceSchemas.Blueprint,
            blueprintId,
            NormalizeIntent(request.Intent),
            new ContentBlueprintMetadata(
                repository,
                request.Project,
                request.AgentId,
                request.SessionId,
                request.RunId,
                facts.SourceFiles,
                facts.EntityIdentifiers,
                facts.Dependencies,
                facts.FrameworkDependencies,
                sourceIdentity,
                capturedAt,
                capturedAt,
                request.ValidationEvidence,
                request.RepairHistory,
                request.Metrics,
                request.LogicalAgentId));
        ContentReuseDecision reuseDecision = SelectReuse(new ContentReuseRequest(
            blueprint.Intent.ContentKind,
            blueprint.Intent.GameplayRole,
            blueprint.Intent.DesignParameters,
            blueprint.Intent.FrameworkRequirements,
            request.Project,
            configuration.RootPath,
            request.StorePath));
        blueprint = blueprint with
        {
            Intent = blueprint.Intent with { ReuseSource = reuseDecision.Source },
            ReuseDecision = reuseDecision,
            ValidationRequirements = request.ValidationRequirements?
                .Select(static requirement => requirement.Normalize())
                .Take(16)
                .ToArray()
        };
        Store.SaveBlueprint(blueprint);
        return blueprint;
    }

    public ContentEvidence CaptureEvidence(ContentEvidenceCaptureRequest request) =>
        CaptureEvidenceLifecycle(request).Evidence;

    public ContentEvidenceLifecycleResult CaptureEvidenceLifecycle(
        ContentEvidenceCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BlueprintId))
        {
            throw new ArgumentException("Evidence requires a blueprint ID.", nameof(request));
        }

        string evidenceId = StableEntityId.Create(
            "content-evidence",
            request.BlueprintId,
            string.Join('\0',
                request.SourceIdentity.SourceFingerprint,
                request.SourceIdentity.Commit,
                request.Outcome.Final,
                request.CapturedAtUtc));
        var evidence = new ContentEvidence(
            ContentIntelligenceSchemas.Evidence,
            evidenceId,
            request.BlueprintId,
            request.SourceIdentity,
            request.Outcome,
            NormalizeStrings(request.Errors),
            NormalizeStrings(request.Warnings),
            request.Repairs,
            request.CapturedAtUtc ?? DateTimeOffset.UtcNow.ToString("O"),
            request.Metrics,
            NormalizeStrings(request.EvidenceReferences));
        Store.SaveEvidence(evidence);
        ContentPrecedentCandidate[] candidates = [];
        var promotions = new List<ContentPromotionResult>();
        ContentArchetypeUsage? usage = null;
        bool quarantined = false;
        string? quarantineReason = null;
        int? rolledBackToVersion = null;
        ContentBlueprint? blueprint = Store.GetBlueprint(request.BlueprintId);
        if (blueprint is not null)
        {
            bool evidenceCurrent = blueprint.Metadata.SourceIdentity is { } expected &&
                SourceIdentityMatches(expected, evidence.SourceIdentity);
            if (evidenceCurrent)
            {
                IReadOnlyList<string> evidenceIds = (blueprint.Metadata.ValidationEvidence ?? [])
                    .Append(evidence.EvidenceId)
                    .Distinct(StringComparer.Ordinal)
                    .Take(64)
                    .ToArray();
                IReadOnlyList<ContentRepairAttempt>? repairs =
                    evidence.Repairs is { Count: > 0 }
                        ? (blueprint.Metadata.RepairHistory ?? [])
                            .Concat(evidence.Repairs)
                            .Take(128)
                            .ToArray()
                        : blueprint.Metadata.RepairHistory;
                blueprint = blueprint with
                {
                    Metadata = blueprint.Metadata with
                    {
                        UpdatedAtUtc = evidence.CapturedAtUtc,
                        ValidationEvidence = evidenceIds,
                        RepairHistory = repairs
                    }
                };
                Store.SaveBlueprint(blueprint);
            }

            if (evidenceCurrent)
            {
                ContentIntelligenceSnapshot before = Store.Snapshot();
                (usage, quarantined, quarantineReason) = HandleArchetypeOutcome(blueprint, evidence);
                if (quarantined && usage is not null)
                {
                    rolledBackToVersion = before.Archetypes
                        .Where(item => item.ArchetypeId == usage.ArchetypeId &&
                            item.Status == "active" &&
                            item.Version < usage.ArchetypeVersion)
                        .Select(item => (int?)item.Version)
                        .OrderByDescending(item => item)
                        .FirstOrDefault();
                }
                ContentAnalysisResult analysis = Analyze(new ContentAnalysisRequest(
                    blueprint.Intent.ContentKind,
                    blueprint.Intent.GameplayRole));
                candidates = analysis.Candidates.ToArray();
                foreach (ContentPrecedentCandidate candidate in candidates
                             .Where(candidate => candidate.Qualification.Qualified &&
                                 candidate.BlueprintIds.Contains(blueprint.BlueprintId, StringComparer.Ordinal)))
                {
                    promotions.Add(Promote(candidate));
                }
            }
        }

        return new ContentEvidenceLifecycleResult(
            evidence,
            candidates,
            promotions,
            usage,
            quarantined,
            quarantineReason,
            rolledBackToVersion);
    }

    public ContentQueryResult Query(ContentQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContentQueryResult stored = Store.Query(
            request with
            {
                Limit = 100,
                MaxBytes = 1_048_576
            });
        ContentPrecedentSummary[] indexedVanilla = QueryIndexedVanilla(request);
        IEnumerable<ContentPrecedentSummary> candidates = stored.Results
            .Concat(indexedVanilla)
            .OrderBy(item => item.TrustRank)
            .ThenBy(item => item.BlueprintId, StringComparer.Ordinal);
        var results = candidates
            .Take(Math.Clamp(request.Limit, 1, 100))
            .ToList();
        bool truncated = stored.Truncated || candidates.Skip(results.Count).Any();
        int maxBytes = Math.Clamp(request.MaxBytes, 256, 1_048_576);
        while (results.Count > 0 &&
               System.Text.Encoding.UTF8.GetByteCount(ContentIntelligenceJson.Serialize(
                   new ContentQueryResult(
                       ContentIntelligenceSchemas.Query,
                       results,
                       truncated,
                       Math.Clamp(request.Limit, 1, 100),
                       maxBytes))) > maxBytes)
        {
            results.RemoveAt(results.Count - 1);
            truncated = true;
        }

        return new ContentQueryResult(
            ContentIntelligenceSchemas.Query,
            results,
            truncated,
            Math.Clamp(request.Limit, 1, 100),
            maxBytes);
    }

    public ContentAnalysisResult Analyze(ContentAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContentIntelligenceSnapshot snapshot = Store.Snapshot();
        var groups = snapshot.Blueprints
            .Where(blueprint =>
                (string.IsNullOrWhiteSpace(request.ContentKind) ||
                 string.Equals(blueprint.Intent.ContentKind, request.ContentKind, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(request.GameplayRole) ||
                 string.Equals(blueprint.Intent.GameplayRole, request.GameplayRole, StringComparison.OrdinalIgnoreCase)) &&
                MatchesRequestedShape(blueprint, request))
            .GroupBy(ContentStructuralFingerprinting.Compute)
            .OrderBy(group => group.Key.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var candidates = groups
            .Select(group =>
            {
                ContentBlueprint[] blueprints = group.OrderBy(item => item.BlueprintId, StringComparer.Ordinal).ToArray();
                ContentQualificationResult qualification = ContentQualificationEngine.Evaluate(
                    group.Key,
                    blueprints,
                    snapshot.Evidences,
                    request.Criteria);
                return new ContentPrecedentCandidate(
                    "candidate:" + group.Key.Fingerprint,
                    group.Key,
                    blueprints.Select(item => item.BlueprintId).ToArray(),
                    blueprints.Select(item => item.Metadata.Project)
                        .Where(project => !string.IsNullOrWhiteSpace(project))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(project => project, StringComparer.Ordinal)
                        .ToArray()!,
                    qualification,
                    blueprints.FirstOrDefault()?.BlueprintId);
            })
            .OrderByDescending(candidate => candidate.Qualification.Qualified)
            .ThenBy(candidate => candidate.StructuralFingerprint.Fingerprint, StringComparer.Ordinal)
            .ToList();
        int limit = Math.Clamp(request.Limit, 1, 100);
        int maxBytes = Math.Clamp(request.MaxBytes, 256, 1_048_576);
        bool truncated = candidates.Count > limit;
        candidates = candidates.Take(limit).ToList();
        while (candidates.Count > 0 &&
               System.Text.Encoding.UTF8.GetByteCount(ContentPhase2Json.Serialize(
                   new ContentAnalysisResult(
                       ContentPhase2Schemas.Analysis,
                       candidates,
                       truncated,
                       limit,
                       maxBytes))) > maxBytes)
        {
            candidates.RemoveAt(candidates.Count - 1);
            truncated = true;
        }

        return new ContentAnalysisResult(
            ContentPhase2Schemas.Analysis,
            candidates,
            truncated,
            limit,
            maxBytes);
    }

    public ContentPromotionResult Promote(ContentPrecedentCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ContentIntelligenceSnapshot snapshot = Store.Snapshot();
        ContentBlueprint[] cluster = snapshot.Blueprints
            .Where(blueprint => candidate.BlueprintIds.Contains(blueprint.BlueprintId, StringComparer.Ordinal))
            .ToArray();
        ContentQualificationResult qualification = ContentQualificationEngine.Evaluate(
            candidate.StructuralFingerprint,
            cluster,
            snapshot.Evidences);
        var reasons = qualification.Reasons.ToList();
        if (cluster.Length != candidate.BlueprintIds.Count)
        {
            reasons.Add("SUPPORTING_BLUEPRINT_MISMATCH");
        }

        HashSet<string> supportingIds = qualification.SupportingBlueprintIds
            .ToHashSet(StringComparer.Ordinal);
        ContentBlueprint[] supporting = cluster
            .Where(blueprint => supportingIds.Contains(blueprint.BlueprintId))
            .ToArray();
        if (supporting.Length != supportingIds.Count)
        {
            reasons.Add("SUPPORTING_BLUEPRINT_MISMATCH");
        }

        ContentArchetype? latest = snapshot.Archetypes
            .Where(archetype => archetype.StructuralFingerprint.Fingerprint ==
                candidate.StructuralFingerprint.Fingerprint)
            .OrderByDescending(archetype => archetype.Version)
            .FirstOrDefault();
        int version = (latest?.Version ?? 0) + 1;
        if (supporting.Length == 0)
        {
            var replay = new ContentReplayResult(
                ContentPhase2Schemas.Replay,
                false,
                candidate.CandidateId,
                [],
                ["SUPPORTING_BLUEPRINT_MISMATCH"],
                []);
            return new ContentPromotionResult(
                ContentPhase2Schemas.Promotion,
                false,
                null,
                null,
                replay,
                reasons,
                candidate.CandidateId);
        }

        ContentPrecedentCandidate verifiedCandidate = candidate with
        {
            BlueprintIds = supporting.Select(item => item.BlueprintId).ToArray(),
            Qualification = qualification
        };
        ContentArchetype archetype = ContentArchetypeFactory.Create(
            verifiedCandidate,
            supporting,
            version);
        ContentReplayResult replayResult = ContentHistoricalReplay.Replay(archetype, supporting);
        if (reasons.Count > 0 || !replayResult.Passed)
        {
            return new ContentPromotionResult(
                ContentPhase2Schemas.Promotion,
                false,
                null,
                null,
                replayResult,
                reasons.Count > 0 ? reasons : replayResult.Failures,
                candidate.CandidateId);
        }

        Store.SaveArchetype(archetype);
        return new ContentPromotionResult(
            ContentPhase2Schemas.Promotion,
            true,
            archetype.ArchetypeId,
            archetype.Version,
            replayResult,
            [],
            candidate.CandidateId);
    }

    public ContentReuseDecision SelectReuse(ContentReuseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ContentIntelligenceSnapshot snapshot = Store.Snapshot();
        ContentArchetype? archetype = snapshot.Archetypes
            .Where(item => item.Status == "active" &&
                string.Equals(item.ContentKind, request.ContentKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.GameplayRole, request.GameplayRole, StringComparison.Ordinal) &&
                MatchesRequestedShape(item, request) &&
                !IsExcluded(item.ArchetypeId, request.Project, snapshot.Policies))
            .OrderByDescending(item => item.Version)
            .ThenBy(item => item.ArchetypeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (archetype is not null)
        {
            return new ContentReuseDecision(
                ContentPhase2Schemas.ReuseDecision,
                "RimContent",
                "active qualified RimContent archetype matched kind and role",
                [archetype.ArchetypeId],
                DateTimeOffset.UtcNow.ToString("O"));
        }
        ContentAnalysisRequest analysisRequest = new(
            request.ContentKind,
            request.GameplayRole,
            Criteria: new ContentQualificationCriteria(),
            DesignParameters: request.DesignParameters,
            FrameworkRequirements: request.FrameworkRequirements);
        ContentAnalysisResult analysis = Analyze(analysisRequest);
        ContentPrecedentCandidate? precedent = analysis.Candidates
            .FirstOrDefault(candidate => candidate.Qualification.Qualified &&
                candidate.RepresentativeBlueprintId is not null &&
                !IsExcluded(candidate, request.Project, snapshot.Policies));
        if (precedent is not null)
        {
            return new ContentReuseDecision(
                ContentPhase2Schemas.ReuseDecision,
                ContentReuseSources.Precedent,
                "qualified independent ecosystem precedent matched structural shape",
                [precedent.CandidateId, .. precedent.Qualification.SupportingBlueprintIds],
                DateTimeOffset.UtcNow.ToString("O"));
        }

        HashSet<string> matchingVanillaIds = snapshot.Blueprints
            .Where(blueprint => blueprint.Intent.ReuseSource == ContentReuseSources.VanillaReference &&
                MatchesRequestedShape(blueprint, analysisRequest))
            .Select(blueprint => blueprint.BlueprintId)
            .ToHashSet(StringComparer.Ordinal);
        ContentQueryRequest vanillaRequest = new(
            ContentKind: request.ContentKind,
            GameplayRole: request.GameplayRole,
            Project: request.Project,
            Limit: 100,
            RootPath: request.RootPath,
            IndexStorePath: request.IndexStorePath);
        ContentPrecedentSummary? vanilla = Store.Query(vanillaRequest)
            .Results
            .Where(item => item.ReuseSource == ContentReuseSources.VanillaReference &&
                matchingVanillaIds.Contains(item.BlueprintId))
            .Concat(QueryIndexedVanilla(vanillaRequest))
            .FirstOrDefault();
        if (vanilla is not null)
        {
            return new ContentReuseDecision(
                ContentPhase2Schemas.ReuseDecision,
                ContentReuseSources.VanillaReference,
                "indexed vanilla/reference pattern is the strongest available reference",
                [vanilla.BlueprintId],
                DateTimeOffset.UtcNow.ToString("O"));
        }

        return new ContentReuseDecision(
            ContentPhase2Schemas.ReuseDecision,
            ContentReuseSources.Novel,
            "no qualified archetype, precedent, or indexed vanilla reference fit",
            null,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    private (ContentArchetypeUsage? Usage, bool Quarantined, string? Reason) HandleArchetypeOutcome(
        ContentBlueprint blueprint,
        ContentEvidence evidence)
    {
        if (blueprint.ReuseDecision?.Source != ContentReuseSources.RimContent ||
            blueprint.ReuseDecision.ReferenceIds is not { Count: > 0 })
        {
            return (null, false, null);
        }

        string archetypeId = blueprint.ReuseDecision.ReferenceIds[0];
        ContentArchetype? archetype = Store.Snapshot().Archetypes
            .Where(item => item.ArchetypeId == archetypeId && item.Status == "active")
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();
        if (archetype is null)
        {
            return (null, false, null);
        }

        bool succeeded = IsPass(evidence.Outcome.Final);
        var usage = new ContentArchetypeUsage(
            "content-archetype-usage/v1",
            StableEntityId.Create("archetype-usage", archetype.ArchetypeId, evidence.EvidenceId),
            archetype.ArchetypeId,
            archetype.Version,
            blueprint.BlueprintId,
            evidence.EvidenceId,
            succeeded,
            evidence.CapturedAtUtc,
            evidence.SourceIdentity.SourceFingerprint);
        Store.SaveUsage(usage);
        const string reason = "ATTRIBUTABLE_REUSE_FAILURE";
        if (!succeeded)
        {
            Store.SaveArchetype(archetype with
            {
                Status = "quarantined",
                QuarantinedAtUtc = evidence.CapturedAtUtc,
                QuarantineReason = reason
            });
            return (usage, true, reason);
        }

        return (usage, false, null);
    }

    private static bool MatchesRequestedShape(
        ContentBlueprint blueprint,
        ContentAnalysisRequest request) =>
        (request.DesignParameters is null ||
         MapsEqual(request.DesignParameters, blueprint.Intent.DesignParameters)) &&
        (request.FrameworkRequirements is null ||
         ValuesEqual(request.FrameworkRequirements, blueprint.Intent.FrameworkRequirements));

    private static bool MatchesRequestedShape(
        ContentArchetype archetype,
        ContentReuseRequest request) =>
        (request.DesignParameters is null ||
         MapsEqual(request.DesignParameters, archetype.Defaults)) &&
        (request.FrameworkRequirements is null ||
         ValuesEqual(request.FrameworkRequirements, archetype.FrameworkRequirements));

    private static bool MapsEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string>? actual) =>
        actual is not null &&
        expected.Count == actual.Count &&
        expected.All(pair => actual.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static bool ValuesEqual(
        IReadOnlyList<string> expected,
        IReadOnlyList<string>? actual) =>
        actual is not null &&
        expected.OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(actual.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool IsExcluded(
        ContentPrecedentCandidate candidate,
        string? project,
        IReadOnlyList<ContentPrecedentPolicy> policies) =>
        IsExcluded(candidate.CandidateId, project, policies) ||
        (candidate.RepresentativeBlueprintId is not null &&
         IsExcluded(candidate.RepresentativeBlueprintId, project, policies)) ||
        candidate.BlueprintIds.Any(id => IsExcluded(id, project, policies));

    private static bool IsExcluded(
        string precedentId,
        string? project,
        IReadOnlyList<ContentPrecedentPolicy> policies) =>
        policies.Any(policy =>
            policy.Excluded &&
            string.Equals(policy.PrecedentId, precedentId, StringComparison.Ordinal) &&
            (policy.Project is null ||
             string.Equals(policy.Project, project, StringComparison.OrdinalIgnoreCase)));

    private static bool IsPass(string? value) =>
        string.Equals(value, "PASS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "SUCCESS", StringComparison.OrdinalIgnoreCase);

    public void SetProjectPolicy(ContentPrecedentPolicy policy) => Store.SetPolicy(policy);

    private static ContentPrecedentSummary[] QueryIndexedVanilla(ContentQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            return [];
        }

        try
        {
            WorkspaceConfiguration configuration = WorkspaceConfiguration.Resolve(
                request.RootPath,
                request.IndexStorePath);
            if (!File.Exists(configuration.StorePath))
            {
                return [];
            }

            using IndexStore store = IndexStore.OpenReadOnly(configuration);
            IReadOnlyList<IndexedFileRecord> files = store.GetFiles();
            var filesById = files.ToDictionary(file => file.Id, StringComparer.Ordinal);
            return store.GetEntities()
                .Where(entity => entity.Kind == "def" &&
                    entity.FileId is not null &&
                    filesById.TryGetValue(entity.FileId, out IndexedFileRecord? file) &&
                    IsVanillaPath(file.Path))
                .Select(entity =>
                {
                    IndexedFileRecord file = filesById[entity.FileId!];
                    (string Kind, string Identity)? definition = ReadDefIdentity(entity.PayloadJson);
                    if (definition is null)
                    {
                        return null;
                    }

                    return new ContentPrecedentSummary(
                        "indexed:" + entity.Id,
                        definition.Value.Kind,
                        null,
                        ContentReuseSources.VanillaReference,
                        null,
                        file.ContentHash,
                        null,
                        [file.Path],
                        [definition.Value.Identity],
                        null,
                        null,
                        "REFERENCE",
                        null,
                        ContentReuseSources.TrustRank(ContentReuseSources.VanillaReference));
                })
                .Where(item => item is not null)
                .Select(item => item!)
                .Where(item => MatchesIndexed(item, request))
                .OrderBy(item => item.BlueprintId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RimContextException)
        {
            return [];
        }
    }

    private static (string Kind, string Identity)? ReadDefIdentity(string payloadJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            string? kind = root.TryGetProperty("defType", out JsonElement defType)
                ? defType.GetString()
                : null;
            string? name = root.TryGetProperty("defName", out JsonElement defName)
                ? defName.GetString()
                : null;
            return string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name)
                ? null
                : (kind, kind + "/" + name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsVanillaPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Data/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesIndexed(ContentPrecedentSummary item, ContentQueryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ContentKind) &&
            !string.Equals(item.ContentKind, request.ContentKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(request.Query) ||
            item.EntityIdentifiers?.Any(identifier =>
                identifier.Contains(request.Query, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public static bool SourceIdentityMatches(ContentSourceIdentity expected, ContentSourceIdentity actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (!HasKnownIdentity(expected))
        {
            return false;
        }

        return Matches(expected.Repository, actual.Repository) &&
            Matches(expected.Project, actual.Project) &&
            Matches(expected.Commit, actual.Commit) &&
            Matches(expected.SourceFingerprint, actual.SourceFingerprint) &&
            Matches(expected.WorkspaceIdentity, actual.WorkspaceIdentity) &&
            Matches(expected.ToolVersion, actual.ToolVersion) &&
            Matches(expected.RimWorldVersion, actual.RimWorldVersion);
    }

    private static bool HasKnownIdentity(ContentSourceIdentity identity) =>
        !string.IsNullOrWhiteSpace(identity.Repository) ||
        !string.IsNullOrWhiteSpace(identity.Project) ||
        !string.IsNullOrWhiteSpace(identity.Commit) ||
        !string.IsNullOrWhiteSpace(identity.SourceFingerprint) ||
        !string.IsNullOrWhiteSpace(identity.WorkspaceIdentity) ||
        !string.IsNullOrWhiteSpace(identity.ToolVersion) ||
        !string.IsNullOrWhiteSpace(identity.RimWorldVersion);
    private static bool Matches(string? expected, string? actual) =>
        expected is null || actual is not null && string.Equals(expected, actual, StringComparison.Ordinal);

    private static ContentBlueprintIntent NormalizeIntent(ContentBlueprintIntent intent) =>
        intent with
        {
            ContentKind = NormalizeText(intent.ContentKind),
            GameplayRole = NormalizeText(intent.GameplayRole),
            DesignParameters = NormalizeMap(intent.DesignParameters),
            VanillaComparables = NormalizeStrings(intent.VanillaComparables),
            FrameworkRequirements = NormalizeStrings(intent.FrameworkRequirements),
            ProjectConstraints = NormalizeStrings(intent.ProjectConstraints),
            ValidationExpectations = NormalizeStrings(intent.ValidationExpectations),
            ReuseSource = NormalizeText(intent.ReuseSource)
        };

    private static IReadOnlyDictionary<string, string>? NormalizeMap(
        IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(32))
        {
            result[BoundText(pair.Key, 128)] = BoundText(pair.Value, 512);
        }

        return result.Count == 0 ? null : result;
    }

    private static IReadOnlyList<string>? NormalizeStrings(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        string[] result = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => BoundText(value.Trim(), 512))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return result.Length == 0 ? null : result;
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : BoundText(value.Trim(), 512);

    private static string BoundText(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    private static DerivedFacts DeriveFacts(
        WorkspaceConfiguration configuration,
        IReadOnlyList<string>? changedPaths)
    {
        string[] changed = (changedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path, configuration.RootPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(256)
            .ToArray();
        var files = new List<IndexedFileRecord>();
        var entities = new List<EntityRecord>();
        var relations = new List<RelationRecord>();
        string? workspaceIdentity = null;
        string? toolVersion = null;

        if (File.Exists(configuration.StorePath))
        {
            try
            {
                using IndexStore store = IndexStore.OpenReadOnly(configuration);
                workspaceIdentity = store.Metadata.WorkspaceIdentity;
                toolVersion = store.Metadata.ToolVersion;
                IReadOnlyList<IndexedFileRecord> indexedFiles = store.GetFiles();
                IReadOnlyList<IndexedFileRecord> selectedFiles = changed.Length == 0
                    ? indexedFiles
                    : indexedFiles.Where(file => changed.Contains(
                            file.Path,
                            StringComparer.OrdinalIgnoreCase))
                        .Where(file => IsCurrentIndexedFile(configuration.RootPath, file))
                        .ToArray();
                files.AddRange(selectedFiles);
                string[] fileIds = files.Select(file => file.Id).ToArray();
                entities.AddRange(store.GetEntities().Where(entity =>
                    entity.FileId is not null && fileIds.Contains(entity.FileId)));
                var entityIds = entities.Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
                relations.AddRange(store.GetRelations().Where(relation =>
                    (relation.FileId is not null && fileIds.Contains(relation.FileId)) ||
                    entityIds.Contains(relation.FromId)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RimContextException)
            {
                // A blueprint remains useful without a readable static index; unknown derived fields stay null.
            }
        }

        string[] sourceFiles = files
            .Select(file => file.Path)
            .Concat(changed.Where(path => !files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        string? fingerprint = BuildFingerprint(configuration.RootPath, files, sourceFiles, changed);
        string[] entityIdentifiers = entities
            .Select(entity => entity.IdentityKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(256)
            .ToArray();
        string[] dependencies = relations
            .Where(relation => relation.ToId is not null)
            .Select(relation => relation.ToId!)
            .Concat(entities.Where(entity => entity.Kind == "dependency").Select(entity => entity.IdentityKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(128)
            .ToArray();
        string[] frameworkDependencies = entities
            .Where(entity => entity.Kind == "dependency")
            .Select(entity => entity.IdentityKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .Take(64)
            .ToArray();

        return new DerivedFacts(
            sourceFiles.Length == 0 ? null : sourceFiles,
            entityIdentifiers.Length == 0 ? null : entityIdentifiers,
            dependencies.Length == 0 ? null : dependencies,
            frameworkDependencies.Length == 0 ? null : frameworkDependencies,
            fingerprint,
            workspaceIdentity,
            toolVersion);
    }

    private static string NormalizePath(string path, string root)
    {
        string full = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
        string relative = Path.GetRelativePath(root, full);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string? BuildFingerprint(
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> changedPaths)
    {
        var indexedHashes = files.ToDictionary(file => file.Path, file => file.ContentHash, StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (string path in sourceFiles)
        {
            string? hash = null;
            if (changedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                hash = TryReadFileHash(root, path);
            }

            hash ??= indexedHashes.GetValueOrDefault(path);
            if (hash is null)
            {
                continue;
            }

            builder.Append(path).Append('\0').Append(hash).Append('\n');
        }

        if (builder.Length == 0)
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool IsCurrentIndexedFile(string root, IndexedFileRecord file) =>
        string.Equals(TryReadFileHash(root, file.Path), file.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static string? TryReadFileHash(string root, string path)
    {
        string fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(fullPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record DerivedFacts(
        IReadOnlyList<string>? SourceFiles,
        IReadOnlyList<string>? EntityIdentifiers,
        IReadOnlyList<string>? Dependencies,
        IReadOnlyList<string>? FrameworkDependencies,
        string? SourceFingerprint,
        string? WorkspaceIdentity,
        string? ToolVersion);
}
