using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimContext.Cli;
using RimContext.Core;
using RimContext.Core.Configuration;
using RimContext.Core.Context;
using RimContext.Core.Contracts;
using RimContext.Core.Content;

using RimContext.Core.Discovery;
using RimContext.Core.Model;
using RimContext.Core.Storage;
using RimContext.Core.Impact;


namespace RimContext.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("cli startup and version", CliStartupAndVersion),
            ("context bundle schema and serialization", ContextBundleSchemaAndSerialization),
            ("context bundle unavailable sections", ContextBundleUnavailableSections),
            ("context bundle generated repository state", ContextBundleGeneratedRepositoryState),
            ("context bundle agent summary and correspondence", ContextBundleAgentSummaryAndCorrespondence),
            ("context bundle stale runtime and failure taxonomy", ContextBundleStaleRuntimeAndFailureTaxonomy),
            ("context bundle decision provenance", ContextBundleDecisionProvenance),
            ("context bundle stale ordering", ContextBundleStaleOrdering),
            ("context bundle provider failure", ContextBundleProviderFailure),
            ("impact graph contract and provenance", ImpactGraphContractAndProvenance),
            ("impact graph dynamic and serialization classes", ImpactGraphDynamicAndSerialization),
            ("execution packet carries remediation precedents", ExecutionPacketCarriesRemediationPrecedents),
            ("execution packet revision status", ExecutionPacketRevisionStatus),
            ("predicted actual scope expansion", PredictedActualScopeExpansion),
            ("indexed graph and packet generation without runtime", IndexedGraphAndPacketGeneration),
            ("json-only stdout and stderr logging", JsonOnlyStdout),
            ("typed Core facade", TypedCoreFacade),
            ("unknown command and invalid input", InvalidInput),
            ("schema creation", SchemaCreation),
            ("schema reopening", SchemaReopening),
            ("incremental first index", IncrementalFirstIndex),
            ("incremental no-op reindex", IncrementalNoOp),
            ("incremental changed file", IncrementalChangedFile),
            ("incremental deleted file", IncrementalDeletedFile),
            ("incremental renamed file", IncrementalRenamedFile),
            ("stable ID determinism", StableIdDeterminism),
            ("schema-version handling", SchemaVersionHandling),
            ("workspace discovery", WorkspaceDiscoveryRules),
            ("excluded directories", ExcludedDirectories),
            ("path normalization", PathNormalization),
            ("invalid workspace input", InvalidWorkspaceInput),
            ("incremental fixture performance", IncrementalFixturePerformance),
            ("XML semantic entities and queries", XmlSemanticEntitiesAndQueries),
            ("malformed XML diagnostics", MalformedXmlDiagnostics),
            ("incremental XML cleanup", IncrementalXmlCleanup),
            ("C# semantic entities and queries", CSharpSemanticEntitiesAndQueries),
            ("Harmony indexing and queries", HarmonyIndexingAndQueries),
            ("mod project and dependency indexing", ModProjectDependencyIndexing),
            ("affected queries", AffectedQueries),
            ("compact query output", CompactQueryOutput),
            ("production end-to-end workflow", ProductionEndToEndWorkflow),
            ("store recovery and lock handling", StoreRecoveryAndLockHandling),
            ("content intelligence contracts and capture", ContentIntelligenceContractsAndCapture),
            ("content precedent query ordering and policies", ContentPrecedentQueryOrderingAndPolicies),
            ("content identity and bounded output", ContentIdentityAndBoundedOutput),
            ("content Phase 2 qualification, reuse, promotion, and quarantine", ContentPhase2QualificationReusePromotionAndQuarantine),
            ("minimum-safe validation planning scenarios", MinimumSafeValidationPlanningScenarios),
            ("validated remediation precedent safety", ValidatedRemediationPrecedentSafety),
        };

        var passed = 0;
        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                passed++;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;

                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"tests: {tests.Length}, passed: {passed}, failed: {failed}");
        return failed == 0 ? 0 : 1;
    }
    private static void ImpactGraphContractAndProvenance()
    {
        ImpactGraph graph = TestImpactGraph();
        string json = ImpactGraphJson.Serialize(graph);
        ImpactGraph roundTrip = ImpactGraphJson.Deserialize(json);
        AssertEqual(ImpactGraphSchemas.Current, roundTrip.SchemaVersion, "impact graph schema");
        AssertEqual(3, roundTrip.Edges.Count, "impact edge count");
        AssertEqual(ImpactEvidenceClasses.Deterministic, roundTrip.Edges[0].Provenance.EvidenceClass, "static provenance");
        AssertThrows<InvalidOperationException>(
            () => ImpactGraphJson.Deserialize(json.Replace(ImpactGraphSchemas.Current, "rimimpact-graph/v2", StringComparison.Ordinal)),
            "unsupported graph schema must fail");
    }

    private static void ImpactGraphDynamicAndSerialization()
    {
        ImpactGraph graph = TestImpactGraph();
        ImpactEdge harmony = graph.Edges.Single(edge => edge.RelationshipKind == ImpactRelationshipKinds.HarmonyTarget);
        AssertEqual(ImpactClasses.DynamicPotential, harmony.ImpactClass, "Harmony relationship class");
        AssertEqual(ImpactEvidenceClasses.Uncertain, harmony.Provenance.EvidenceClass, "Harmony provenance");
        ActualImpact impact = new ImpactGraphService().AnalyzeDiff(
            graph,
            ["Source/Plant.cs"],
            prediction: new PredictedImpact(["Source/Plant.cs"], ["source"], [ImpactClasses.Direct], [], "fixture"));
        Assert(impact.HarmonyOrDynamicRisk, "Harmony broadens actual impact");
        Assert(impact.SerializationRisk, "serialization broadens actual impact");
        Assert(impact.ValidationConcerns.Contains("Harmony/dynamic dispatch"), "dynamic validation concern");
        Assert(impact.ValidationConcerns.Contains("Serialization/save-load"), "serialization validation concern");
    }

    private static void ExecutionPacketRankedHardBudget()
    {
        ImpactGraph graph = TestImpactGraph();
        var evidence = Enumerable.Range(0, 100)
            .Select(index => new ImpactNode(
                "node-" + index,
                "source_file",
                "Defs/VeryLongComponent" + index + ".xml",
                "VeryLongComponent" + index,
                "Defs/VeryLongComponent" + index + ".xml"))
            .ToArray();
        graph = graph with { Nodes = graph.Nodes.Concat(evidence).ToArray() };
        ExecutionPacket packet = new ExecutionPacketBuilder().Build(
            graph,
            new ExecutionPacketRequest(
                "VeryLongComponent",
                Project: "Frontier",
                MaxBytes: 2_048,
                MaxEntries: 8));
        Assert(packet.Budget.Truncated, "large graph packet is truncated");
        Assert(packet.Budget.UsedBytes <= packet.Budget.MaxBytes, "packet hard budget");
        Assert(packet.TopFiles.Count <= 8, "packet entry cap");
        Assert(packet.LikelyImplementation?.Provenance is not null, "recommendation provenance");
        Assert(packet.Metrics.AgentInputTokens is null, "token savings remain unavailable");
    }

    private static void ExecutionPacketCarriesRemediationPrecedents()
    {
        ImpactGraph graph = TestImpactGraph();
        var identity = new global::RimDev.Contracts.ExecutionIdentity
        {
            RepositoryId = graph.Identity.Repository,
            ProjectId = graph.Identity.Project,
            SourceRevision = graph.Identity.SourceRevision,
            SourceFingerprint = "fingerprint-packet"
        };
        var precedent = new global::RimDev.Contracts.RemediationPrecedent
        {
            PrecedentId = "precedent-packet",
            FailureFamily = "validation-failed",
            Applicability = ["node.plant"],
            RootCause = "dependency changed",
            ValidatedRemediation = "run the focused validation",
            Evidence =
            [
                new global::RimDev.Contracts.EvidenceReference
                {
                    Kind = "test",
                    Uri = "evidence://packet"
                }
            ],
            SuccessfulValidationIdentity = identity,
            Provenance = new global::RimDev.Contracts.ContractProvenance
            {
                Source = "packet-test"
            }
        };
        ExecutionPacket packet = new ExecutionPacketBuilder().Build(
            graph,
            new ExecutionPacketRequest(
                "medicinal plant",
                Project: graph.Identity.Project,
                RemediationPrecedents: [precedent]));

        Assert(packet.RemediationPrecedents is not null, "packet retains precedent section");
        AssertEqual("precedent-packet", packet.RemediationPrecedents!.Single().PrecedentId, "packet precedent identity");
        Assert(packet.Budget.UsedBytes <= packet.Budget.MaxBytes, "packet budget");
        Assert(JsonSerializer.Serialize(packet).Contains("remediationPrecedents", StringComparison.Ordinal), "packet serialization");
    }

    private static void ExecutionPacketRevisionStatus()
    {
        ImpactGraph graph = TestImpactGraph();
        ExecutionPacket packet = new ExecutionPacketBuilder().Build(
            graph,
            new ExecutionPacketRequest("medicinal plant", Project: "Frontier"));
        ImpactGraph unchanged = graph;
        AssertEqual(
            ExecutionPacketStatuses.Valid,
            new ImpactGraphService().EvaluatePacket(packet, unchanged).Status,
            "unchanged packet valid");
        ImpactGraph unrelatedRevision = graph with
        {
            Identity = graph.Identity with { IndexGeneration = "new-generation" }
        };
        AssertEqual(
            ExecutionPacketStatuses.Valid,
            new ImpactGraphService().EvaluatePacket(packet, unrelatedRevision, ["README.txt"]).Status,
            "unrelated change preserves packet");
        AssertEqual(
            ExecutionPacketStatuses.PartiallyStale,
            new ImpactGraphService().EvaluatePacket(packet, unrelatedRevision, ["Source/Plant.cs"]).Status,
            "relevant change partially stales packet");
        AssertEqual(
            ExecutionPacketStatuses.Invalid,
            new ImpactGraphService().EvaluatePacket(packet, unrelatedRevision, ["Frontier.csproj"]).Status,
            "dependency boundary invalidates packet");
    }

    private static void PredictedActualScopeExpansion()
    {
        ImpactGraph graph = TestImpactGraph();
        PredictedImpact prediction = new(
            ["Source/Plant.xml"],
            ["plant"],
            [ImpactClasses.Direct],
            ["Def/reference"],
            "XML-only task");
        ActualImpact actual = new ImpactGraphService().AnalyzeDiff(
            graph,
            ["Source/Plant.cs", "Source/Plant.xml"],
            prediction: prediction);
        Assert(actual.ScopeExpanded, "actual scope expansion recorded");
        Assert(actual.ExpansionReasons.Count >= 2, "scope expansion reasons retained");
    }
    private static void IndexedGraphAndPacketGeneration()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Defs/Plants.xml",
            "<Defs><ThingDef><defName>Healroot</defName><description>medicine</description></ThingDef></Defs>");
        new RimContextService().Index(new RimContextIndexRequest(workspace.Root));
        var service = new RimContextService();
        ImpactGraph graph = service.BuildImpactGraph(
            new ImpactGraphBuildRequest(workspace.Root, Project: "Frontier"));
        Assert(graph.Nodes.Any(node => node.Kind == "def"), "indexed Def node");
        Assert(graph.Nodes.Any(node => node.File == "Defs/Plants.xml"), "indexed file node");
        AssertEqual(3, graph.Metrics.IndexedLookups, "bounded indexed lookups");
        ExecutionPacket packet = service.CreateExecutionPacket(
            graph,
            new ExecutionPacketRequest("add medicinal plant", Project: "Frontier", MaxBytes: 4_096));
        AssertEqual(ExecutionPacketSchemas.Current, packet.SchemaVersion, "indexed packet schema");
        Assert(packet.Metrics.IndexCacheHit, "packet uses indexed cache");
        Assert(packet.TopFiles.Count <= 16, "indexed packet remains bounded");
    }


    private static void MinimumSafeValidationPlanningScenarios()
    {
        ImpactGraph graph = TestImpactGraph();
        var identity = new ValidationSourceIdentity(
            "workspace",
            "source-revision",
            "index-generation",
            "ProjectA",
            "Repo");
        var catalog = new[]
        {
            new ValidationCatalogEntry(
                "static-test",
                "recipe-static",
                [new ValidationCoverage("def", "Plant")]),
            new ValidationCatalogEntry(
                "component-test",
                "recipe-component",
                [new ValidationCoverage("def", "Plant")]),
            new ValidationCatalogEntry(
                "runtime-test",
                "recipe-runtime",
                [],
                ["runtime", "quicktest"]),
            new ValidationCatalogEntry(
                "save-load-test",
                "recipe-save-load",
                [],
                ["save-load"]),
            new ValidationCatalogEntry(
                "framework-test",
                "recipe-framework",
                [],
                ["framework", "consumer"]),
            new ValidationCatalogEntry("fallback-one", "recipe-shared", []),
            new ValidationCatalogEntry("fallback-two", "recipe-shared", [])
        };
        var planner = new MinimumSafeValidationPlanner();

        ValidationPlan xmlPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.xml"], ["plant"], [ImpactClasses.Direct]),
            catalog,
            identity));
        AssertEqual(ValidationPlanTiers.NarrowTargeted, xmlPlan.Tier, "XML/Def tier");
        AssertEqual(ValidationRequirementKinds.StaticReference, xmlPlan.Required[0].Kind, "XML/Def requirement");

        var learned = new LearnedImpactRelationship(
            ValidationPlanSchemas.LearningCurrent,
            "Plant",
            "component-test",
            "direct-dependent",
            ImpactClasses.Direct,
            new ImpactProvenance("fixture", ImpactEvidenceClasses.Learned, "learned"),
            "global",
            Status: "proven");
        ValidationPlan componentPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.cs"], ["source"], [ImpactClasses.Direct], dependents: ["plant"]),
            catalog,
            identity,
            LearnedRelationships: [learned]));
        AssertEqual(ValidationPlanTiers.AffectedComponent, componentPlan.Tier, "learned dependent tier");

        ValidationPlan constrainedPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.cs"], ["source"], [ImpactClasses.Direct]),
            catalog,
            identity with { FrameworkVersion = "framework-2" },
            LearnedRelationships: [learned with { FrameworkVersion = "framework-1" }]));
        Assert(!constrainedPlan.Required.Any(requirement => requirement.TestId == "component-test"), "framework constraint blocks stale learning");

        ValidationPlan runtimePlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.cs"], ["source"], [ImpactClasses.DynamicPotential], harmony: true),
            catalog,
            identity));
        AssertEqual(ValidationPlanTiers.AffectedProject, runtimePlan.Tier, "Harmony tier");
        Assert(runtimePlan.Required.Any(requirement => requirement.Kind == ValidationRequirementKinds.Runtime), "Harmony runtime coverage");
        Assert(runtimePlan.RuntimeRequests is { Count: > 0 }, "runtime escalation request emitted");
        Assert(
            runtimePlan.RuntimeRequests!.All(request =>
                request.RequiredEvidence.Contains("artifact-correspondence", StringComparer.Ordinal) &&
                request.ExcludedWork.Contains("unrelated-tests", StringComparer.Ordinal)),
            "runtime request is bounded and explicit");

        var generatedRequirement = new RimDev.Contracts.ValidationRequirement
        {
            RequirementId = "generator.runtime-check",
            Subject = new RimDev.Contracts.EntityReference
            {
                Kind = RimDev.Contracts.EntityReferenceKinds.RuntimeSubject,
                Id = "Plant"
            },
            Assertion = "generated Plant behavior remains valid",
            RuntimeRequired = true,
            Prerequisites = ["map-ready"],
            Producer = "content-generator",
            Source = "generator-output"
        };
        ValidationPlan generatedPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.cs"], ["source"], [ImpactClasses.Direct]),
            catalog,
            identity,
            GeneratedRequirements: [generatedRequirement]));
        AssertEqual(1, generatedPlan.GeneratedRequirements!.Count, "generated requirement retained");
        Assert(generatedPlan.Required.Any(requirement => requirement.Kind == ValidationRequirementKinds.Runtime), "generated requirement selects runtime coverage");
        Assert(generatedPlan.RuntimeRequests!.Any(request => request.Subject.Id == "Plant"), "generated runtime request propagated");

        ValidationPlan reusedPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.xml"], ["plant"], [ImpactClasses.Direct]),
            catalog,
            identity,
            PriorEvidence:
            [
                new ValidationOutcomeEvidence(
                    "evidence-reusable",
                    identity,
                    xmlPlan.PlanFingerprint,
                    "PASS",
                    xmlPlan.RequiredTestIds)
            ]));
        Assert(
            reusedPlan.Required.Any(requirement => requirement.EvidenceIds.Contains("evidence-reusable", StringComparer.Ordinal)),
            "exact current evidence is reused before scheduling");
        Assert(!reusedPlan.TestsNeedingExecution.Contains("static-test", StringComparer.Ordinal), "reused evidence removes duplicate scheduling");

        ValidationPlan savePlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["Source/Plant.cs"], ["save"], [ImpactClasses.Unknown], serialization: true),
            catalog,
            identity));
        Assert(savePlan.Required.Any(requirement => requirement.Kind == ValidationRequirementKinds.Serialization), "save-load coverage");

        ValidationPlan frameworkPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["src/Framework.cs"], ["source"], [ImpactClasses.Framework]),
            catalog,
            identity));
        AssertEqual(ValidationPlanTiers.AffectedFramework, frameworkPlan.Tier, "framework tier");
        Assert(frameworkPlan.Required.Any(requirement => requirement.Kind == ValidationRequirementKinds.FrameworkContract), "framework consumer coverage");

        ValidationPlan unknownPlan = planner.Build(new ValidationPlanRequest(
            graph,
            NewValidationActual(["generated.bin"], [], [ImpactClasses.Unknown]),
            catalog,
            identity,
            FallbackTestIds: ["fallback-one", "fallback-two"]));
        AssertEqual(ValidationPlanTiers.BroaderCanonical, unknownPlan.Tier, "unknown tier");
        AssertEqual(1, unknownPlan.RequiredTestIds.Count, "duplicate recipes are deduplicated");
        AssertEqual("ready", unknownPlan.Status, "unknown impact uses available broader fallback");

        ValidationPlan addedPlan = planner.ApplyAgentRequest(
            xmlPlan,
            new AgentValidationRequest(AdditionalTestIds: ["runtime-test"]),
            catalog);
        Assert(addedPlan.Additional.Any(requirement => requirement.TestId == "runtime-test"), "agent may add validation");
        string requirementId = xmlPlan.Required[0].RequirementId;
        ValidationPlan notRemoved = planner.ApplyAgentRequest(
            xmlPlan,
            new AgentValidationRequest(RemoveRequirementIds: [requirementId]),
            catalog);
        Assert(notRemoved.Required.Any(requirement => requirement.RequirementId == requirementId), "agent cannot remove without proof");
        ValidationPlan removed = planner.ApplyAgentRequest(
            xmlPlan,
            new AgentValidationRequest(
                RemoveRequirementIds: [requirementId],
                Overrides:
                [
                    new ValidationPlanOverride(
                        requirementId,
                        "remove",
                        "review-1",
                        identity,
                        "reviewed explicit override",
                        true)
                ]),
            catalog);
        Assert(!removed.Required.Any(requirement => requirement.RequirementId == requirementId), "accepted override permits removal");

        string serialized = ValidationPlanJson.Serialize(xmlPlan);
        AssertEqual(ValidationPlanSchemas.Current, ValidationPlanJson.Deserialize(serialized).SchemaVersion, "validation plan round trip");
        var currentEvidence = new ValidationOutcomeEvidence(
            "evidence-current",
            identity,
            xmlPlan.PlanFingerprint,
            "PASS",
            xmlPlan.RequiredTestIds);
        Assert(ValidationEvidenceGate.IsCurrent(currentEvidence, identity, xmlPlan.PlanFingerprint), "current evidence accepted");
        Assert(!ValidationEvidenceGate.IsCurrent(
            currentEvidence,
            identity with { SourceRevision = "stale-revision" },
            xmlPlan.PlanFingerprint), "stale evidence rejected");
        Assert(!ValidationEvidenceGate.IsCurrent(
            currentEvidence,
            identity with { WorkspaceIdentity = "other-workspace" },
            xmlPlan.PlanFingerprint), "workspace identity mismatch rejected");

        using var workspace = new TempWorkspace();
        var learningStore = new ImpactLearningStore(Path.Combine(workspace.Root, "learning.jsonl"));
        var learningService = new ImpactLearningService(learningStore);
        ImpactLearningObservation observation = NewLearningObservation(
            identity,
            project: "ProjectA",
            globalCandidate: true,
            evidenceId: "evidence-global");
        ImpactLearningResult learnedResult = learningService.Observe(observation);
        Assert(learnedResult.PromotedGlobal, "proven causal relationship promoted globally");
        Assert(learningStore.Read(project: "ProjectB").Any(), "global relationship reused across projects");
        Assert(!learningService.Observe(observation with { CausalAttribution = false }).Learned, "unattributed outcome is not learned");
        learningStore.AppendOverride(new ImpactLearningOverride(
            observation.FromIdentity,
            observation.ToIdentity,
            observation.RelationshipKind,
            "ProjectA",
            true,
            "project-specific exclusion",
            identity,
            "override-evidence"));
        Assert(!learningStore.Read(project: "ProjectA").Any(), "project override excludes relationship");
        Assert(learningStore.Read(project: "ProjectB").Any(), "project override does not hide global relationship elsewhere");
    }

    private static ActualImpact NewValidationActual(
        IReadOnlyList<string> files,
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<string> classes,
        bool harmony = false,
        bool serialization = false,
        IReadOnlyList<string>? dependents = null) =>
        new(
            files,
            nodeIds,
            dependents ?? [],
            classes,
            [],
            harmony,
            serialization,
            false,
            []);

    private static ImpactLearningObservation NewLearningObservation(
        ValidationSourceIdentity identity,
        string project,
        bool globalCandidate,
        string evidenceId) =>
        new(
            "Plant",
            "component-test",
            "direct-dependent",
            ImpactClasses.Direct,
            new ImpactProvenance("rimerror", ImpactEvidenceClasses.Learned, evidenceId),
            identity with { Project = project },
            evidenceId,
            true,
            DeterministicRelationship: true,
            RimErrorAttribution: true,
            Project: project,
            GlobalCandidate: globalCandidate);

    private static ImpactGraph TestImpactGraph()
    {
        var identity = new ImpactGraphIdentity(
            "workspace",
            "workspace-generation",
            "index-generation",
            Project: "Frontier");
        var nodes = new ImpactNode[]
        {
            new("source", "source_file", "Source/Plant.cs", "Plant", "Source/Plant.cs"),
            new("plant", "def", "ThingDef/Plant", "Plant", "Source/Plant.xml"),
            new("save", "csharp_member", "Plant.ExposeData", "ExposeData", "Source/Plant.cs"),
            new("patch", "harmony_patch", "PlantPatch", "PlantPatch", "Source/Plant.cs")
        };
        var edges = new ImpactEdge[]
        {
            new(
                "def-edge",
                "source",
                "plant",
                ImpactRelationshipKinds.ComponentDef,
                ImpactClasses.Direct,
                new ImpactProvenance("fixture", ImpactEvidenceClasses.Deterministic, "def-edge")),
            new(
                "harmony-edge",
                "patch",
                "source",
                ImpactRelationshipKinds.HarmonyTarget,
                ImpactClasses.DynamicPotential,
                new ImpactProvenance("fixture", ImpactEvidenceClasses.Uncertain, "harmony-edge")),
            new(
                "save-edge",
                "save",
                "plant",
                ImpactRelationshipKinds.SerializationConcern,
                ImpactClasses.Unknown,
                new ImpactProvenance("fixture", ImpactEvidenceClasses.Inferred, "save-edge"))
        };
        return new ImpactGraph(
            ImpactGraphSchemas.Current,
            identity,
            nodes,
            edges,
            new ImpactGraphBuildMetrics(1, 3, true, nodes.Length, edges.Length));
    }
    private static void ContentIntelligenceContractsAndCapture()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/CompWidget.cs", "public sealed class CompWidget {}\n");
        var context = new RimContextService();
        context.Index(new RimContextIndexRequest(workspace.Root));
        string storePath = Path.Combine(workspace.Root, "precedent.jsonl");
        var service = new ContentIntelligenceService(new ContentIntelligenceStore(storePath));
        var generatedRequirement = new RimDev.Contracts.ValidationRequirement
        {
            RequirementId = "content.generated-check",
            Subject = new RimDev.Contracts.EntityReference
            {
                Kind = RimDev.Contracts.EntityReferenceKinds.ContentBlueprint,
                Id = "CompWidget"
            },
            Assertion = "generated artifact has a valid ThingDef reference",
            Producer = "content-generator"
        };
        ContentBlueprint blueprint = service.CaptureBlueprint(
            new ContentBlueprintCaptureRequest(
                workspace.Root,
                null,
                new ContentBlueprintIntent(
                    "component",
                    "supports widget interaction",
                    new Dictionary<string, string> { ["count"] = "1" },
                    ProjectConstraints: ["no framework dependency"]),
                ["Source/CompWidget.cs"],
                Repository: "FixtureMod",
                Project: "FixtureProject",
                AgentId: "agent-1",
                RunId: "run-1",
                LogicalAgentId: "logical-agent-1",
                ValidationRequirements: [generatedRequirement]));
        AssertEqual(ContentIntelligenceSchemas.Blueprint, blueprint.SchemaVersion, "blueprint schema");
        AssertEqual("FixtureMod", blueprint.Metadata.Repository, "derived repository identity");
        Assert(blueprint.Metadata.SourceFiles?.Contains("Source/CompWidget.cs") == true, "source file derivation");
        Assert(blueprint.Metadata.SourceIdentity?.SourceFingerprint is not null, "source fingerprint derivation");
        AssertEqual(1, blueprint.ValidationRequirements!.Count, "content generator requirement retained");
        string originalFingerprint = blueprint.Metadata.SourceIdentity!.SourceFingerprint!;
        workspace.Write("Source/CompWidget.cs", "public sealed class CompWidget { public int Value => 2; }\n");
        ContentBlueprint changed = service.CaptureBlueprint(
            new ContentBlueprintCaptureRequest(
                workspace.Root,
                null,
                blueprint.Intent,
                ["Source/CompWidget.cs"],
                Repository: "FixtureMod",
                Project: "FixtureProject"));
        Assert(changed.Metadata.SourceIdentity?.SourceFingerprint != originalFingerprint,
            "changed source uses current content instead of stale index hash");
        Assert(blueprint.Metadata.SourceIdentity?.RimWorldVersion is null, "unknown RimWorld version remains null");
        using (JsonDocument serialized = JsonDocument.Parse(ContentIntelligenceJson.Serialize(blueprint)))
        {
            AssertEqual(ContentIntelligenceSchemas.Blueprint, serialized.RootElement.GetProperty("schemaVersion").GetString(), "serialized blueprint schema");
            AssertEqual(JsonValueKind.Null, serialized.RootElement.GetProperty("projectOverride").ValueKind, "unknown override is explicit null");
        }

        ContentEvidence evidence = service.CaptureEvidence(
            new ContentEvidenceCaptureRequest(
                blueprint.BlueprintId,
                blueprint.Metadata.SourceIdentity!,
                new ContentEvidenceOutcome(
                    StaticReferenceValidation: "PASS",
                    Build: "PASS",
                    AffectedTests: "PASS",
                    Final: "PASS"),
                EvidenceReferences: ["test-evidence-1"]));
        AssertEqual(ContentIntelligenceSchemas.Evidence, evidence.SchemaVersion, "evidence schema");
        ContentBlueprint updated = service.Store.GetBlueprint(blueprint.BlueprintId)!;
        Assert(updated.Metadata.ValidationEvidence?.Contains(evidence.EvidenceId) == true,
            "evidence capture updates blueprint validation references");
        AssertEqual("logical-agent-1", updated.Metadata.LogicalAgentId,
            "persistent logical agent identity is captured");
        File.AppendAllText(
            storePath,
            """{"schemaVersion":"content-intelligence-store/v2","recordType":"blueprint","value":{}}""" +
            Environment.NewLine);
        AssertEqual(2, service.Store.Snapshot().Blueprints.Count,
            "future store envelope is ignored without hiding known records");
        AssertThrows<InvalidOperationException>(
            () => ContentIntelligenceJson.ValidateBlueprint(blueprint with { SchemaVersion = "content-blueprint/v2" }),
            "unsupported blueprint version must fail");
        AssertThrows<InvalidOperationException>(
            () => ContentIntelligenceJson.ValidateBlueprint(
                blueprint with { Intent = blueprint.Intent with { ReuseSource = "unsupported" } }),
            "unsupported reuse source must fail");
    }

    private static void ContentPrecedentQueryOrderingAndPolicies()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Data/Defs/VanillaExample.xml",
            "<Defs><ThingDef><defName>VanillaExample</defName></ThingDef></Defs>");
        new RimContextService().Index(new RimContextIndexRequest(workspace.Root));
        string storePath = Path.Combine(workspace.Root, "precedent.jsonl");
        var service = new ContentIntelligenceService(new ContentIntelligenceStore(storePath));
        ContentReuseDecision indexedVanillaDecision = service.SelectReuse(
            new ContentReuseRequest(
                ContentKind: "ThingDef",
                RootPath: workspace.Root));
        AssertEqual(
            ContentReuseSources.VanillaReference,
            indexedVanillaDecision.Source,
            "automatic reuse selection includes indexed vanilla references");
        ContentQueryResult indexed = service.Query(
            new ContentQueryRequest(ContentKind: "ThingDef", RootPath: workspace.Root));
        Assert(indexed.Results.Any(result =>
            result.ReuseSource == ContentReuseSources.VanillaReference &&
            result.EntityIdentifiers?.Contains("ThingDef/VanillaExample") == true),
            "indexed vanilla reference is queried dynamically");
        var blueprints = new[]
        {
            NewContentBlueprint("novel", ContentReuseSources.Novel, "ProjectA"),
            NewContentBlueprint("vanilla", ContentReuseSources.VanillaReference, null),
            NewContentBlueprint("precedent", ContentReuseSources.Precedent, "ProjectA"),
            NewContentBlueprint("rimcontent", ContentReuseSources.RimContent, null)
        };
        foreach (ContentBlueprint blueprint in blueprints)
        {
            service.Store.SaveBlueprint(blueprint);
            service.CaptureEvidence(
                new ContentEvidenceCaptureRequest(
                    blueprint.BlueprintId,
                    blueprint.Metadata.SourceIdentity!,
                    new ContentEvidenceOutcome(Final: "PASS")));
        }

        ContentQueryResult ranked = service.Query(new ContentQueryRequest(ContentKind: "ThingDef"));
        AssertEqual(4, ranked.Results.Count, "all proven precedent sources returned");
        AssertEqual(ContentReuseSources.RimContent, ranked.Results[0].ReuseSource, "RimContent trust order");
        AssertEqual(ContentReuseSources.Precedent, ranked.Results[1].ReuseSource, "precedent trust order");
        AssertEqual(ContentReuseSources.VanillaReference, ranked.Results[2].ReuseSource, "vanilla trust order");
        service.SetProjectPolicy(new ContentPrecedentPolicy(
            ranked.Results[1].BlueprintId,
            Project: "ProjectB",
            Excluded: false,
            Constraints: ["requires ProjectB capability"]));
        ContentQueryResult constrained = service.Query(
            new ContentQueryRequest(ContentKind: "ThingDef", Project: "ProjectB"));
        Assert(constrained.Results
            .First(result => result.BlueprintId == ranked.Results[1].BlueprintId)
            .Constraints?.Contains("requires ProjectB capability") == true,
            "project policy constraint is preserved");
        AssertEqual(ContentReuseSources.Novel, ranked.Results[3].ReuseSource, "novel trust order");

        service.SetProjectPolicy(new ContentPrecedentPolicy(
            ranked.Results[1].BlueprintId,
            Project: "ProjectB",
            Excluded: true));
        ContentQueryResult projectResults = service.Query(
            new ContentQueryRequest(ContentKind: "ThingDef", Project: "ProjectB"));
        Assert(!projectResults.Results.Any(result => result.BlueprintId == ranked.Results[1].BlueprintId), "project exclusion");
        Assert(projectResults.Results.Count > 0, "global precedents remain available to project");
    }

    private static void ContentIdentityAndBoundedOutput()
    {
        var expected = new ContentSourceIdentity(
            "repo",
            "project",
            "commit-1",
            "source-1",
            "workspace-1",
            "tool-1",
            null);
        Assert(ContentIntelligenceService.SourceIdentityMatches(
            expected,
            expected with { RimWorldVersion = "1.6" }),
            "unknown expected version is compatible");
        Assert(!ContentIntelligenceService.SourceIdentityMatches(
            expected,
            expected with { SourceFingerprint = "source-2" }),
            "source mismatch is rejected");

        using var workspace = new TempWorkspace();
        string storePath = Path.Combine(workspace.Root, "precedent.jsonl");
        var service = new ContentIntelligenceService(new ContentIntelligenceStore(storePath));
        for (int index = 0; index < 12; index++)
        {
            ContentBlueprint blueprint = NewContentBlueprint(
                "item-" + index.ToString(CultureInfo.InvariantCulture),
                ContentReuseSources.Precedent,
                null);
            service.Store.SaveBlueprint(blueprint);
            service.CaptureEvidence(new ContentEvidenceCaptureRequest(
                blueprint.BlueprintId,
                blueprint.Metadata.SourceIdentity!,
                new ContentEvidenceOutcome(Final: "PASS")));
        }

        ContentBlueprint unknownIdentity = NewContentBlueprint(
            "unknown-identity",
            ContentReuseSources.Precedent,
            null) with
        {
            Metadata = NewContentBlueprint(
                "unknown-identity",
                ContentReuseSources.Precedent,
                null).Metadata with
            {
                SourceIdentity = null
            }
        };
        service.Store.SaveBlueprint(unknownIdentity);
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            unknownIdentity.BlueprintId,
            new ContentSourceIdentity(Repository: "repo"),
            new ContentEvidenceOutcome(Final: "PASS")));
        Assert(!service.Query(new ContentQueryRequest(ContentKind: "ThingDef")).Results
            .Any(result => result.BlueprintId == unknownIdentity.BlueprintId),
            "unknown source identity cannot establish reusable evidence");

        ContentBlueprint staleBlueprint = NewContentBlueprint(
            "stale",
            ContentReuseSources.Precedent,
            null);
        service.Store.SaveBlueprint(staleBlueprint);
        service.CaptureEvidence(
            new ContentEvidenceCaptureRequest(
                staleBlueprint.BlueprintId,
                staleBlueprint.Metadata.SourceIdentity! with { SourceFingerprint = "different-source" },
                new ContentEvidenceOutcome(Final: "PASS")));
        Assert(service.Store.GetBlueprint(staleBlueprint.BlueprintId)?.Metadata.ValidationEvidence is null,
            "stale evidence is not attached to blueprint metadata");
        ContentQueryResult staleQuery = service.Query(
            new ContentQueryRequest(ContentKind: "ThingDef"));
        Assert(!staleQuery.Results.Any(result => result.BlueprintId == staleBlueprint.BlueprintId), "stale evidence must not be reusable");

        ContentQueryResult limited = service.Query(
            new ContentQueryRequest(ContentKind: "ThingDef", Limit: 100, MaxBytes: 512));
        Assert(limited.Results.Count < 12, "byte budget truncates results");
        Assert(Encoding.UTF8.GetByteCount(ContentIntelligenceJson.Serialize(limited)) <= 512, "query output respects byte budget");

        CliResult cli = Run("content", "--store", storePath, "--kind", "ThingDef", "--limit", "1", "--include-failures", "--json");
        AssertEqual(0, cli.ExitCode, "content command remains compatible with direct CLI");
        using JsonDocument document = ParseJson(cli.Stdout);
        AssertEqual("content", document.RootElement.GetProperty("command").GetString(), "content command name");
    }

    private static void ContentPhase2QualificationReusePromotionAndQuarantine()
    {
        using var workspace = new TempWorkspace();
        string storePath = Path.Combine(workspace.Root, "phase2.jsonl");
        var store = new ContentIntelligenceStore(storePath);
        var service = new ContentIntelligenceService(store);
        ContentBlueprint first = NewPhase2Blueprint("first", "ProjectA", "run-1");
        ContentBlueprint second = NewPhase2Blueprint("second", "ProjectB", "run-2");
        store.SaveBlueprint(first);
        store.SaveBlueprint(second);
        ContentStructuralFingerprint firstShape = ContentStructuralFingerprinting.Compute(first);
        AssertEqual(firstShape.Fingerprint, ContentStructuralFingerprinting.Compute(second).Fingerprint, "repeated implementations share a structural fingerprint");

        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            first.BlueprintId,
            first.Metadata.SourceIdentity!,
            new ContentEvidenceOutcome("PASS", "PASS", "PASS", Final: "PASS"),
            CapturedAtUtc: "2025-01-01T00:00:00Z"));
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            second.BlueprintId,
            second.Metadata.SourceIdentity!,
            new ContentEvidenceOutcome("PASS", "PASS", "PASS", Final: "PASS"),
            CapturedAtUtc: "2025-01-02T00:00:00Z"));

        ContentAnalysisResult analysis = service.Analyze(new ContentAnalysisRequest("ThingDef", "weapon"));
        ContentPrecedentCandidate candidate = analysis.Candidates.Single();
        Assert(candidate.Qualification.Qualified, "independent successful implementations qualify automatically");
        AssertEqual(2, candidate.Qualification.DistinctProjects, "qualification counts distinct projects");
        AssertEqual(2, candidate.Qualification.DistinctRuns, "qualification counts distinct runs");
        AssertEqual(0, candidate.Qualification.FailureCount, "qualified candidate has no failed implementations");
        ContentArchetype archetype = store.Snapshot().Archetypes.Single(item => item.Status == "active");
        AssertEqual(ContentPhase2Schemas.Archetype, archetype.SchemaVersion, "automatic promotion stores a versioned archetype");
        Assert(archetype.Templates is not null && archetype.Defaults is not null && archetype.Examples is not null && archetype.FrameworkRequirements is not null, "promotion stores data-only templates, defaults, examples, and framework requirements");
        ContentBlueprint third = NewPhase2Blueprint("third", "ProjectC", "run-3");
        store.SaveBlueprint(third);
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            third.BlueprintId,
            third.Metadata.SourceIdentity!,
            new ContentEvidenceOutcome("PASS", "PASS", "PASS", Final: "PASS"),
            CapturedAtUtc: "2025-01-02T12:00:00Z"));
        AssertEqual(2, store.Snapshot().Archetypes.Count(item => item.ArchetypeId == archetype.ArchetypeId && item.Status == "active"), "new supporting evidence creates a new active archetype version");


        ContentReuseDecision rimContent = service.SelectReuse(new ContentReuseRequest("ThingDef", "weapon", Project: "ProjectC"));
        AssertEqual(ContentReuseSources.RimContent, rimContent.Source, "RimContent is the first reuse source");
        service.SetProjectPolicy(new ContentPrecedentPolicy(archetype.ArchetypeId, "ProjectC", Excluded: true));
        ContentReuseDecision excluded = service.SelectReuse(new ContentReuseRequest("ThingDef", "weapon", Project: "ProjectC"));
        Assert(excluded.Source != ContentReuseSources.RimContent, "project exclusion skips the active archetype");

        ContentBlueprint staleReuse = NewPhase2Blueprint(
            "stale-reuse",
            "ProjectD",
            "run-stale",
            reuseDecision: rimContent);
        store.SaveBlueprint(staleReuse);
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            staleReuse.BlueprintId,
            staleReuse.Metadata.SourceIdentity! with { SourceFingerprint = "stale-source" },
            new ContentEvidenceOutcome("FAIL", "FAIL", "FAIL", Final: "FAIL"),
            CapturedAtUtc: "2025-01-02T18:00:00Z"));
        AssertEqual(2, store.Snapshot().Archetypes.Count(item => item.ArchetypeId == archetype.ArchetypeId && item.Status == "active"), "stale reuse evidence cannot quarantine an archetype");
        ContentBlueprint regression = NewPhase2Blueprint(
            "regression",
            "ProjectD",
            "run-4",
            reuseDecision: rimContent);
        store.SaveBlueprint(regression);
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            regression.BlueprintId,
            regression.Metadata.SourceIdentity!,
            new ContentEvidenceOutcome("FAIL", "FAIL", "FAIL", Final: "FAIL"),
            CapturedAtUtc: "2025-01-03T00:00:00Z"));
        ContentArchetype quarantined = store.Snapshot().Archetypes
            .Single(item => item.ArchetypeId == archetype.ArchetypeId && item.Status == "quarantined");
        AssertEqual(2, quarantined.Version, "attributable reuse failure quarantines the current archetype version");
        AssertEqual("ATTRIBUTABLE_REUSE_FAILURE", quarantined.QuarantineReason, "quarantine retains the exact failure reason");
        ContentArchetype rollback = store.Snapshot().Archetypes
            .Single(item => item.ArchetypeId == archetype.ArchetypeId && item.Status == "active");
        AssertEqual(1, rollback.Version, "failed reuse falls back to the last proven archetype version");
        Assert(store.Snapshot().Usages.Any(usage => usage.ArchetypeId == archetype.ArchetypeId && usage.Succeeded == false), "quarantine retains usage evidence");
        AssertEqual(ContentReuseSources.RimContent,
            service.SelectReuse(new ContentReuseRequest("ThingDef", "weapon", Project: "ProjectD")).Source,
            "last proven archetype remains reusable after a newer version is quarantined");

        service.SetProjectPolicy(new ContentPrecedentPolicy(archetype.ArchetypeId, "ProjectD", Excluded: true));

        ContentBlueprint vanilla = NewPhase2Blueprint("vanilla", null, "run-5", reuseSource: ContentReuseSources.VanillaReference);
        store.SaveBlueprint(vanilla);
        service.CaptureEvidence(new ContentEvidenceCaptureRequest(
            vanilla.BlueprintId,
            vanilla.Metadata.SourceIdentity!,
            new ContentEvidenceOutcome("PASS", "PASS", "PASS", Final: "PASS"),
            CapturedAtUtc: "2025-01-04T00:00:00Z"));
        ContentQueryResult storedVanilla = store.Query(new ContentQueryRequest(ContentKind: "ThingDef", GameplayRole: "weapon", Project: "ProjectD"));
        Assert(storedVanilla.Results.Any(item => item.BlueprintId == vanilla.BlueprintId && item.ReuseSource == ContentReuseSources.VanillaReference), "stored vanilla reference remains queryable");
        ContentReuseDecision vanillaDecision = service.SelectReuse(new ContentReuseRequest("ThingDef", "weapon", Project: "ProjectD"));
        AssertEqual(ContentReuseSources.VanillaReference, vanillaDecision.Source, "vanilla is selected after failed promotion and project exclusion");

        using var novelWorkspace = new TempWorkspace();
        var novelService = new ContentIntelligenceService(
            new ContentIntelligenceStore(Path.Combine(novelWorkspace.Root, "novel.jsonl")));
        AssertEqual(ContentReuseSources.Novel,
            novelService.SelectReuse(new ContentReuseRequest("ThingDef", "weapon")).Source,
            "novel is the closed fallback when no evidence exists");

        AssertEqual(ContentReuseSources.Novel,
            service.SelectReuse(new ContentReuseRequest(
                "ThingDef",
                "weapon",
                new Dictionary<string, string> { ["count"] = "2" },
                ["Core"],
                "ProjectD")).Source,
            "reuse does not cross structural design shapes");
        ContentBlueprint differentShape = NewPhase2Blueprint("different", "ProjectE", "run-5", designValue: "2");
        Assert(firstShape.Fingerprint != ContentStructuralFingerprinting.Compute(differentShape).Fingerprint, "different design shape creates a distinct cluster");
        ContentEvidence staleEvidence = new(
            ContentIntelligenceSchemas.Evidence,
            "stale-evidence",
            differentShape.BlueprintId,
            differentShape.Metadata.SourceIdentity! with { SourceFingerprint = "stale-source" },
            new ContentEvidenceOutcome("PASS", "PASS", "PASS", Final: "PASS"),
            CapturedAtUtc: "2025-01-05T00:00:00Z");
        ContentQualificationResult stale = ContentQualificationEngine.Evaluate(
            ContentStructuralFingerprinting.Compute(differentShape),
            [differentShape],
            [staleEvidence]);
        Assert(!stale.Qualified && stale.StaleEvidenceCount == 1, "stale evidence cannot qualify a precedent");

        ContentArchetype replayFailureArchetype = archetype with
        {
            StructuralFingerprint = ContentStructuralFingerprinting.Compute(differentShape)
        };
        ContentReplayResult replayFailure = ContentHistoricalReplay.Replay(replayFailureArchetype, [first]);
        Assert(!replayFailure.Passed && replayFailure.Failures.Count > 0, "historical replay fails closed on structural mismatch");

        string oldBlueprintJson = """{"schemaVersion":"content-blueprint/v1","blueprintId":"legacy","intent":{"contentKind":"ThingDef","gameplayRole":"weapon"},"metadata":{"project":"Legacy"}}""";
        ContentBlueprint legacy = ContentIntelligenceJson.Deserialize<ContentBlueprint>(oldBlueprintJson);
        Assert(legacy.ReuseDecision is null, "Phase 1 blueprints without Phase 2 reuse data remain readable");
    }

    private static ContentBlueprint NewPhase2Blueprint(
        string suffix,
        string? project,
        string run,
        string designValue = "1",
        string reuseSource = ContentReuseSources.Precedent,
        ContentReuseDecision? reuseDecision = null)
    {
        return new ContentBlueprint(
            ContentIntelligenceSchemas.Blueprint,
            "phase2-" + suffix,
            new ContentBlueprintIntent(
                "ThingDef",
                "weapon",
                new Dictionary<string, string> { ["count"] = designValue },
                FrameworkRequirements: ["Core"],
                ValidationExpectations: ["static", "build", "affected"],
                ReuseSource: reuseSource),
            new ContentBlueprintMetadata(
                Repository: "repo-" + suffix,
                Project: project,
                RunId: run,
                SourceFiles: ["Defs/" + suffix + ".xml"],
                EntityIdentifiers: ["ThingDef/" + suffix],
                Dependencies: ["ThingDef:dependency"],
                FrameworkDependencies: ["Core:1"],
                SourceIdentity: new ContentSourceIdentity(
                    "repo-" + suffix,
                    project,
                    "commit-" + suffix,
                    "source-" + suffix,
                    "workspace-" + suffix,
                    "tool-1")),
            ReuseDecision: reuseDecision);
    }

    private static ContentBlueprint NewContentBlueprint(
        string suffix,
        string reuseSource,
        string? project)
    {
        return new ContentBlueprint(
            ContentIntelligenceSchemas.Blueprint,
            "blueprint-" + suffix,
            new ContentBlueprintIntent("ThingDef", "weapon", ReuseSource: reuseSource),
            new ContentBlueprintMetadata(
                Repository: "repo-" + suffix,
                Project: project,
                SourceFiles: ["Defs/" + suffix + ".xml"],
                SourceIdentity: new ContentSourceIdentity(
                    "repo-" + suffix,
                    project,
                    "commit-" + suffix,
                    "source-" + suffix,
                    "workspace-" + suffix,
                    "tool-1")));
    }


    private static void CliStartupAndVersion()
    {
        var result = Run("version", "--json");
        AssertEqual(0, result.ExitCode, "version exit code");
        using var document = ParseJson(result.Stdout);
        AssertEqual("ok", document.RootElement.GetProperty("status").GetString(), "version status");
        AssertEqual("version", document.RootElement.GetProperty("command").GetString(), "version command");
        AssertEqual(IndexConstants.ToolVersion, document.RootElement.GetProperty("data").GetProperty("toolVersion").GetString(), "tool version");
    }

    private static void JsonOnlyStdout()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/A.cs", "class A {}\n");
        var result = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, result.ExitCode, "index exit code");
        using var indexJson = ParseJson(result.Stdout);
        AssertEqual("ok", indexJson.RootElement.GetProperty("status").GetString(), "index status");
        var data = indexJson.RootElement.GetProperty("data");
        var files = data.GetProperty("files");
        AssertEqual(1, files.GetProperty("scanned").GetInt32(), "index scanned count");
        AssertEqual(1, files.GetProperty("added").GetInt32(), "index added count");
        AssertEqual(JsonValueKind.Number, data.GetProperty("duration_ms").ValueKind, "index duration");
        Assert(result.Stdout.TrimStart().StartsWith("{", StringComparison.Ordinal), "stdout must start with JSON");
        Assert(!result.Stdout.Contains("[info]", StringComparison.Ordinal), "logging must not leak into stdout");
        Assert(result.Stderr.Contains("[info]", StringComparison.Ordinal), "index diagnostics must be on stderr");

        var summary = Run("summary", "--root", workspace.Root, "--json");
        AssertEqual(0, summary.ExitCode, "summary exit code");
        using var summaryJson = ParseJson(summary.Stdout);
        AssertEqual("summary", summaryJson.RootElement.GetProperty("command").GetString(), "summary command");
    }

    private static void InvalidInput()
    {
        using var workspace = NewIndexedWorkspace();
        AssertEqual(0, Run("index", "--root", workspace.Root, "--json").ExitCode, "invalid-input fixture index");
        var unknown = Run("does-not-exist", "--json");
        AssertEqual(2, unknown.ExitCode, "unknown command exit code");
        using (var document = ParseJson(unknown.Stdout))
        {
            AssertEqual(ErrorCodes.InvalidArgument, document.RootElement.GetProperty("code").GetString(), "unknown command code");
        }

        var missingSelector = Run("find", "--json");
        AssertEqual(2, missingSelector.ExitCode, "missing selector exit code");
        using (var document = ParseJson(missingSelector.Stdout))
        {
            AssertEqual(ErrorCodes.InvalidArgument, document.RootElement.GetProperty("code").GetString(), "missing selector code");
        }

        var tooLarge = Run("find", "ThingDef", "--limit", "101", "--json");
        AssertEqual(2, tooLarge.ExitCode, "limit exit code");
        using var limitJson = ParseJson(tooLarge.Stdout);
        AssertEqual(ErrorCodes.LimitExceeded, limitJson.RootElement.GetProperty("code").GetString(), "limit code");

        var conflictingModes = Run("version", "--json", "--human");
        AssertEqual(2, conflictingModes.ExitCode, "conflicting output modes exit code");
        using var conflictingModesJson = ParseJson(conflictingModes.Stdout);
        AssertEqual(ErrorCodes.InvalidArgument, conflictingModesJson.RootElement.GetProperty("code").GetString(), "conflicting output modes code");

        using var notFound = ParseJson(Run("definition", "ThingDef/Missing", "--root", workspace.Root, "--json").Stdout);
        AssertEqual(ErrorCodes.NotFound, notFound.RootElement.GetProperty("code").GetString(), "not found code");
        AssertEqual("ThingDef/Missing not found", notFound.RootElement.GetProperty("message").GetString(), "not found message");
        Assert(!notFound.RootElement.TryGetProperty("error", out _), "error should not be nested");
    }

    private static void TypedCoreFacade()
    {
        using var workspace = NewXmlWorkspace();
        var service = new RimContextService();
        var index = service.Index(new RimContextIndexRequest(workspace.Root));
        Assert(index.Statistics.Scanned > 0, "Core facade indexed fixture files");

        var summary = service.Summary(new RimContextSummaryRequest(workspace.Root));
        Assert(summary.Defs >= 2, "Core facade summary contains Defs");

        var affected = service.Affected(new RimContextAffectedRequest(
            ["Mods/TestMod/Defs/Weapons.xml"],
            workspace.Root));
        Assert(
            affected.Direct.Any(item => item.Name == "ThingDef/MyWeapon"),
            "Core facade affected result contains the changed Def");
    }

    private static void ContextBundleSchemaAndSerialization()
    {
        var bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [CompleteContextSnapshot()]);

        AssertEqual("complete", bundle.SnapshotStatus, "complete context snapshot status");
        AssertEqual(false, bundle.Stale, "complete context snapshot stale state");
        string compact = RimContextBundleJson.Serialize(bundle);
        string compactAgain = RimContextBundleJson.Serialize(bundle);
        string verbose = RimContextBundleJson.Serialize(bundle, verbose: true);
        AssertEqual(compact, compactAgain, "context compact serialization determinism");
        Assert(compact.Length < verbose.Length, "verbose context output should include formatting");
        Assert(verbose.Contains(Environment.NewLine, StringComparison.Ordinal), "verbose context output should be indented");

        using var document = ParseJson(compact);
        AssertEqual(RimContextBundleSchema.Current, document.RootElement.GetProperty("schemaVersion").GetString(), "context bundle schema");
        AssertEqual("complete", document.RootElement.GetProperty("snapshotStatus").GetString(), "serialized context status");
        Assert(document.RootElement.GetProperty("ownership").GetArrayLength() > 0, "context ownership contract");

        using var cliDocument = ParseJson(Run("context", "--root", Path.GetTempPath(), "--json").Stdout);
        AssertEqual(RimContextBundleSchema.Current, cliDocument.RootElement.GetProperty("schemaVersion").GetString(), "direct context command schema");
        Assert(cliDocument.RootElement.TryGetProperty("repository", out _), "direct context command repository section");
        AssertEqual(
            RimContextBundleStatuses.Unknown,
            cliDocument.RootElement.GetProperty("environment").GetProperty("value")
                .GetProperty("rimWorldVersion").GetString(),
            "direct static context reports unknown RimWorld version explicitly");

        var limited = Run("context", "--root", Path.GetTempPath(), "--max-bytes", "256", "--json");
        Assert(Encoding.UTF8.GetByteCount(limited.Stdout.TrimEnd()) <= 256, "context max-bytes output should be bounded");
        using var limitedDocument = ParseJson(limited.Stdout);
        AssertEqual(true, limitedDocument.RootElement.GetProperty("truncated").GetBoolean(), "context max-bytes truncation");
    }

    private static void ContextBundleUnavailableSections()
    {
        var bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            Array.Empty<RimContextProviderSnapshot>());

        AssertEqual("partial", bundle.SnapshotStatus, "unavailable context snapshot status");
        AssertEqual(false, bundle.Stale, "unavailable context snapshot is not stale");
        AssertEqual("unknown", bundle.Repository.Status, "unavailable repository status");
        AssertEqual("CONTEXT_PROVIDER_UNAVAILABLE", bundle.Repository.ReasonCode, "unavailable repository reason");
        Assert(bundle.Repository.Value is null, "unavailable repository has no fabricated value");
        AssertEqual("unknown", bundle.Runtime.Status, "unavailable runtime status");
        AssertEqual("unknown", bundle.Efficiency.Status, "unavailable efficiency status");
    }

    private static void ContextBundleGeneratedRepositoryState()
    {
        var repository = new RimContextRepositoryState
        {
            Identity = "git:fixture",
            Dirty = true,
            ChangedFiles =
            [
                new RimContextChangedFile { Path = "Source/Changed.cs", Status = " M", Category = "source" }
            ],
            GeneratedFiles =
            [
                new RimContextChangedFile { Path = "obj/Debug/generated.dll", Status = "??", Category = "generated", Untracked = true }
            ]
        };
        var snapshot = CompleteContextSnapshot() with
        {
            Repository = AvailableSection(repository)
        };
        var bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [snapshot]);

        using var document = ParseJson(RimContextBundleJson.Serialize(bundle));
        var value = document.RootElement.GetProperty("repository").GetProperty("value");
        AssertEqual(1, value.GetProperty("changedFiles").GetArrayLength(), "meaningful changed repository files");
        AssertEqual(1, value.GetProperty("generatedFiles").GetArrayLength(), "generated repository files are explicit");
        AssertEqual("obj/Debug/generated.dll", value.GetProperty("generatedFiles")[0].GetProperty("path").GetString(), "generated file path");
    }

    private static void ContextBundleAgentSummaryAndCorrespondence()
    {
        var snapshot = CompleteContextSnapshot() with
        {
            Repository = AvailableSection(new RimContextRepositoryState
            {
                Identity = "git:fixture",
                Dirty = false,
                SourceFingerprint = "source-1",
                ChangedFiles = []
            }),
            Deployment = AvailableSection(new RimContextDeploymentState
            {
                SourceFingerprint = "source-1",
                BuildArtifactFingerprint = "built-1",
                DeployedArtifactFingerprint = "deployed-1",
                Correspondence = "mismatch",
                BuildDeploymentCorrespondence = "mismatch"
            }),
            Runtime = AvailableSection(new RimContextRuntimeState
            {
                Generation = 7,
                RuntimeArtifactFingerprint = "deployed-1",
                BridgeStatus = "ready"
            }),
            Testing = AvailableSection(new RimContextTestingState
            {
                AvailableSuites = ["smoke"],
                AvailableTests = ["smoke.test"],
                LatestEvidence = [new RimContextEvidenceReference { Id = "proof-1", Status = "available" }]
            })
        };
        RimContextBundle bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [snapshot]);

        AssertEqual("action-required", bundle.AgentSummary.Status, "mismatch summary status");
        Assert(bundle.AgentSummary.ActionRequired.Count > 0, "mismatch summary includes an action");
        Assert(bundle.AgentSummary.ReusableEvidence.SequenceEqual(["proof-1"]), "summary retains reusable evidence");
        AssertEqual("mismatch", bundle.AgentSummary.DeploymentCorrespondence, "summary retains deployment mismatch");
        using JsonDocument document = ParseJson(RimContextBundleJson.Serialize(bundle));
        Assert(document.RootElement.TryGetProperty("agentSummary", out JsonElement summary), "agent summary is canonical JSON");
        Assert(summary.GetProperty("status").GetString() == "action-required", "serialized summary status");
    }

    private static void ContextBundleStaleRuntimeAndFailureTaxonomy()
    {
        DateTimeOffset old = FixedTime.AddMinutes(-10);
        var snapshot = CompleteContextSnapshot(old) with
        {
            Runtime = AvailableSection(new RimContextRuntimeState
            {
                Generation = 3,
                CurrentGenerationTrust = "stale",
                FailureCode = "RUNTIME_GENERATION_STALE"
            }),
            Failures =
            [
                new RimContextFailure
                {
                    SignatureCode = "DEVBRIDGE_LEASE_CONTENDED",
                    OriginatingComponent = "DevBridge2",
                    Classification = "infrastructure",
                    RootCause = "Another owner holds the lease.",
                    RecommendedAction = "wait-for-lease",
                    RetryAppropriate = true,
                    RetryAfterStateChange = true,
                    InfrastructureOnly = true,
                    EvidenceInvalidationEffect = "reuse-invalidated",
                    ObservedAtUtc = old
                }
            ]
        };
        RimContextBundle bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [snapshot]);

        AssertEqual("stale", bundle.Runtime.Status, "runtime stale status");
        Assert(bundle.StaleReasons?.Contains("runtime", StringComparer.Ordinal) == true, "runtime stale reason");
        using JsonDocument document = ParseJson(RimContextBundleJson.Serialize(bundle));
        JsonElement failure = document.RootElement.GetProperty("failures")[0];
        AssertEqual(true, failure.GetProperty("retryAfterStateChange").GetBoolean(), "failure retry taxonomy");
        AssertEqual(true, failure.GetProperty("infrastructureOnly").GetBoolean(), "failure infrastructure taxonomy");
        AssertEqual("reuse-invalidated", failure.GetProperty("evidenceInvalidationEffect").GetString(), "failure evidence taxonomy");
    }

    private static void ContextBundleDecisionProvenance()
    {
        var decision = new RimContextDecision
        {
            Decision = "affected-test-selection",
            Action = RimContextDecisionActions.Reuse,
            ReasonCode = "SOURCE_FINGERPRINT_MATCH",
            Explanation = "The current source fingerprint matches reusable evidence.",
            RelevantChangedInputs = ["Source/Changed.cs"],
            PreviousEvidence = [new RimContextEvidenceReference { Id = "proof-1", Kind = "validation-proof" }],
            EvidenceReused = [new RimContextEvidenceReference { Id = "proof-1", Status = "reused" }],
            EvidenceInvalidated = [new RimContextEvidenceReference { Id = "proof-old", Status = "invalidated" }],
            Owner = "RimTest/RimLiaison",
            ObservedAtUtc = FixedTime
        };
        var bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [CompleteContextSnapshot() with { Decisions = [decision] }]);

        using var document = ParseJson(RimContextBundleJson.Serialize(bundle));
        var serialized = document.RootElement.GetProperty("decisions")[0];
        AssertEqual("REUSE", serialized.GetProperty("action").GetString(), "decision action provenance");
        AssertEqual("SOURCE_FINGERPRINT_MATCH", serialized.GetProperty("reasonCode").GetString(), "decision reason provenance");
        AssertEqual("RimTest/RimLiaison", serialized.GetProperty("owner").GetString(), "decision owner provenance");
        AssertEqual("proof-1", serialized.GetProperty("evidenceReused")[0].GetProperty("id").GetString(), "reused evidence provenance");
        AssertEqual("proof-old", serialized.GetProperty("evidenceInvalidated")[0].GetProperty("id").GetString(), "invalidated evidence provenance");
        Assert(!bundle.AgentSummary.ReusableEvidence.Contains("proof-old", StringComparer.Ordinal), "invalidated evidence is not reusable");
    }

    private static void ContextBundleStaleOrdering()
    {
        var old = CompleteContextSnapshot(FixedTime.AddMinutes(-10)) with
        {
            Decisions =
            [
                new RimContextDecision
                {
                    Decision = "z-decision",
                    Action = RimContextDecisionActions.Run,
                    ReasonCode = "Z",
                    Owner = "fixture",
                    ObservedAtUtc = FixedTime.AddMinutes(-9)
                },
                new RimContextDecision
                {
                    Decision = "a-decision",
                    Action = RimContextDecisionActions.Skip,
                    ReasonCode = "A",
                    Owner = "fixture",
                    ObservedAtUtc = FixedTime.AddMinutes(-8)
                }
            ],
            Extensions =
            [
                Extension("fixture", "z", "2"),
                Extension("fixture", "a", "1")
            ]
        };
        var bundle = RimContextBundleBuilder.Build(
            new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
            FixedTime,
            [old]);

        AssertEqual("stale", bundle.Repository.Status, "stale repository status");
        AssertEqual(true, bundle.Repository.Stale, "stale repository marker");
        Assert(bundle.StaleReasons?.Contains("repository", StringComparer.Ordinal) == true, "stale repository reason");
        AssertEqual("a-decision", bundle.Decisions[0].Decision, "decision deterministic ordering");
        AssertEqual("a", bundle.Extensions![0].Key, "extension deterministic ordering");
    }

    private static void ContextBundleProviderFailure()
    {
        var bundle = RimContextBundleBuilder.BuildAsync(
                new RimContextBundleRequest(RootPath: "fixture", NowUtc: FixedTime),
                [new ThrowingContextProvider()])
            .GetAwaiter()
            .GetResult();

        AssertEqual("CONTEXT_PROVIDER_FAILED", bundle.Failures.Single().SignatureCode, "provider failure signature");
        AssertEqual("fixture-provider", bundle.Failures.Single().OriginatingComponent, "provider failure owner");
        AssertEqual("unknown", bundle.Topology.Status, "provider failure topology status");
    }

    private static RimContextProviderSnapshot CompleteContextSnapshot(DateTimeOffset? observedAtUtc = null)
    {
        DateTimeOffset observed = observedAtUtc ?? FixedTime;
        return new RimContextProviderSnapshot(
            "fixture",
            observed,
            Topology: AvailableSection(new RimContextTopology
            {
                Components = [new RimContextComponent { Name = "fixture", Role = "test provider" }]
            }),
            Repository: AvailableSection(new RimContextRepositoryState { Identity = "git:fixture", Dirty = false }),
            Environment: AvailableSection(new RimContextEnvironmentState { Os = "test", Runtime = ".NET", SecretsExcluded = true }),
            Deployment: AvailableSection(new RimContextDeploymentState { SourceFingerprint = "source-1" }),
            Runtime: AvailableSection(new RimContextRuntimeState { RimWorldRunning = false, BridgeStatus = "available" }),
            Testing: AvailableSection(new RimContextTestingState { AvailableSuites = ["smoke"], AvailableTests = ["test"] }),
            Efficiency: AvailableSection(new RimContextEfficiencyMetrics { TotalWorkflowMs = 10 }));
    }

    private static RimContextSection<T> AvailableSection<T>(T value, string provider = "fixture") => new()
    {
        Status = RimContextBundleStatuses.Available,
        Value = value,
        Provider = provider
    };

    private static RimContextExtension Extension(string provider, string key, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return new RimContextExtension { Provider = provider, Key = key, Value = document.RootElement.Clone() };
    }

    private static void CompactQueryOutput()
    {
        using var workspace = NewXmlWorkspace();
        workspace.Write(
            "Source/Weapon.cs",
            "namespace Test; public class Weapon { public void Fire() {} }\n");
        var index = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, index.ExitCode, "compact fixture index");

        var definition = Run("definition", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        var symbol = Run("find", "Test.Weapon", "--root", workspace.Root, "--json");
        var references = Run("refs", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        var affected = Run("affected", "Mods/TestMod/Defs/Weapons.xml", "--root", workspace.Root, "--json");
        var definitionBytes = Encoding.UTF8.GetByteCount(definition.Stdout);
        var symbolBytes = Encoding.UTF8.GetByteCount(symbol.Stdout);
        var referenceBytes = Encoding.UTF8.GetByteCount(references.Stdout);
        var affectedBytes = Encoding.UTF8.GetByteCount(affected.Stdout);

        Assert(definitionBytes < 1024, "exact definition should stay below 1 KB");
        Assert(symbolBytes < 1024, "exact symbol should stay below 1 KB");
        Assert(referenceBytes < 3072, "references should stay below 3 KB");
        Assert(affectedBytes < 5120, "affected should stay below 5 KB");

        using var definitionJson = ParseJson(definition.Stdout);
        AssertEqual(false, definitionJson.RootElement.GetProperty("meta").GetProperty("truncated").GetBoolean(), "definition not truncated");
        using var affectedJson = ParseJson(affected.Stdout);
        AssertEqual(false, affectedJson.RootElement.GetProperty("meta").GetProperty("truncated").GetBoolean(), "affected not truncated");

        var limited = Run("affected", "Mods/TestMod/Defs/Weapons.xml", "--root", workspace.Root, "--max-bytes", "256", "--json");
        using var limitedJson = ParseJson(limited.Stdout);
        Assert(Encoding.UTF8.GetByteCount(limited.Stdout.TrimEnd()) <= 256, "byte-limited response should be bounded");
        Assert(limitedJson.RootElement.TryGetProperty("truncated", out var topTruncated)
            ? topTruncated.GetBoolean()
            : limitedJson.RootElement.GetProperty("meta").GetProperty("truncated").GetBoolean(), "byte limit truncation");

        var human = Run("find", "Test.Weapon", "--root", workspace.Root, "--human");
        Assert(human.Stdout.Contains("\n  ", StringComparison.Ordinal), "human mode should be indented");

        var warmTimer = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var warm = Run("definition", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
            AssertEqual(0, warm.ExitCode, "warm definition query");
        }

        warmTimer.Stop();
        Console.WriteLine($"PERF query_bytes definition={definitionBytes} symbol={symbolBytes} refs={referenceBytes} affected={affectedBytes} warm_definition_avg_ms={warmTimer.Elapsed.TotalMilliseconds / 5:0.0}");
    }

    private static void ProductionEndToEndWorkflow()
    {
        using var workspace = new TempWorkspace();
        workspace.CopyFrom(RepositoryFixture("RealisticMod"));
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);

        var initialTimer = Stopwatch.StartNew();
        var initial = Run("index", "--root", workspace.Root, "--json");
        initialTimer.Stop();
        AssertEqual(0, initial.ExitCode, "production initial index exit code");
        using var initialJson = ParseJson(initial.Stdout);
        AssertEqual("ok", initialJson.RootElement.GetProperty("status").GetString(), "production initial index status");
        var initialData = initialJson.RootElement.GetProperty("data");
        var initialFiles = initialData.GetProperty("files");
        Assert(initialFiles.GetProperty("scanned").GetInt32() >= 9, "production fixture scan count");
        Assert(initialData.GetProperty("duration_ms").GetInt64() >= 0, "production initial duration");

        var summary = Run("summary", "--root", workspace.Root, "--json");
        AssertEqual(0, summary.ExitCode, "production summary exit code");
        using (var summaryJson = ParseJson(summary.Stdout))
        {
            var data = summaryJson.RootElement.GetProperty("data");
            Assert(data.GetProperty("mods").GetInt32() >= 1, "production mod count");
            Assert(data.GetProperty("projects").GetInt32() >= 2, "production project count");
            Assert(data.GetProperty("defs").GetInt32() >= 5, "production def count");
            Assert(data.GetProperty("harmony_patches").GetInt32() >= 1, "production Harmony count");
        }

        var definition = Run("definition", "ThingDef/ExampleWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, definition.ExitCode, "production Def lookup exit code");
        using (var definitionJson = ParseJson(definition.Stdout))
        {
            var item = definitionJson.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("ExampleWeapon", item.GetProperty("defName").GetString(), "production Def name");
        }

        var symbol = Run("definition", "RealisticMod.ExampleWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, symbol.ExitCode, "production C# symbol lookup exit code");
        using (var symbolJson = ParseJson(symbol.Stdout))
        {
            AssertEqual("csharp_type", symbolJson.RootElement.GetProperty("results").EnumerateArray()
                .Single().GetProperty("kind").GetString(), "production C# symbol kind");
        }

        var references = Run("refs", "ThingDef/ExampleWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, references.ExitCode, "production refs exit code");
        using (var referencesJson = ParseJson(references.Stdout))
        {
            var data = referencesJson.RootElement.GetProperty("data");
            Assert(GetArray(data, "incoming").Length > 0, "production incoming Def refs");
        }

        var harmony = Run("harmony", "ExampleWeapon.TickWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, harmony.ExitCode, "production Harmony query exit code");
        using (var harmonyJson = ParseJson(harmony.Stdout))
        {
            Assert(harmonyJson.RootElement.GetProperty("results").EnumerateArray()
                .Any(item => GetArray(item, "patches").Length > 0), "production Harmony patch");
        }

        var affected = Run("affected", "RealisticMod/Defs/Weapons.xml", "--root", workspace.Root, "--json");
        AssertEqual(0, affected.ExitCode, "production affected exit code");
        using (var affectedJson = ParseJson(affected.Stdout))
        {
            Assert(GetArray(affectedJson.RootElement.GetProperty("data"), "direct").Length > 0,
                "production affected direct tier");
        }

        var noOpTimer = Stopwatch.StartNew();
        var noOp = Run("index", "--root", workspace.Root, "--json");
        noOpTimer.Stop();
        AssertEqual(0, noOp.ExitCode, "production no-op index exit code");
        using (var noOpJson = ParseJson(noOp.Stdout))
        {
            var files = noOpJson.RootElement.GetProperty("data").GetProperty("files");
            AssertEqual(0, files.GetProperty("added").GetInt32(), "production no-op added");
            AssertEqual(0, files.GetProperty("changed").GetInt32(), "production no-op changed");
            AssertEqual(0, files.GetProperty("removed").GetInt32(), "production no-op removed");
        }

        workspace.Write("RealisticMod/Source/ModSettings.cs", "namespace RealisticMod; public static class ModSettings { public const string WeaponDef = \"ExampleWeapon\"; public const int Revision = 2; }\n");
        var changed = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, changed.ExitCode, "production changed index exit code");
        using (var changedJson = ParseJson(changed.Stdout))
        {
            AssertEqual(1, changedJson.RootElement.GetProperty("data").GetProperty("files").GetProperty("changed").GetInt32(),
                "production changed file count");
        }

        var affectedAfterChange = Run("affected", "RealisticMod/Source/ModSettings.cs", "--root", workspace.Root, "--json");
        AssertEqual(0, affectedAfterChange.ExitCode, "production repeated affected exit code");
        using (var affectedJson = ParseJson(affectedAfterChange.Stdout))
        {
            Assert(GetArray(affectedJson.RootElement.GetProperty("data"), "direct").Length > 0,
                "production repeated affected direct tier");
        }

        workspace.Delete("RealisticMod/Defs/Research.xml");
        var deleted = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, deleted.ExitCode, "production deletion index exit code");
        using (var deletedJson = ParseJson(deleted.Stdout))
        {
            AssertEqual(1, deletedJson.RootElement.GetProperty("data").GetProperty("files").GetProperty("removed").GetInt32(),
                "production deleted file count");
        }

        var removedDefinition = Run("definition", "ResearchProjectDef/ExampleWeapons", "--root", workspace.Root, "--json");
        AssertEqual(4, removedDefinition.ExitCode, "production deleted Def query exit code");
        using (var removedJson = ParseJson(removedDefinition.Stdout))
        {
            AssertEqual(ErrorCodes.NotFound, removedJson.RootElement.GetProperty("code").GetString(), "production deleted Def error");
        }

        var databaseSize = new FileInfo(configuration.StorePath).Length;
        var queryTimer = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var query = Run("definition", "ThingDef/ExampleWeapon", "--root", workspace.Root, "--json");
            AssertEqual(0, query.ExitCode, "production warm query exit code");
        }

        queryTimer.Stop();
        var definitionBytes = Encoding.UTF8.GetByteCount(definition.Stdout.TrimEnd());
        var referencesBytes = Encoding.UTF8.GetByteCount(references.Stdout.TrimEnd());
        var affectedBytes = Encoding.UTF8.GetByteCount(affected.Stdout.TrimEnd());
        Console.WriteLine($"PERF e2e initial_ms={initialTimer.Elapsed.TotalMilliseconds:0.0} index_duration_ms={initialData.GetProperty("duration_ms").GetInt64()} noop_ms={noOpTimer.Elapsed.TotalMilliseconds:0.0} query_avg_ms={queryTimer.Elapsed.TotalMilliseconds / 5:0.0} definition_bytes={definitionBytes} refs_bytes={referencesBytes} affected_bytes={affectedBytes} database_bytes={databaseSize}");
    }

    private static void StoreRecoveryAndLockHandling()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/A.cs", "public class A {}\n");
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        AssertEqual(0, Run("index", "--root", workspace.Root, "--json").ExitCode, "recovery initial index");

        File.WriteAllText(configuration.StorePath + ".tmp", "interrupted temporary store");
        var recoveredTemporary = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, recoveredTemporary.ExitCode, "interrupted temporary recovery");
        Assert(!File.Exists(configuration.StorePath + ".tmp"), "stale temporary store removed");

        File.WriteAllText(configuration.StorePath, "not a SQLite database");
        var recoveredCorruption = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, recoveredCorruption.ExitCode, "corrupt database recovery");
        using (var summary = ParseJson(Run("summary", "--root", workspace.Root, "--json").Stdout))
        {
            AssertEqual("ok", summary.RootElement.GetProperty("status").GetString(), "recovered database summary");
        }

        File.Delete(configuration.StorePath);
        var incompatibleMetadata = new StoreMetadata(
            999,
            IndexConstants.ToolVersion,
            configuration.WorkspaceIdentity,
            configuration.RootPath,
            configuration.ConfigurationFingerprint,
            FixedTime.ToString("O", CultureInfo.InvariantCulture));
        using (var incompatibleStore = IndexStore.CreateNew(configuration.StorePath, incompatibleMetadata))
        {
        }

        var migrated = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, migrated.ExitCode, "schema mismatch rebuild");
        using (var schemaSummary = ParseJson(Run("summary", "--root", workspace.Root, "--json").Stdout))
        {
            AssertEqual("ok", schemaSummary.RootElement.GetProperty("status").GetString(), "schema rebuild summary");
        }

        var lockPath = configuration.StorePath + ".lock";
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var locked = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(4, locked.ExitCode, "concurrent index lock exit code");
        using var lockedJson = ParseJson(locked.Stdout);
        AssertEqual(ErrorCodes.StoreLocked, lockedJson.RootElement.GetProperty("code").GetString(), "concurrent index lock code");
    }

    private static void SchemaCreation()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        Assert(File.Exists(configuration.StorePath), "index store should exist");
        AssertEqual(2, result.Counts.FileCount, "indexed file count");
        using var store = IndexStore.OpenReadOnly(configuration);
        AssertEqual(IndexConstants.SchemaVersion, store.Metadata.SchemaVersion, "schema version");
        AssertEqual(IndexConstants.ToolVersion, store.Metadata.ToolVersion, "tool version");
        AssertEqual(configuration.WorkspaceIdentity, store.Metadata.WorkspaceIdentity, "workspace identity");
        AssertEqual(FixedTime.ToUniversalTime().ToString("O"), store.Metadata.IndexedAtUtc, "index timestamp");
        AssertEqual(3L, store.GetCounts().EntityCount, "entity count");
        Assert(store.GetEntities().Any(entity => entity.Kind == "csharp_type"), "C# type entity should be present");
    }

    private static void SchemaReopening()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        using var first = IndexStore.OpenReadOnly(configuration);
        using var second = IndexStore.OpenReadOnly(configuration);
        AssertEqual(first.Metadata, second.Metadata, "reopened metadata");
        AssertEqual(first.GetCounts(), second.GetCounts(), "reopened counts");
    }

    private static void IncrementalFirstIndex()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        AssertEqual(2, result.Statistics.Scanned, "first scan count");
        AssertEqual(2, result.Statistics.Added, "first added count");
        AssertEqual(0, result.Statistics.Changed, "first changed count");
        AssertEqual(0, result.Statistics.Removed, "first removed count");
        AssertEqual(0, result.Statistics.Unchanged, "first unchanged count");
        Assert(result.DurationMilliseconds >= 0, "duration should be non-negative");
    }

    private static void IncrementalNoOp()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(2, result.Statistics.Scanned, "no-op scan count");
        AssertEqual(0, result.Statistics.Added, "no-op added count");
        AssertEqual(0, result.Statistics.Changed, "no-op changed count");
        AssertEqual(0, result.Statistics.Removed, "no-op removed count");
        AssertEqual(2, result.Statistics.Unchanged, "no-op unchanged count");
    }

    private static void IncrementalChangedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Write("Source/A.cs", "class A { int Changed; }\n");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Changed, "changed count");
        AssertEqual(1, result.Statistics.Unchanged, "changed run unchanged count");
        AssertEqual(0, result.Statistics.Added, "changed run added count");
        AssertEqual(0, result.Statistics.Removed, "changed run removed count");

        using var store = IndexStore.OpenReadOnly(configuration);
        var source = store.GetFiles().Single(file => file.Path == "Source/A.cs");
        AssertEqual(64, source.ContentHash.Length, "changed file hash should be refreshed");
        AssertEqual(4L, store.GetCounts().EntityCount, "changed file should have current metadata and semantic entities");
    }

    private static void IncrementalDeletedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Delete("Defs/A.xml");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Scanned, "delete scan count");
        AssertEqual(1, result.Statistics.Removed, "deleted count");
        AssertEqual(1, result.Statistics.Unchanged, "delete run unchanged count");

        using var store = IndexStore.OpenReadOnly(configuration);
        Assert(!store.GetFiles().Any(file => file.Path == "Defs/A.xml"), "deleted file row should be removed");
        Assert(!store.GetEntities().Any(entity => entity.IdentityKey == "Defs/A.xml"), "deleted file entity should be removed");
    }

    private static void IncrementalRenamedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Rename("Source/A.cs", "Source/B.cs");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Added, "renamed added count");
        AssertEqual(1, result.Statistics.Removed, "renamed removed count");
        AssertEqual(1, result.Statistics.Unchanged, "renamed unchanged count");

        using var store = IndexStore.OpenReadOnly(configuration);
        var paths = store.GetFiles().Select(file => file.Path).ToArray();
        Assert(paths.Contains("Source/B.cs", StringComparer.Ordinal), "renamed target should be indexed");
        Assert(!paths.Contains("Source/A.cs", StringComparer.Ordinal), "renamed source should be removed");
    }

    private static void StableIdDeterminism()
    {
        var first = StableEntityId.Create("source_file", "workspace:root", "src/A.cs");
        var second = StableEntityId.Create("source_file", "workspace:root", "src/A.cs");
        AssertEqual(first, second, "same semantic identity");
        Assert(!first.Equals(StableEntityId.Create("source_file", "workspace:root", "src/B.cs"), StringComparison.Ordinal), "different path should have different ID");

        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        string firstFileId;
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            firstFileId = store.GetFiles().Single(file => file.Path == "Source/A.cs").Id;
        }

        workspace.Write("Unrelated.cs", "class Unrelated {}\n");
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        using var reopened = IndexStore.OpenReadOnly(configuration);
        var secondFileId = reopened.GetFiles().Single(file => file.Path == "Source/A.cs").Id;
        AssertEqual(firstFileId, secondFileId, "unrelated file changes must not change IDs");
    }

    private static void SchemaVersionHandling()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = configuration.StorePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var exception = AssertThrows<RimContextException>(() => IndexStore.OpenReadOnly(configuration), "schema mismatch should fail");
        AssertEqual(ErrorCodes.IndexIncompatible, exception.Error.Code, "schema mismatch code");
    }

    private static void WorkspaceDiscoveryRules()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("src/A.cs", "class A {}\n");
        workspace.Write("Defs/A.xml", "<Defs />\n");
        workspace.Write("Directory.Build.props", "<Project />\n");
        workspace.Write("notes.txt", "ignored\n");
        workspace.Write(".git/ignored.cs", "class Ignored {}\n");
        workspace.Write("bin/ignored.xml", "<Defs />\n");

        using var external = new TempWorkspace();
        external.Write("Lib.dll", "not an assembly yet\n");
        external.Write("Ignored.cs", "class Ignored {}\n");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root, assemblyRoots: [external.Root]);
        var files = WorkspaceDiscovery.Discover(configuration);
        var paths = files.Select(file => file.DisplayPath).ToArray();
        Assert(paths.Contains("src/A.cs", StringComparer.Ordinal), "source file should be discovered");
        Assert(paths.Contains("Defs/A.xml", StringComparer.Ordinal), "XML file should be discovered");
        Assert(paths.Contains("Directory.Build.props", StringComparer.Ordinal), "build properties should be discovered");
        Assert(paths.Any(path => path.StartsWith("external/", StringComparison.Ordinal) && path.EndsWith("/Lib.dll", StringComparison.Ordinal)), "external assembly should be discovered");
        Assert(!paths.Any(path => path.Contains("ignored", StringComparison.OrdinalIgnoreCase)), "ignored/generated files should not be discovered");
        Assert(paths.SequenceEqual(paths.OrderBy(path => path, StringComparer.Ordinal)), "discovery order should be deterministic");
    }

    private static void ExcludedDirectories()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/Kept.cs", "class Kept {}\n");
        workspace.Write(".git/Ignored.cs", "class Ignored {}\n");
        workspace.Write("bin/Ignored.cs", "class Ignored {}\n");
        workspace.Write("obj/Ignored.xml", "<Defs />\n");
        workspace.Write("artifacts/Ignored.cs", "class Ignored {}\n");
        workspace.Write(".rimctx/Ignored.cs", "class Ignored {}\n");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(1, result.Statistics.Scanned, "excluded directories must not be scanned");
        using var store = IndexStore.OpenReadOnly(configuration);
        AssertEqual("Source/Kept.cs", store.GetFiles().Single().Path, "kept file path");
    }

    private static void PathNormalization()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/Nested.cs", "class Nested {}\n");
        var configuration = WorkspaceConfiguration.Resolve(Path.Combine(workspace.Root, "."));
        var files = WorkspaceDiscovery.Discover(configuration);
        AssertEqual("Source/Nested.cs", files.Single().DisplayPath, "root-relative path normalization");

        var slashId = StableEntityId.Create("source_file", "workspace:root", "Source/Nested.cs");
        var backslashId = StableEntityId.Create("source_file", "workspace:root", "Source\\Nested.cs");
        AssertEqual(slashId, backslashId, "slash normalization must preserve IDs");
    }

    private static void InvalidWorkspaceInput()
    {
        using var workspace = new TempWorkspace();
        var missingRoot = Path.Combine(workspace.Root, "does-not-exist");
        var result = Run("index", "--root", missingRoot, "--json");
        AssertEqual(4, result.ExitCode, "missing root exit code");
        using var document = ParseJson(result.Stdout);
        AssertEqual(ErrorCodes.PathNotFound, document.RootElement.GetProperty("code").GetString(), "missing root error code");
        Assert(!result.Stdout.Contains(missingRoot, StringComparison.Ordinal), "error output must not expose absolute input paths");
    }

    private static void IncrementalFixturePerformance()
    {
        using var workspace = new TempWorkspace();
        for (var index = 0; index < 64; index++)
        {
            workspace.Write($"Source/Generated{index:D3}.cs", $"class Generated{index} {{ }}\n");
        }

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var clock = Stopwatch.StartNew();
        var first = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        clock.Stop();
        var firstMilliseconds = clock.ElapsedMilliseconds;

        clock.Restart();
        var second = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        clock.Stop();
        var secondMilliseconds = clock.ElapsedMilliseconds;

        AssertEqual(64, first.Statistics.Added, "fixture first added count");
        AssertEqual(64, second.Statistics.Unchanged, "fixture no-op unchanged count");
        Console.WriteLine($"PERF fixture_files=64 first_ms={firstMilliseconds} second_ms={secondMilliseconds}");
    }

    private static void XmlSemanticEntitiesAndQueries()
    {
        using var workspace = NewXmlWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        AssertEqual(5, result.Statistics.Added, "XML fixture file count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            Assert(entities.Any(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defName\":\"MyWeapon\"", StringComparison.Ordinal)), "simple def should be indexed");
            Assert(entities.Any(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defType\":\"RecipeDef\"", StringComparison.Ordinal)), "multiple def types should be indexed");
            Assert(entities.Count(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defName\":\"Duplicate\"", StringComparison.Ordinal)) == 2, "duplicate def names should retain separate entities");
            Assert(entities.Any(entity => entity.Kind == "patch_operation" && entity.PayloadJson.Contains("PatchOperationAdd", StringComparison.Ordinal)), "patch operation should be indexed");
            Assert(entities.Any(entity => entity.Kind == "mod" && entity.PayloadJson.Contains("com.test.mod", StringComparison.Ordinal)), "mod ownership should be indexed");

            var relations = store.GetRelations();
            Assert(relations.Any(relation => relation.Kind == "inheritance"), "inheritance relation should be indexed");
            Assert(relations.Any(relation => relation.Kind == "def_reference" && relation.ToId is not null), "resolved def reference should be indexed");
        }

        var definition = Run("definition", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, definition.ExitCode, "definition query exit code");
        using (var document = ParseJson(definition.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("ThingDef", item.GetProperty("defType").GetString(), "definition type");
            AssertEqual("MyWeapon", item.GetProperty("defName").GetString(), "definition name");
            AssertEqual("Mods/TestMod/Defs/Weapons.xml", item.GetProperty("file").GetString(), "definition file");
            Assert(item.GetProperty("line").GetInt32() > 0, "definition line");
            AssertEqual("com.test.mod", item.GetProperty("mod").GetString(), "definition owner");
            AssertEqual("BaseWeapon", item.GetProperty("parent").GetString(), "definition parent");
        }

        var find = Run("find", "Duplicate", "--root", workspace.Root, "--json");
        AssertEqual(0, find.ExitCode, "duplicate find exit code");
        using (var document = ParseJson(find.Stdout))
        {
            AssertEqual(2, document.RootElement.GetProperty("results").GetArrayLength(), "duplicate find result count");
        }

        var prefix = Run("find", "My", "--root", workspace.Root, "--json");
        AssertEqual(0, prefix.ExitCode, "prefix find exit code");
        using (var document = ParseJson(prefix.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("defName").GetString() == "MyWeapon"), "prefix find result");
        }

        var substring = Run("find", "Weapon", "--root", workspace.Root, "--json");
        AssertEqual(0, substring.ExitCode, "substring find exit code");
        using (var document = ParseJson(substring.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("defName").GetString() == "MyWeapon"), "substring find result");
        }

        var refs = Run("refs", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "refs query exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("incoming").EnumerateArray().Any(), "incoming reference should be present");
            Assert(data.GetProperty("outgoing").EnumerateArray().Any(), "outgoing reference should be present");
            Assert(data.GetProperty("outgoing").EnumerateArray().All(item => item.GetProperty("direction").GetString() == "outgoing"), "outgoing direction");
        }
    }

    private static void MalformedXmlDiagnostics()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Defs/Broken.xml", "<Defs><ThingDef><defName>Broken</defName>");
        var result = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, result.ExitCode, "malformed XML should not fail indexing");
        using var document = ParseJson(result.Stdout);
        AssertEqual("partial", document.RootElement.GetProperty("status").GetString(), "malformed XML status");
        var warning = document.RootElement.GetProperty("warnings").EnumerateArray().Single();
        AssertEqual("Defs/Broken.xml", warning.GetProperty("path").GetString(), "malformed XML warning path");
        Assert(warning.GetProperty("message").GetString()!.StartsWith("Malformed XML", StringComparison.Ordinal), "malformed XML warning message");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        using var store = IndexStore.OpenReadOnly(configuration);
        var file = store.GetFiles().Single();
        AssertEqual("error", file.ParseStatus, "malformed XML parse status");
        Assert(file.Diagnostic?.StartsWith("Malformed XML", StringComparison.Ordinal) == true, "malformed XML file diagnostic");
    }

    private static void IncrementalXmlCleanup()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Defs/Changing.xml", "<Defs><ThingDef><defName>OldName</defName></ThingDef></Defs>");
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Write("Defs/Changing.xml", "<Defs><ThingDef><defName>NewName</defName></ThingDef></Defs>");
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "changed XML count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var payloads = store.GetEntities().Where(entity => entity.Kind == "def").Select(entity => entity.PayloadJson).ToArray();
            Assert(payloads.Any(payload => payload.Contains("\"defName\":\"NewName\"", StringComparison.Ordinal)), "new XML def should be indexed");
            Assert(!payloads.Any(payload => payload.Contains("\"defName\":\"OldName\"", StringComparison.Ordinal)), "old XML def should be removed");
        }

        workspace.Delete("Defs/Changing.xml");
        var deleted = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(2));
        AssertEqual(1, deleted.Statistics.Removed, "deleted XML count");
        using var reopened = IndexStore.OpenReadOnly(configuration);
        Assert(!reopened.GetEntities().Any(entity => entity.Kind == "def"), "deleted XML entities should be removed");
    }

    private static void AffectedQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Base.cs",
            """
            namespace Game;
            public class Base
            {
                public void Tick() { }
            }
            """);
        workspace.Write(
            "Source/Use.cs",
            """
            namespace Game;
            public class Use : Base
            {
                public void Run()
                {
                    var value = new Base();
                    value.Tick();
                }
            }
            """);
        workspace.Write(
            "Source/Patch.cs",
            """
            using Game;
            [HarmonyPatch(typeof(Base), "Tick")]
            public static class BasePatch
            {
                [HarmonyPostfix]
                public static void Postfix() { }
            }
            """);
        workspace.Write("Source/Isolated.cs", "namespace Isolated; public class OnlyHere { }");
        workspace.Write("Source/CycleA.cs", "namespace Cycle; public class A : B { }");
        workspace.Write("Source/CycleB.cs", "namespace Cycle; public class B : A { }");
        workspace.Write(
            "Defs/Weapons.xml",
            """
            <Defs>
              <ThingDef><defName>MyWeapon</defName></ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Defs/Recipes.xml",
            """
            <Defs>
              <RecipeDef>
                <defName>MakeWeapon</defName>
                <thingDef>MyWeapon</thingDef>
              </RecipeDef>
            </Defs>
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var indexed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(0, indexed.Diagnostics?.Count ?? 0, "affected fixture diagnostics");

        var source = Run("affected", "Source/Base.cs", "--root", workspace.Root, "--json");
        AssertEqual(0, source.ExitCode, "source affected exit code");
        using (var document = ParseJson(source.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Base.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "source changed path");
            Assert(data.GetProperty("direct").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "csharp_type" &&
                item.GetProperty("name").GetString() == "Game.Base"), "direct changed type");
            Assert(data.GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Game.Use"), "dependent shared type");
            var runtimeRisk = data.GetProperty("runtime_risk").EnumerateArray().ToArray();
            Assert(runtimeRisk.Any(item =>
                item.GetProperty("kind").GetString() == "harmony_patch" &&
                item.GetProperty("name").GetString() == "BasePatch.Postfix"), "Harmony runtime risk");
            Assert(runtimeRisk.All(item => item.TryGetProperty("reason", out var reason) &&
                                           reason.GetString()!.Contains("harmony_target", StringComparison.Ordinal)),
                "runtime risk reason");
        }

        var xml = Run("affected", "Defs/Weapons.xml", "--root", workspace.Root, "--json");
        AssertEqual(0, xml.ExitCode, "XML affected exit code");
        using (var document = ParseJson(xml.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("direct").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "ThingDef/MyWeapon"), "direct changed def");
            Assert(data.GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "RecipeDef/MakeWeapon"), "dependent referenced def");
        }

        var absolute = Run(
            "affected",
            Path.Combine(workspace.Root, "Source", "Isolated.cs"),
            "--root",
            workspace.Root,
            "--json");
        using (var document = ParseJson(absolute.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Isolated.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "absolute path normalization");
            Assert(GetArray(data, "dependent").Length == 0, "isolated source has no dependents");
        }

        var multiple = Run(
            "affected",
            "Source/Isolated.cs",
            "Source/Base.cs",
            "Source/Isolated.cs",
            "--root",
            workspace.Root,
            "--json");
        using (var document = ParseJson(multiple.Stdout))
        {
            var changed = document.RootElement.GetProperty("data").GetProperty("changed").EnumerateArray().ToArray();
            AssertEqual(2, changed.Length, "multiple changed path deduplication");
            AssertEqual("Source/Base.cs", changed[0].GetString(), "multiple path ordering");
            AssertEqual("Source/Isolated.cs", changed[1].GetString(), "multiple path ordering second");
        }

        var limited = Run("affected", "Defs/Weapons.xml", "--root", workspace.Root, "--limit", "1", "--json");
        using (var document = ParseJson(limited.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("truncated").GetBoolean(), "affected result truncation");
            var resultCount = GetArray(data, "direct").Length +
                              GetArray(data, "dependent").Length +
                              GetArray(data, "runtime_risk").Length;
            Assert(resultCount <= 1, "affected global result limit");
        }

        var cycle = Run("affected", "Source/CycleA.cs", "--root", workspace.Root, "--depth", "8", "--json");
        AssertEqual(0, cycle.ExitCode, "cyclic graph affected exit code");
        using (var document = ParseJson(cycle.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Cycle.B"), "cyclic graph dependent");
        }

        workspace.Delete("Source/Isolated.cs");
        var deleted = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, deleted.Statistics.Removed, "affected deleted file index count");
        var missing = Run("affected", "Source/Isolated.cs", "--root", workspace.Root, "--json");
        using (var document = ParseJson(missing.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Isolated.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "deleted changed path");
            AssertEqual(0, GetArray(data, "direct").Length, "deleted direct entities");
            AssertEqual(0, GetArray(data, "dependent").Length, "deleted dependent entities");
        }

        var unknown = Run("affected", "Source/Missing.cs", "--root", workspace.Root, "--json");
        using var unknownDocument = ParseJson(unknown.Stdout);
        AssertEqual(0, unknown.ExitCode, "unknown affected path exit code");
        AssertEqual(0, GetArray(unknownDocument.RootElement.GetProperty("data"), "direct").Length, "unknown direct entities");
    }

    private static void ModProjectDependencyIndexing()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Mods/A/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.a</packageId>
              <name>Alpha</name>
              <supportedVersions><li>1.5</li><li>1.6</li></supportedVersions>
              <modDependencies><li><packageId>com.test.b</packageId></li></modDependencies>
              <loadAfter><li>com.external.after</li></loadAfter>
              <loadBefore><li>com.external.before</li></loadBefore>
              <incompatibleWith><li>com.external.incompatible</li></incompatibleWith>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/B/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.b</packageId>
              <name>Beta</name>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/C/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.a</packageId>
              <name>Duplicate Alpha</name>
            </ModMetaData>
            """);
        workspace.Write("Mods/A/Source/A.cs", "namespace Alpha; public class A {}\n");
        workspace.Write("Mods/B/Source/B.cs", "namespace Beta; public class B {}\n");
        workspace.Write(
            "Mods/A/A.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <RootNamespace>Alpha</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../B/B.csproj" />
                <ProjectReference Include="../Missing/Missing.csproj" />
                <PackageReference Include="HarmonyLib" Version="2.3.0" />
                <Reference Include="Beta">
                  <HintPath>../B/bin/B.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup>
                <TargetFrameworks>net8.0;net472</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        Assert(result.Diagnostics?.Count(item => item.Code == "DEPENDENCY") >= 3, "dependency diagnostics");

        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            AssertEqual(3, entities.Count(item => item.Kind == "mod"), "mod entity count");
            AssertEqual(2, entities.Count(item => item.Kind == "project"), "project entity count");
            Assert(entities.Any(item =>
                item.Kind == "project" &&
                item.PayloadJson.Contains("\"packageReferences\":[{\"include\":\"HarmonyLib\",\"version\":\"2.3.0\"}]", StringComparison.Ordinal)),
                "package reference payload");
            var relations = store.GetRelations();
            Assert(relations.Any(item => item.Kind == "requires"), "requires relation");
            Assert(relations.Any(item => item.Kind == "load_after"), "load after relation");
            Assert(relations.Any(item => item.Kind == "load_before"), "load before relation");
            Assert(relations.Any(item => item.Kind == "incompatible"), "incompatible relation");
            Assert(relations.Any(item => item.Kind == "project_reference"), "project reference relation");
            Assert(relations.Any(item => item.Kind == "assembly_reference"), "assembly reference relation");
            Assert(relations.Any(item => item.Kind == "owns" && item.ToId is not null), "ownership relation");
        }

        var summary = Run("summary", "--root", workspace.Root, "--json");
        AssertEqual(0, summary.ExitCode, "dependency summary exit code");
        using (var document = ParseJson(summary.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual(3, data.GetProperty("mods").GetInt64(), "summary mods");
            AssertEqual(2, data.GetProperty("projects").GetInt64(), "summary projects");
            AssertEqual(2, data.GetProperty("source_files").GetInt64(), "summary sources");
            AssertEqual(3, data.GetProperty("xml_files").GetInt64(), "summary XML");
            Assert(data.GetProperty("diagnostics").GetProperty("error").GetInt64() >= 2, "summary diagnostics");
        }

        var projectRefs = Run("refs", "Mods/B/B.csproj", "--root", workspace.Root, "--json");
        AssertEqual(0, projectRefs.ExitCode, "project refs exit code");
        using (var document = ParseJson(projectRefs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "project_reference"),
                "project reference query");
        }

        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup>
            </Project>
            """);
        var cycle = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, cycle.ExitCode, "dependency cycle index exit code");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            Assert(store.GetEntities().Any(item =>
                item.Kind == "diagnostic" &&
                item.PayloadJson.Contains("cycle detected", StringComparison.OrdinalIgnoreCase)),
                "project cycle diagnostic");
        }

        var modDefinition = Run("definition", "com.test.b", "--root", workspace.Root, "--json");
        AssertEqual(0, modDefinition.ExitCode, "mod definition exit code");
        using (var document = ParseJson(modDefinition.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("mod", item.GetProperty("kind").GetString(), "mod definition kind");
            AssertEqual("com.test.b", item.GetProperty("packageId").GetString(), "mod package ID");
            AssertEqual("Beta", item.GetProperty("name").GetString(), "mod name");
        }

        var noOp = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, noOp.ExitCode, "dependency no-op exit code");
        using (var document = ParseJson(noOp.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("files").GetProperty("unchanged").GetInt32() > 0, "dependency no-op");
        }

        workspace.Write(
            "Mods/A/A.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../B/B.csproj" />
                <PackageReference Include="HarmonyLib" Version="2.3.0" />
              </ItemGroup>
            </Project>
            """);
        var changed = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, changed.ExitCode, "dependency changed index exit code");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            Assert(!store.GetEntities().Any(item =>
                item.Kind == "diagnostic" &&
                item.PayloadJson.Contains("Missing project reference", StringComparison.Ordinal)),
                "stale project diagnostic cleanup");
        }

        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var cycleRemoved = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, cycleRemoved.ExitCode, "dependency cycle cleanup index exit code");

        workspace.Delete("Mods/C/About/About.xml");
        var deleted = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, deleted.ExitCode, "dependency deleted index exit code");
        var afterDelete = Run("summary", "--root", workspace.Root, "--json");
        using (var document = ParseJson(afterDelete.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual(2, data.GetProperty("mods").GetInt64(), "deleted mod cleanup");
            AssertEqual(0, data.GetProperty("diagnostics").GetProperty("error").GetInt64(), "duplicate cleanup");
        }
    }

    private static void HarmonyIndexingAndQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Target.cs",
            """
            namespace Game;
            public class Target
            {
                public Target(string value) { }
                public void Do(int value) { }
                public string Value { get; set; }
            }
            """);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "Assemblies"));
        File.Copy(
            typeof(Program).Assembly.Location,
            Path.Combine(workspace.Root, "Assemblies", "Reference.dll"),
            overwrite: true);
        workspace.Write(
            "Source/Patches.cs",
            """
            using Game;
            [HarmonyPatch(typeof(Game.Target), nameof(Game.Target.Do), new[] { typeof(int) })]
            public static class TargetPatch
            {
                [HarmonyPostfix]
                public static void Postfix() { }

                [HarmonyPatch(typeof(Game.Target), "Value", MethodType.Getter)]
                [HarmonyPrefix]
                public static void Getter() { }

                [HarmonyPatch(typeof(Game.Target), "Value", MethodType.Setter)]
                [HarmonyPostfix]
                public static void Setter() { }

                [HarmonyPatch(typeof(Game.Target), MethodType.Constructor)]
                [HarmonyPrefix]
                public static void ConstructorPatch() { }

                [HarmonyPrepare]
                public static bool Prepare() => true;

                [HarmonyCleanup]
                public static void Cleanup() { }

                [HarmonyTargetMethod]
                public static MethodBase TargetMethod() =>
                    AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do));

                [HarmonyTargetMethods]
                public static IEnumerable<MethodBase> TargetMethods() =>
                    new[] { AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do)) };
            }

            public static class DirectCall
            {
                public static void Apply(Harmony harmony)
                {
                    harmony.Patch(
                        AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do)),
                        postfix: new HarmonyMethod(typeof(DirectCall), nameof(DirectCall.Postfix)));
                }

                public static void Postfix() { }
            }

            [HarmonyPatch(typeof(RimContext.Tests.Program), "Main")]
            public static class AssemblyPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }

            [HarmonyPatch(typeof(MissingType), "Nope")]
            public static class UnresolvedPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var patches = store.GetEntities().Where(item => item.Kind == "harmony_patch").ToArray();
            Assert(patches.Length >= 6, "Harmony patch entities");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"postfix\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"target\":\"Game.Target.Do\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"targetSignature\":[\"int\"]", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"resolved\":true", StringComparison.Ordinal)), "resolved postfix");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target.get_Value\"", StringComparison.Ordinal)), "property getter");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target.set_Value\"", StringComparison.Ordinal)), "property setter");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target..ctor\"", StringComparison.Ordinal)), "constructor patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"target_methods\"", StringComparison.Ordinal)), "target methods");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"prepare\"", StringComparison.Ordinal)), "prepare patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"cleanup\"", StringComparison.Ordinal)), "cleanup patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchClass\":\"DirectCall\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"patchKind\":\"postfix\"", StringComparison.Ordinal)), "direct Harmony call");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchClass\":\"AssemblyPatch\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"resolutionState\":\"resolved\"", StringComparison.Ordinal)),
                "assembly target resolution");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"resolutionState\":\"unresolved\"", StringComparison.Ordinal)), "unresolved target");
            Assert(store.GetRelations().Any(item => item.Kind == "harmony_target_member"), "Harmony member relation");
        }

        var target = Run("harmony", "Game.Target.Do", "--root", workspace.Root, "--json");
        AssertEqual(0, target.ExitCode, "Harmony target query exit code");
        using (var document = ParseJson(target.Stdout))
        {
            var result = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("Game.Target.Do", result.GetProperty("target").GetString(), "Harmony target");
            Assert(result.GetProperty("patches").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "postfix" &&
                item.GetProperty("resolved").GetBoolean()), "Harmony postfix query");
        }

        var refs = Run("refs", "Game.Target.Do", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "Harmony refs exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "harmony_target_member"),
                "Harmony target reference");
        }

        var file = Run(
            "harmony",
            "--file",
            "Source/Patches.cs",
            "--root",
            workspace.Root,
            "--json");
        AssertEqual(0, file.ExitCode, "Harmony file query exit code");
        using (var document = ParseJson(file.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").GetArrayLength() >= 4, "Harmony file results");
        }

        workspace.Write(
            "Source/Patches.cs",
            """
            [HarmonyPatch(typeof(Game.Target), "Do")]
            public static class ReplacementPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }
            """);
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "Harmony incremental changed count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var patches = store.GetEntities().Where(item => item.Kind == "harmony_patch").ToArray();
            AssertEqual(1, patches.Length, "stale Harmony entities removed");
            Assert(patches[0].PayloadJson.Contains("\"patchClass\":\"ReplacementPatch\"", StringComparison.Ordinal),
                "replacement Harmony entity");
            AssertEqual(
                2,
                store.GetRelations().Count(item => item.Kind is "harmony_target" or "harmony_target_member"),
                "stale Harmony relations removed");
        }
    }

    private static void CSharpSemanticEntitiesAndQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Types.cs",
            """
            namespace Test;
            public interface IThing { }
            [Obsolete]
            public partial class MyClass : BaseClass, IThing
            {
                public int Field;
                private static string Property { get; set; }
                public void Run(int value) { }
                public void Run(string value) { }
                public static void Static() { }
                public class Nested { }
            }
            public enum Mode { One, Two }
            """);
        workspace.Write(
            "Source/MyClass.Part.cs",
            """
            namespace Test;
            public partial class MyClass
            {
                public void Part() { }
            }
            """);
        workspace.Write(
            "Source/Use.cs",
            """
            namespace Test;
            public class Use : MyClass
            {
                public void Call()
                {
                    var value = new MyClass();
                    value.Run(1);
                    MyClass.Static();
                }
            }
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var first = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(3, first.Statistics.Added, "C# fixture file count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("Test.MyClass", StringComparison.Ordinal)), "qualified class");
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("Test.MyClass.Nested", StringComparison.Ordinal)), "nested class");
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("\"typeKind\":\"enum\"", StringComparison.Ordinal)), "enum");
            AssertEqual(2, entities.Count(item => item.Kind == "csharp_member" &&
                item.PayloadJson.Contains("\"signature\":\"Run", StringComparison.Ordinal)), "overloaded methods");
            Assert(entities.Any(item => item.Kind == "csharp_member" &&
                item.PayloadJson.Contains("\"name\":\"Part\"", StringComparison.Ordinal)), "partial member");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_interface_implementation"), "interface relation");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_inheritance"), "inheritance relation");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_member_usage"), "member relation");
        }

        var find = Run("find", "MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, find.ExitCode, "C# find exit code");
        using (var document = ParseJson(find.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray()
                .First(item => item.GetProperty("kind").GetString() == "csharp_type");
            AssertEqual("Test.MyClass", item.GetProperty("name").GetString(), "C# find name");
            AssertEqual(6, item.GetProperty("members").GetInt32(), "C# member count");
        }

        var definition = Run("definition", "Test.MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, definition.ExitCode, "C# definition exit code");
        using (var document = ParseJson(definition.Stdout))
        {
            AssertEqual("csharp_type", document.RootElement.GetProperty("results").EnumerateArray()
                .Single().GetProperty("kind").GetString(), "C# definition kind");
        }

        var refs = Run("refs", "Test.MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "C# refs exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "csharp_inheritance"), "C# incoming inheritance");
        }

        var outgoingRefs = Run("refs", "Test.MyClass", "--direction", "out", "--root", workspace.Root, "--json");
        AssertEqual(0, outgoingRefs.ExitCode, "C# outgoing refs exit code");
        using (var document = ParseJson(outgoingRefs.Stdout))
        {
            AssertEqual(0, GetArray(document.RootElement.GetProperty("data"), "incoming").Length,
                "outgoing refs should omit incoming");
            Assert(GetArray(document.RootElement.GetProperty("data"), "outgoing").Length > 0,
                "C# outgoing relations");
        }

        var file = Run("file", "Source/MyClass.Part.cs", "--root", workspace.Root, "--json");
        AssertEqual(0, file.ExitCode, "C# file exit code");
        using (var document = ParseJson(file.Stdout))
        {
            var data = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual(3, data.GetProperty("entityCount").GetInt32(), "partial file entity count");
            Assert(data.GetProperty("entities").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Part"), "partial file member");
        }

        workspace.Write("Source/MyClass.Part.cs", "namespace Test; public partial class MyClass { public void Changed() { } }\n");
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "C# incremental changed count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var payloads = store.GetEntities().Where(item => item.Kind == "csharp_member")
                .Select(item => item.PayloadJson).ToArray();
            Assert(payloads.Any(item => item.Contains("\"name\":\"Changed\"", StringComparison.Ordinal)), "changed member");
            Assert(!payloads.Any(item => item.Contains("\"name\":\"Part\"", StringComparison.Ordinal)), "stale partial member removed");
        }

        workspace.Write("Source/Broken.cs", "public class Broken {");
        var malformed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(2));
        Assert(malformed.Diagnostics?.Any(item => item.Path == "Source/Broken.cs") == true, "C# diagnostic");
    }

    private static TempWorkspace NewIndexedWorkspace()
    {
        var workspace = new TempWorkspace();
        workspace.Write("Source/A.cs", "class A {}\n");
        workspace.Write("Defs/A.xml", "<Defs />\n");
        return workspace;
    }

    private static TempWorkspace NewXmlWorkspace()
    {
        var workspace = new TempWorkspace();
        workspace.Write(
            "Mods/TestMod/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.mod</packageId>
              <name>Test Mod</name>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Weapons.xml",
            """
            <Defs>
              <ThingDef ParentName="BaseWeapon">
                <defName>MyWeapon</defName>
                <thingDef>BaseWeapon</thingDef>
              </ThingDef>
              <ThingDef>
                <defName>BaseWeapon</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Recipes.xml",
            """
            <Defs>
              <RecipeDef>
                <defName>MakeWeapon</defName>
                <thingDef>MyWeapon</thingDef>
              </RecipeDef>
              <ThingDef>
                <defName>Duplicate</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Other.xml",
            """
            <Defs>
              <ThingDef>
                <defName>Duplicate</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Patches/Weapons.xml",
            """
            <Patch>
              <Operation Class="PatchOperationAdd">
                <xpath>/Defs/ThingDef[defName="MyWeapon"]/label</xpath>
                <value>patched weapon</value>
              </Operation>
            </Patch>
            """);
        return workspace;
    }

    private static CliResult Run(params string[] args)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = CliApplication.Run(args, stdout, stderr);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonElement[] GetArray(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];
    }

    private static JsonDocument ParseJson(string output)
    {
        Assert(!string.IsNullOrWhiteSpace(output), "CLI stdout should not be empty");
        return JsonDocument.Parse(output);
    }

    private static T AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string RepositoryFixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Fixture '{name}' was not found from '{AppContext.BaseDirectory}'.");
    }

    private sealed class ThrowingContextProvider : IRimContextBundleProvider
    {
        public string Id => "fixture-provider";

        public ValueTask<RimContextProviderSnapshot> CollectAsync(
            RimContextProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("fixture provider failure");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static void ValidatedRemediationPrecedentSafety()
    {
        using var workspace = new TempWorkspace();
        string storePath = Path.Combine(workspace.Root, "impact-learning.jsonl");
        var identity = new global::RimDev.Contracts.ExecutionIdentity
        {
            RepositoryId = "repo",
            ProjectId = "project",
            SourceRevision = "source-v1"
        };
        var subject = new global::RimDev.Contracts.EntityReference
        {
            Kind = global::RimDev.Contracts.EntityReferenceKinds.Test,
            Id = "runtime-test"
        };
        var precedent = new global::RimDev.Contracts.RemediationPrecedent
        {
            PrecedentId = "precedent-1",
            FailureFamily = "missing-reference",
            Subject = subject,
            Applicability = ["framework:CoreFramework"],
            RootCause = "missing dependency declaration",
            ValidatedRemediation = "restore the direct framework dependency",
            Evidence =
            [
                new global::RimDev.Contracts.EvidenceReference
                {
                    Kind = "validation",
                    Uri = "evidence://validation/pass"
                }
            ],
            SuccessfulValidationIdentity = identity,
            Provenance = new global::RimDev.Contracts.ContractProvenance
            {
                Source = "fixture"
            }
        };
        var store = new ImpactLearningStore(storePath);
        Assert(store.RecordValidatedRemediation(precedent), "validated remediation is stored");
        AssertEqual(
            1,
            store.ReadRemediationPrecedents("missing-reference", subject).Count,
            "matching proven remediation is reusable");
        Assert(
            !store.RecordValidatedRemediation(precedent with { Status = "tentative" }),
            "unvalidated remediation is not promoted");
        AssertEqual(
            1,
            store.ReadRemediationPrecedents("missing-reference", subject).Count,
            "unvalidated remediation remains excluded");
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "rimctx-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void CopyFrom(string sourceRoot)
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var relativeSegments = relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                if (relativeSegments.Any(segment => segment is ".git" or ".rimctx" or "bin" or "obj"))
                {
                    continue;
                }

                var destinationPath = Resolve(relativePath);
                var parent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                System.IO.File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        public void Write(string relativePath, string content)
        {
            var path = Resolve(relativePath);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            System.IO.File.WriteAllText(path, content);
        }

        public void Delete(string relativePath)
        {
            var path = Resolve(relativePath);
            System.IO.File.Delete(path);
        }

        public void Rename(string relativePath, string newRelativePath)
        {
            var source = Resolve(relativePath);
            var destination = Resolve(newRelativePath);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            System.IO.File.Move(source, destination);
        }

        private string Resolve(string relativePath)
        {
            var platformPath = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(Root, platformPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly DateTimeOffset FixedTime = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
}
