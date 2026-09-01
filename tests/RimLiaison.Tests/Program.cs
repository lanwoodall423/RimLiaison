using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;

using RimError.Core;
using RimContext.Core;
using RimContext.Core.Context;
using RimContext.Core.Semantics;
using RimContext.Core.Storage;
using RimLiaison;
using RimLiaison.Catalog;
using RimLiaison.Benchmarking;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.RimError;
using RimLiaison.RimContext;
using RimLiaison.Recovery;
using RimLiaison.Results;
using RimLiaison.Provenance;
using RimLiaison.Validation;

namespace RimLiaison.Tests;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("shared exact identity match", SharedContractsTests.ExactIdentityMatches),
        ("shared source revision mismatch", SharedContractsTests.SourceRevisionMismatchIsRejected),
        ("shared artifact and generation mismatch", SharedContractsTests.ArtifactAndGenerationMismatchesAreRejected),
        ("shared optional identity compatibility", SharedContractsTests.MissingOptionalIdentityIsCompatible),
        ("shared stale evidence rejection", SharedContractsTests.StaleEvidenceIsRejected),
        ("shared contract serialization", SharedContractsTests.SerializationAndVersioningAreStable),
        ("shared legacy evidence adapter", SharedContractsTests.LegacyValidationEvidenceAdapts),
        ("shared bounded payloads", SharedContractsTests.PayloadsAndEventsAreBounded),
        ("shared dependency direction", SharedContractsTests.SharedAssemblyHasNoToolDependencies),
        ("valid catalog", ValidCatalogLoads),
        ("production manifest validates owner metadata", ProjectMetadataOwnershipTests.ProductionManifestValidatesOwnerMetadata),
        ("missing production metadata fails closed", ProjectMetadataOwnershipTests.MissingProductionMetadataFailsClosed),
        ("contradictory production metadata fails closed", ProjectMetadataOwnershipTests.ContradictoryProductionMetadataFailsClosed),
        ("materializer uses owning manifest", ProjectMetadataOwnershipTests.MaterializerUsesOwningManifestNotToolingCatalog),
        ("DRF project-owned metadata passes", ProjectMetadataOwnershipTests.DrfProjectOwnedMetadataPasses),
        ("Frontier project-owned metadata passes", ProjectMetadataOwnershipTests.FrontierProjectOwnedMetadataPasses),
        ("Insight Canvas project-owned metadata passes", ProjectMetadataOwnershipTests.InsightCanvasProjectOwnedMetadataPasses),
        ("wrong metadata owner fails closed", ProjectMetadataOwnershipTests.WrongMetadataOwnerFailsClosed),
        ("project-owned recipe resolves", ValidationRecipeResolverTests.ProjectOwnedRecipeResolves),
        ("project-owned recipe resolves by convention", ValidationRecipeResolverTests.ProjectOwnedRecipeResolvesByConvention),
        ("builtin recipe resolves", ValidationRecipeResolverTests.BuiltinRecipeResolves),
        ("missing recipe returns structured failure", ValidationRecipeResolverTests.MissingRecipeReturnsStructuredFailure),
        ("project recipe does not require central runtime", ValidationRecipeResolverTests.ProjectRecipeDoesNotRequireCentralRuntime),
        ("relative recipe path survives repository move", ValidationRecipeResolverTests.RelativeRecipePathSurvivesRepositoryMove),
        ("recipe id traversal is rejected", ValidationRecipeResolverTests.RecipeIdTraversalIsRejected),
        ("absolute recipe path is rejected", ValidationRecipeResolverTests.AbsoluteRecipePathIsRejected),
        ("foreign project recipe is rejected", ValidationRecipeResolverTests.ForeignProjectRecipeIsRejected),
        ("ambiguous legacy ownership fails closed", ValidationRecipeResolverTests.AmbiguousLegacyOwnershipFailsClosed),
        ("legacy recipe resolves deterministically", ValidationRecipeResolverTests.LegacyProjectRecipeMigratesDeterministically),
        ("recipe hash and schema are recorded", ValidationRecipeResolverTests.RecipeHashAndSchemaAreRecorded),
        ("ecosystem recipes resolve", ValidationRecipeResolverTests.EcosystemRecipesResolve),
        ("ecosystem recipes resolve by convention", ValidationRecipeResolverTests.EcosystemRecipesResolveByConvention),
        ("managed project identity map explicit", CanonicalProjectIdentityTests.ManagedProjectIdentityMapIsExplicit),
        ("DRF display and routing share canonical identity", CanonicalProjectIdentityTests.DrfDisplayNameAndRoutingSlugResolveSameCanonical),
        ("Frontier identifiers resolve correctly", CanonicalProjectIdentityTests.FrontierIdentifiersResolveCorrectly),
        ("Insight Canvas identifiers resolve correctly", CanonicalProjectIdentityTests.InsightCanvasIdentifiersResolveCorrectly),
        ("exact canonical project ID passes", CanonicalProjectIdentityTests.ExactCanonicalIdPasses),
        ("registered explicit alias passes", CanonicalProjectIdentityTests.RegisteredExplicitAliasPasses),
        ("unregistered alias fails", CanonicalProjectIdentityTests.UnregisteredAliasFails),
        ("wrong project fails", CanonicalProjectIdentityTests.WrongProjectFails),
        ("package identity conflict fails", CanonicalProjectIdentityTests.PackageIdentityConflictFails),
        ("metadata owner conflict fails", CanonicalProjectIdentityTests.MetadataOwnerConflictFails),
        ("forged owner fails", CanonicalProjectIdentityTests.ForgedOwnerFails),
        ("temporary contract cannot change identity", CanonicalProjectIdentityTests.TemporaryContractPathCannotChangeIdentity),
        ("runtime folder cannot override identity", CanonicalProjectIdentityTests.RuntimeFolderCannotOverrideIdentity),
        ("CLI slug cannot claim another project", CanonicalProjectIdentityTests.CliSlugCannotClaimAnotherProject),
        ("auto-enrollment preserves canonical identity", CanonicalProjectIdentityTests.AutoEnrollmentPreservesCanonicalIdentity),
        ("cross-stack materialization preserves canonical identity", CanonicalProjectIdentityTests.CrossStackMaterializationPreservesCanonicalIdentity),
        ("deployment mapping retains canonical identity", CanonicalProjectIdentityTests.DeploymentMappingRetainsCanonicalIdentity),
        ("observability records canonical identity", CanonicalProjectIdentityTests.ObservabilityRecordsCanonicalIdentity),
        ("promotion requires package", ToolchainPromotionTests.PromotionRequiresPackage),
        ("malformed promotion package fails closed", ToolchainPromotionTests.MalformedPromotionPackageFailsClosed),
        ("static promotion path does not acquire lease", ToolchainPromotionTests.StaticPromotionPathDoesNotAcquireLease),
        ("qualification hash mismatch fails closed", ToolchainPromotionTests.QualificationHashMismatchFailsClosed),
        ("incomplete qualification fails closed", ToolchainPromotionTests.IncompleteQualificationFailsClosed),
        ("different candidate hashes use different payload identity", ToolchainPromotionTests.DifferentCandidateHashesUseDifferentPayloadIdentity),
        ("candidate package is immutable without installed runtime", ToolchainPromotionTests.CandidatePackageIsImmutableExactWithoutInstalledRuntime),
        ("bootstrap healthy production candidate succeeds", PromotionBootstrapHealthTests.HealthyProductionHealthyCandidateSuccess),
        ("bootstrap replaces missing legacy runtime", PromotionBootstrapHealthTests.MissingLegacyRuntimeReplacement),
        ("bootstrap replaces corrupt legacy runtime", PromotionBootstrapHealthTests.CorruptLegacyRuntimeReplacement),
        ("bootstrap replaces unrecoverable legacy promotion", PromotionBootstrapHealthTests.UnrecoverableLegacyReplacement),
        ("bootstrap failed candidate rolls back missing legacy", PromotionBootstrapHealthTests.MissingLegacyUnhealthyCandidateRollback),
        ("bootstrap health binds candidate root", PromotionBootstrapHealthTests.CandidateRootHealthBinding),
        ("isolated candidate uses narrow capabilities probe", PromotionBootstrapHealthTests.IsolatedCandidateUsesNarrowCapabilitiesProbe),
        ("candidate capabilities probe failure blocks health", PromotionBootstrapHealthTests.CandidateCapabilitiesProbeFailureBlocksHealth),
        ("candidate capabilities rejects wrong runtime identity", PromotionBootstrapHealthTests.CandidateCapabilitiesRejectsWrongRuntimeIdentity),
        ("candidate DevBridge health failures block candidate", PromotionBootstrapHealthTests.CandidateDevBridgeHealthFailuresBlockCandidate),
        ("bootstrap rejects unrelated healthy installation", PromotionBootstrapHealthTests.UnrelatedHealthyInstallationCannotSatisfyIdentity),
        ("bootstrap staged coordinator quiescence requires absent process", PromotionBootstrapHealthTests.StagedCoordinatorQuiescenceRequiresAbsentProcess),
        ("bootstrap missing candidate DLL fails", PromotionBootstrapHealthTests.MissingCandidateDllFailsAccurately),
        ("bootstrap corrupt coordinator fails", PromotionBootstrapHealthTests.CorruptCandidateCoordinatorFailsAccurately),
        ("bootstrap fingerprint mismatch fails", PromotionBootstrapHealthTests.CandidateFingerprintMismatchFails),
        ("bootstrap post-commit doctor sees new fingerprint", PromotionBootstrapHealthTests.PostCommitDoctorResolvesNewFingerprint),
        ("bootstrap post-commit failure rolls back", PromotionBootstrapHealthTests.PostCommitDoctorFailureRollsBack),
        ("bootstrap failed promotion preserves identity", PromotionBootstrapHealthTests.FailedPromotionNeverChangesActiveIdentity),
        ("bootstrap concurrent promotions lock safely", PromotionBootstrapHealthTests.ConcurrentPromotionsAreLockSafe),
        ("bootstrap cancellation rolls back deterministically", PromotionBootstrapHealthTests.CancellationLeavesDeterministicRollbackState),
        ("bootstrap generated qualification output does not block", PromotionBootstrapHealthTests.GeneratedQualificationOutputDoesNotBlockPromotion),
        ("bootstrap meaningful source change blocks", PromotionBootstrapHealthTests.MeaningfulSourceChangeBlocksPromotion),
        ("bootstrap unknown tracked artifact blocks", PromotionBootstrapHealthTests.UnknownTrackedArtifactBlocksPromotion),
        ("bootstrap HEAD mismatch blocks", PromotionBootstrapHealthTests.HeadMismatchStillBlocksPromotion),
        ("isolated DevBridge checkout uses explicit managed path", ToolchainCandidateTests.IsolatedDevBridgeCheckoutUsesExplicitManagedPath),
        ("normal DevBridge checkout uses explicit managed path", ToolchainCandidateTests.NormalCheckoutUnderModsUsesExplicitManagedPath),
        ("valid RimWorld root allows candidate build", ToolchainCandidateTests.ValidRimWorldRootAllowsBuildToProceed),
        ("missing Assembly-CSharp fails before build", ToolchainCandidateTests.MissingAssemblyCSharpFailsBeforeBuild),
        ("missing Unity CoreModule fails before build", ToolchainCandidateTests.MissingUnityCoreModuleFailsBeforeBuild),
        ("wrong RimWorld root fails clearly", ToolchainCandidateTests.WrongConfiguredRootFailsClearly),
        ("current RimWorld installation resolves managed directory", ToolchainCandidateTests.CurrentRimWorldInstallationResolvesToManagedDirectory),
        ("release arguments pass exact managed directory", ToolchainCandidateTests.ReleaseArgumentsPassExactManagedDirectory),
        ("candidate evidence leaves project unimplicated", ToolchainCandidateTests.CandidateEvidenceMarksProjectUnimplicated),
        ("promotion lease acquires canonical ownership", PromotionLeaseOrchestrationTests.AcquiresCanonicalLease),
        ("promotion lease reaches live verification", PromotionLeaseOrchestrationTests.ForwardsLeaseIdToLiveVerification),
        ("promotion lease preserves workflow owner", PromotionLeaseOrchestrationTests.ForwardsWorkflowIdentityToLeaseAndVerification),
        ("promotion success releases lease", PromotionLeaseOrchestrationTests.SuccessfulVerificationReleasesLease),
        ("promotion live failure releases lease", PromotionLeaseOrchestrationTests.FailedVerificationReleasesLease),
        ("promotion live exception releases lease", PromotionLeaseOrchestrationTests.ExceptionReleasesLease),
        ("promotion cancellation releases lease", PromotionLeaseOrchestrationTests.CancellationReleasesLease),
        ("promotion generation mismatch reacquires once", PromotionLeaseOrchestrationTests.GenerationMismatchReacquiresOnce),
        ("promotion generation reacquisition is bounded", PromotionLeaseOrchestrationTests.GenerationMismatchIsBounded),
        ("promotion capability generation change reacquires", PromotionLeaseOrchestrationTests.CapabilityGenerationChangeReacquires),
        ("promotion lease release failure blocks", PromotionLeaseOrchestrationTests.LeaseReleaseFailureBlocksPromotion),
        ("promotion lease acquisition failure stops live call", PromotionLeaseOrchestrationTests.AcquireFailureDoesNotCallLiveVerification),
        ("promotion requires workflow identity", PromotionLeaseOrchestrationTests.MissingWorkflowIdentityFailsBeforeLease),
        ("promotion leases remain workflow isolated", PromotionLeaseOrchestrationTests.OtherWorkflowCannotReuseLease),
        ("promotion reports lease evidence", PromotionLeaseOrchestrationTests.ReportsLeaseGenerationAndStage),
        ("missing project runtime root fails closed", ProjectMetadataOwnershipTests.MissingRuntimeRootFailsClosed),
        ("production binding requires exact installed identity", ProjectMetadataOwnershipTests.ProductionBindingRequiresExactInstalledIdentity),
        ("healthy promoted installation needs no repair", PromotedToolchainRecoveryTests.HealthyInstallationDoesNotRepair),
        ("legacy promotion migrates and restores", PromotedToolchainRecoveryTests.LegacyPromotionMigratesAndRestores),
        ("legacy promotion without exact material blocks safely", PromotedToolchainRecoveryTests.LegacyPromotionWithoutExactMaterialBlocksSafely),
        ("modern promotion skips migration", PromotedToolchainRecoveryTests.ModernPromotionSkipsMigration),
        ("legacy candidate selection requires exact identity", PromotedToolchainRecoveryTests.LegacyCandidateSelectionRequiresExactIdentity),
        ("missing promoted artifact repairs", PromotedToolchainRecoveryTests.MissingArtifactRepairsAndPreservesIdentity),
        ("restoration ignores current source divergence", PromotedToolchainRecoveryTests.RecoveryIgnoresCurrentSourceDivergence),
        ("local Release cannot substitute promoted payload", PromotedToolchainRecoveryTests.LocalReleaseCannotSubstitutePromotedPayload),
        ("missing recovery payload blocks infrastructure", PromotedToolchainRecoveryTests.MissingRecoveryPayloadBlocksInfrastructure),
        ("invalid recovery payload blocks infrastructure", PromotedToolchainRecoveryTests.InvalidRecoveryPayloadBlocksInfrastructure),
        ("corrupt promoted artifact repairs", PromotedToolchainRecoveryTests.CorruptArtifactRepairs),
        ("missing runtime artifact repairs", PromotedToolchainRecoveryTests.MissingRuntimeArtifactRepairs),
        ("missing coordinator repairs", PromotedToolchainRecoveryTests.MissingCoordinatorRepairs),
        ("runtime hash mismatch repairs", PromotedToolchainRecoveryTests.RuntimeHashMismatchRepairs),
        ("concurrent promoted repair is shared", PromotedToolchainRecoveryTests.ConcurrentRecoveryHasOneEffectiveRepair),
        ("authoritative package repairs", PromotedToolchainRecoveryTests.AuthoritativePackageRepairs),
        ("unavailable promoted package blocks infrastructure", PromotedToolchainRecoveryTests.UnavailablePackageIsInfrastructureBlock),
        ("experimental replacement is rejected", PromotedToolchainRecoveryTests.ExperimentalReplacementIsRejected),
        ("promoted repair is bounded", PromotedToolchainRecoveryTests.SameIntegrityFailureIsBounded),
        ("project failure skips promoted repair", PromotedToolchainRecoveryTests.ProjectFailureIsNotToolchainRepair),
        ("external DevBridge runtime missing uses promoted recovery", PromotedToolchainRecoveryTests.ExternalRuntimeMissingUsesPromotedRecovery),
        ("production freshness recovery retries interrupted operation", PromotedToolchainRecoveryTests.ProductionFreshnessRecoveryRetriesInterruptedOperation),
        ("production freshness recovery preserves project failure", PromotedToolchainRecoveryTests.ProductionFreshnessRecoveryPreservesProjectFailure),
        ("production freshness recovery bounds repeated integrity failure", PromotedToolchainRecoveryTests.ProductionFreshnessRecoveryBoundsRepeatedIntegrityFailure),
        ("source-relative project runtime root fails closed", ProjectMetadataOwnershipTests.SourceRuntimeRootFailsClosed),
        ("new project auto-enrolls runtime root", RuntimeRootEnrollmentTests.NewValidProjectAutoEnrolls),
        ("already enrolled project is unchanged", RuntimeRootEnrollmentTests.AlreadyEnrolledProjectIsUnchanged),
        ("stale source path is refreshed safely", RuntimeRootEnrollmentTests.StaleSourcePathIsRefreshedSafely),
        ("RimWorld move updates derived runtime root", RuntimeRootEnrollmentTests.RimWorldMoveUpdatesDerivedRoot),
        ("runtime root never becomes source root", RuntimeRootEnrollmentTests.RuntimeRootNeverBecomesSourceRoot),
        ("runtime root outside Mods is rejected", RuntimeRootEnrollmentTests.RuntimeRootOutsideModsIsRejected),
        ("duplicate runtime ownership fails closed", RuntimeRootEnrollmentTests.TwoProjectsClaimingRuntimeRootFailClosed),
        ("ambiguous runtime identity fails closed", RuntimeRootEnrollmentTests.AmbiguousRuntimeFolderIdentityFailsClosed),
        ("missing RimWorld root fails early", RuntimeRootEnrollmentTests.MissingRimWorldRootFailsEarly),
        ("concurrent enrollment is idempotent", RuntimeRootEnrollmentTests.ConcurrentEnrollmentIsIdempotent),
        ("project metadata remains portable", RuntimeRootEnrollmentTests.ProjectMetadataRemainsPortable),
        ("successful self-heal supplies materializer root", RuntimeRootEnrollmentTests.SuccessfulSelfHealProvidesMaterializerRoot),
        ("failed self-heal returns structured reason", RuntimeRootEnrollmentTests.FailedSelfHealReturnsStructuredReason),
        ("workspace audit reports healthy projects", RuntimeRootEnrollmentTests.WorkspaceAuditReportsHealthyProjects),
        ("workspace audit reports and repairs missing enrollment", RuntimeRootEnrollmentTests.WorkspaceAuditRepairsMissingEnrollment),
        ("workspace audit repairs stale runtime mapping", RuntimeRootEnrollmentTests.WorkspaceAuditRepairsStaleRuntimeMapping),
        ("workspace audit isolates blocked project", RuntimeRootEnrollmentTests.WorkspaceAuditIsolatesBlockedProject),
        ("workspace audit reports disappeared project", RuntimeRootEnrollmentTests.WorkspaceAuditReportsDisappearedProject),
        ("clean whole-mod package materializes contract", RuntimeRootEnrollmentTests.CleanWholeModPackageMaterializesContract),
        ("workspace enrollment survives process restart", RuntimeRootEnrollmentTests.WorkspaceEnrollmentSurvivesProcessRestart),
        ("concurrent process enrollment is idempotent", RuntimeRootEnrollmentTests.ConcurrentProcessEnrollmentIsIdempotent),
        ("duplicate project identity fails before enrollment", RuntimeRootEnrollmentTests.DuplicateProjectIdentityFailsBeforeEnrollment),
        ("malformed About.xml fails before enrollment", RuntimeRootEnrollmentTests.MalformedAboutXmlFailsBeforeEnrollment),
        ("source under Mods fails before enrollment", RuntimeRootEnrollmentTests.SourceUnderModsFailsBeforeEnrollment),
        ("workspace enrollment process probe", RuntimeRootEnrollmentTests.WorkspaceEnrollmentProcessProbe),
        ("context uses canonical stack discovery", ContextUsesCanonicalStackDiscovery),
        ("git context separates descriptor recovery state", GitContextSeparatesDescriptorRecoveryState),
        ("context provider projects owner state", ContextProviderProjectsOwnerState),
        ("context keeps unavailable providers scoped", ContextKeepsUnavailableProvidersScoped),
        ("context projects RimError structured store", ContextProjectsRimErrorStructuredStore),
        ("context provider projects synchronized owner evidence", ContextProviderProjectsSynchronizedOwnerEvidence),
        ("validation evidence identity is immutable and relevant", ValidationEvidenceIdentityIsImmutableAndRelevant),
        ("publication gate rejects newly selected tests", PublicationGateRejectsNewlySelectedTests),
        ("golden workflow benchmark matches baseline", GoldenWorkflowBenchmarkMatchesBaseline),
        ("observability retains bounded parallel identities", ObservabilityRetainsBoundedParallelIdentities),
        ("observed performance stays separate from benchmarks", ObservedPerformanceStaysSeparateFromBenchmarks),
        ("observed performance reports insufficient data", ObservedPerformanceReportsInsufficientData),
        ("real workflow telemetry aggregates bounded events", ObservabilityTests.RealWorkflowTelemetryAggregatesBoundedEvents),
        ("canonical projection newer failure preserves validation", ProjectObservabilityProjectionTests.NewerFailedCommandDoesNotReplaceSuccessfulValidation),
        ("canonical projection recovered infrastructure", ProjectObservabilityProjectionTests.RecoveredInfrastructureFailureIsAHealthyProjectWithFinding),
        ("canonical projection active working", ProjectObservabilityProjectionTests.ActiveProjectIsWorking),
        ("canonical projection inactive healthy", ProjectObservabilityProjectionTests.InactiveSuccessfulProjectIsHealthy),
        ("canonical projection stale session", ProjectObservabilityProjectionTests.StaleSessionNeedsAttention),
        ("canonical projection abandoned session", ProjectObservabilityProjectionTests.AbandonedSessionNeedsAttention),
        ("canonical projection capped timeline", ProjectObservabilityProjectionTests.TimelineRetainsLatestMeaningfulActivityWhenCapped),
        ("canonical projection incomplete history", ProjectObservabilityProjectionTests.IncompleteHistoryIsNotInferredHealthy),
        ("canonical projection excludes qualification", ProjectObservabilityProjectionTests.QualificationActivityDoesNotCreateProductionProject),
        ("canonical projection aggregates sessions", ProjectObservabilityProjectionTests.MultipleSessionsAggregateUnderOneProject),
        ("canonical projection separates workers", ProjectObservabilityProjectionTests.ConcurrentLogicalWorkersRemainDistinct),
        ("canonical projection timeline contradiction", ProjectObservabilityProjectionTests.OverviewAndProjectTimelineShareT2),
        ("canonical projection successful recovered tooling", ProjectObservabilityProjectionTests.SuccessfulTaskWithRecoveredToolingFailureRemainsHealthy),
        ("canonical projection one finding occurrence per issue", ProjectObservabilityProjectionTests.FindingWithRecoveryEventsRemainsOneOccurrence),
        ("canonical projection ordinary retry is not a finding", ProjectObservabilityProjectionTests.OrdinaryRetryDoesNotCreateToolingFinding),
        ("canonical projection repeated retry finding", ProjectObservabilityProjectionTests.RepeatedRetryCreatesOneFindingGroup),
        ("canonical projection successful workaround", ProjectObservabilityProjectionTests.SuccessfulWorkaroundCreatesToolingFinding),
        ("canonical projection recurring tooling finding", ProjectObservabilityProjectionTests.RepeatedToolingShortcomingAggregatesAcrossProjectsAndKeepsOccurrences),
        ("canonical projection bounded finding count", ProjectObservabilityProjectionTests.FindingCountSurvivesBoundedOccurrenceRetention),
        ("canonical projection project and tooling outcomes", ProjectObservabilityProjectionTests.ProjectFailureAndToolingFindingStaySeparate),
        ("canonical projection project failure only", ProjectObservabilityProjectionTests.ProjectFailureWithoutToolingFindingIsSeparate),
        ("canonical projection tooling failure only", ProjectObservabilityProjectionTests.ToolingFailureWithoutProjectProblemRemainsIndependent),
        ("canonical projection missing evidence", ProjectObservabilityProjectionTests.MissingEvidenceProducesUnknown),
        ("canonical projection terminal snapshot without evidence", ProjectObservabilityProjectionTests.TerminalSnapshotWithoutMeaningfulEvidenceIsUnknown),
        ("canonical projection ambiguous session is isolated", ProjectObservabilityProjectionTests.AmbiguousSessionIdentityDoesNotCrossContaminateProjects),
        ("canonical projection distinct findings", ProjectObservabilityProjectionTests.SimilarDistinctFindingsDoNotCollapse),
        ("canonical projection UI overview/detail agreement", ProjectObservabilityProjectionTests.UiOverviewAndProjectDetailUseSameCanonicalProjection),
        ("canonical projection terminal failure", ProjectObservabilityProjectionTests.FailedTerminalSnapshotNeedsAttentionWithoutAttemptEvent),
        ("canonical projection assessment evidence", ProjectObservabilityProjectionTests.ToolingAssessmentPreservesObservedEvidenceAndDerivedOwner),
        ("canonical projection recovered production-toolchain assessment", ProjectObservabilityProjectionTests.RecoveredProductionToolchainAssessmentContainsRepairEvidence),
        ("canonical projection unkeyed findings", ProjectObservabilityProjectionTests.UnkeyedSimilarFindingsRemainSeparate),
        ("canonical projection reevaluates staleness", ProjectObservabilityProjectionTests.UiProjectionReevaluatesStalenessAsTimeAdvances),
        ("canonical projection optional tooling gap", ProjectObservabilityProjectionTests.OptionalToolingGapDoesNotFailSuccessfulProject),
        ("tooling assessment clipboard success", ToolingAssessmentHandoffTests.ClipboardSuccessIsAutomatic),
        ("tooling assessment clipboard fallback", ToolingAssessmentHandoffTests.ClipboardFailureFallsBackToExport),
        ("tooling assessment payload fallback", ToolingAssessmentHandoffTests.ExcessivePayloadFallsBackWithoutClipboardAttempt),
        ("tooling assessment critical evidence fallback", ToolingAssessmentHandoffTests.CriticalOmissionForcesExport),
        ("tooling assessment bounded gap clipboard", ToolingAssessmentHandoffTests.BoundedNoncriticalGapAllowsClipboard),
        ("tooling assessment transport equivalence", ToolingAssessmentHandoffTests.ClipboardAndExportContainEquivalentAssessment),
        ("tooling assessment redaction", ToolingAssessmentHandoffTests.HandoffRedactsSecrets),
        ("tooling assessment occurrence handoff", ToolingAssessmentHandoffTests.AssessmentCanBeBuiltFromOccurrence),
        ("destination presenter canonical overview and project", ObservabilityDestinationPresenterTests.OverviewAndProjectUseCanonicalProjectState),
        ("destination presenter owner-focused problems", ObservabilityDestinationPresenterTests.ProblemsExposeOwnerFocusedAction),
        ("reliability projection evaluates production campaigns", ReliabilityTests.ProductionCampaignProjection),
        ("reliability UI projection and campaign controls", ReliabilityTests.PromptTwoReliabilitySurface),

        ("qualification harness covers deterministic contract", QualificationTests.HarnessCoversDeterministicContract),
        ("production recommendation remains non-blocking", QualificationTests.ProductionFirstRecommendationDoesNotFailModWorkflow),
        ("required capability can fail Golden Path", QualificationTests.RequiredCapabilityCanFailGoldenPath),
        ("qualification recommendations project to backlog", QualificationTests.QualificationBacklogProjectsStructuredRecommendations),
        ("runner timeout contains a hung child", RunnerTimeoutContainsHungChild),
        ("failure knowledge matches generated state", FailureKnowledgeMatchesGeneratedState),
        ("context exposes provenance benchmarks and failure knowledge", ContextExposesProvenanceBenchmarksAndFailureKnowledge),
        ("isolation metadata validates safe defaults", IsolationMetadataValidatesSafeDefaults),
        ("duplicate ids fail", DuplicateIdsFail),
        ("missing references fail", MissingReferencesFail),
        ("suite cycles fail", SuiteCyclesFail),
        ("missing recipes fail", MissingRecipesFail),
        ("recipe list loads", RecipeListLoads),
        ("list is minimal and sorted", ListIsMinimalAndSorted),
        ("environment fallback leaves list usable", EnvironmentFallbackLeavesListUsable),
        ("environment fallback leaves run usable", EnvironmentFallbackLeavesRunUsable),
        ("environment fallback leaves doctor usable", EnvironmentFallbackLeavesDoctorUsable),
        ("doctor preserves structured DevBridge failure", DoctorPreservesStructuredDevBridgeFailure),
        ("DevBridge process evidence is retained", DevBridgeProcessEvidenceIsRetained),
        ("DevBridge batch launcher executes on Windows", DevBridgeBatchLauncherExecutesOnWindows),
        ("DevBridge root selects its batch launcher", DevBridgeRootSelectsBatchLauncher),
        ("show exposes metadata", ShowExposesMetadata),
        ("missing run uses not-found contract", MissingRunUsesNotFoundContract),
        ("missing show and suite commands use not-found exit code", MissingShowAndSuiteCommandsUseNotFoundExitCode),
        ("suite run parse errors are not single-test results", SuiteRunParseErrorsAreNotSingleTestResults),
        ("suite and validation commands work", SuiteAndValidationCommandsWork),
        ("invalid catalog fails before command output", InvalidCatalogFailsBeforeCommand),
        ("catalog test delegates recipe id", CatalogTestDelegatesRecipeId),
        ("workflow correlation reaches DevBridge", WorkflowCorrelationReachesDevBridge),
        ("old DevBridge responses remain compatible", OldDevBridgeResponsesRemainCompatible),
        ("old DevBridge request parsers remain compatible", OldDevBridgeRequestParsersRemainCompatible),
        ("mismatched workflow ids fail closed", MismatchedWorkflowIdsFailClosed),
        ("catalog run CLI delegates execution", CatalogRunCliDelegatesExecution),
        ("catalog run acquires and propagates a lease", CatalogRunAcquiresAndPropagatesLease),
        ("run result categories are compact", RunResultCategoriesAreCompact),
        ("compact final output includes workflow id", CompactFinalOutputIncludesWorkflowId),
        ("agent output contracts are golden and bounded", AgentOutputContractsAreGoldenAndBounded),
        ("RimError diagnosis reads the Core store", RimErrorDiagnosisIsNormalized),
        ("DevBridge diagnostic source is bounded and generation scoped", DevBridgeDiagnosticSourceIsBoundedAndGenerationScoped),
        ("automatic diagnostics carry scoped identities", AutomaticDiagnosticsCarryScopedIdentities),
        ("normal CLI failure acquires diagnostics automatically", NormalCliFailureAcquiresDiagnosticsAutomatically),
        ("successful test skips diagnostic acquisition", SuccessfulTestSkipsDiagnosticAcquisition),
        ("stale diagnostic source cannot produce trustworthy result", StaleDiagnosticSourceCannotProduceTrustworthyResult),
        ("scoped RimError diagnosis filters nearby runs", ScopedRimErrorDiagnosisFiltersNearbyRuns),
        ("RimError scoped source uses Core in memory", RimErrorScopedSourceUsesCore),
        ("diagnostic failure preserves test failure", DiagnosticFailurePreservesTestFailure),
        ("recipe CLI delegates structured plan", RecipeCliDelegatesStructuredPlan),
        ("plan preserves no-launch result", PlanPreservesNoLaunchResult),
        ("successful recipe run is normalized", SuccessfulRecipeRunIsNormalized),
        ("recipe assertion failure is classified", RecipeAssertionFailureIsClassified),
        ("DevBridge refusal is classified", DevBridgeRefusalIsClassified),
        ("infrastructure failure is classified", InfrastructureFailureIsClassified),
        ("timeout is classified", TimeoutIsClassified),
        ("cancellation is classified", CancellationIsClassified),
        ("malformed response is classified", MalformedResponseIsClassified),
        ("incompatible schema is classified", IncompatibleSchemaIsClassified),
        ("validation chain build failure is infrastructure", ValidationChainDiagnosisTests.DevelopmentBuildFailureIsInfrastructure),
        ("validation chain freshness failure is infrastructure", ValidationChainDiagnosisTests.ArtifactFreshnessFailureIsInfrastructure),
        ("validation chain readiness identity failure is infrastructure", ValidationChainDiagnosisTests.ReadinessIdentityFailureIsInfrastructure),
        ("validation chain lease failure is infrastructure", ValidationChainDiagnosisTests.LeaseFailureIsInfrastructure),
        ("validation chain runtime timeout is infrastructure", ValidationChainDiagnosisTests.RuntimeTimeoutIsInfrastructure),
        ("validation chain project assertion is project failure", ValidationChainDiagnosisTests.ProjectAssertionFailureIsProjectFailure),
        ("validation chain success is pass", ValidationChainDiagnosisTests.CompleteChainIsPass),
        ("production failure ownership table is bounded", ProductionExecutionTests.FailureOwnershipTableIsBounded),
        ("project failure normalizes to MOD_FAILURE", ProductionExecutionTests.ProjectFailureNormalizesToModFailure),
        ("project build failure normalizes to MOD_FAILURE", ProductionExecutionTests.ProjectBuildFailureNormalizesToModFailure),
        ("explicit build owner controls classification", ProductionExecutionTests.ExplicitBuildOwnerControlsClassification),
        ("recovery evidence is counted by cycle type", ProductionExecutionTests.RecoveryEvidenceIsCountedByCycleType),
        ("agent outcome model has only three values", ProductionExecutionTests.AgentOutcomeModelHasOnlyThreeValues),
        ("reconnect recovery stops at reconcile", ManagedRuntimeEscalationTests.ReconnectRestoresServiceWithoutReset),
        ("coordinator recycle recovery restores service", ManagedRuntimeEscalationTests.CoordinatorRecycleRestoresService),
        ("full reset recovery restores service", ManagedRuntimeEscalationTests.FullResetRestoresServiceAfterCoordinatorFailure),
        ("unsafe checkpoint does not escalate", ManagedRuntimeEscalationTests.UnsafeCheckpointDoesNotEscalate),
        ("ambiguous identity fails closed", ManagedRuntimeEscalationTests.AmbiguousPromotedIdentityFailsClosed),
        ("recovery evidence is structured and bounded", ManagedRuntimeEscalationTests.RecoveryEvidenceIsStructuredAndBounded),
        ("recovery uses bounded stage budgets", ManagedRuntimeEscalationTests.RecoveryUsesBoundedStageBudgets),
        ("restart predicate requires completed process", ManagedRuntimeEscalationTests.RestartPredicateRequiresCompletedProcess),
        ("artifact transaction uses shared recovery once", ManagedRuntimeEscalationTests.ArtifactTransactionUsesSharedRecoveryOnce),
        ("RimContext direct coverage selects a test", RimContextDirectCoverageSelectsTest),
        ("RimContext transitive coverage selects a test", RimContextTransitiveCoverageSelectsTest),
        ("RimContext shared coverage is deduplicated", RimContextSharedCoverageIsDeduplicated),
        ("RimContext no impact is conservative", RimContextNoImpactSelectsNoTests),
        ("RimContext unknown impact uses fallback", RimContextUnknownImpactUsesFallback),
        ("RimContext unavailable is conservative", RimContextUnavailableIsConservative),
        ("RimContext selection ordering is deterministic", RimContextSelectionOrderingIsDeterministic),
        ("RimContext adapter uses Core", RimContextAdapterUsesCore),
        ("RimContext complete index stays ready", RimContextCompleteIndexStaysReady),
        ("RimContext partial index is recovered once", RimContextPartialIndexIsRecoveredOnce),
        ("RimContext failed recovery preserves diagnostics", RimContextFailedRecoveryPreservesDiagnostics),
        ("RimContext cancellation is bounded", RimContextCancellationIsBounded),
        ("affected CLI emits compact selection", AffectedCliEmitsCompactSelection),
        ("affected run pass is compact", AffectedRunPassIsCompact),
        ("affected zero impact uses fallback", AffectedZeroImpactUsesFallback),
        ("affected changed path without fallback blocks", AffectedChangedPathWithoutFallbackBlocks),
        ("suite all-pass aggregation is compact", SuiteAllPassAggregationIsCompact),
        ("empty suite execution is conservative", EmptySuiteExecutionIsConservative),
        ("suite one failure is summarized", SuiteOneFailureIsSummarized),
        ("suite multiple failures are deterministic", SuiteMultipleFailuresAreDeterministic),
        ("suite fail-fast stops after first failure", SuiteFailFastStopsAfterFirstFailure),
        ("suite fail-fast pass is complete", SuiteFailFastPassExecutesEverySelectedTest),
        ("fail-fast ordering surfaces cheap historical failures", FailFastOrderingTests.HistoricallyFailureProneCheapTestsMoveEarlier),
        ("fail-fast ordering moves expensive stable tests later", FailFastOrderingTests.ExpensiveStableTestsMoveLater),
        ("fail-fast ordering falls back without history", FailFastOrderingTests.NoHistoryFallsBackDeterministically),
        ("fail-fast ordering ignores corrupt stale and incompatible history", FailFastOrderingTests.CorruptStaleAndIncompatibleHistoryIsIgnored),
        ("fail-fast ordering falls back with insufficient history", FailFastOrderingTests.InsufficientHistoryFallsBackDeterministically),
        ("fail-fast ordering ignores partial group history", FailFastOrderingTests.PartialHistoryDoesNotPreferOneGroupMember),
        ("fail-fast ordering preserves selected membership", FailFastOrderingTests.SelectedTestMembershipNeverChanges),
        ("generation reuse safety dominates fail-fast ordering", FailFastOrderingTests.GenerationReuseSafetyDominatesHeuristicOrdering),
        ("fail-fast ordering keeps reuse groups contiguous", FailFastOrderingTests.HistoricalOrderingKeepsMultipleReuseGroupsContiguous),
        ("fail-fast ordering is deterministic for identical history", FailFastOrderingTests.IdenticalHistoryProducesIdenticalOrdering),
        ("synthetic history reduces expected first failure time", FailFastOrderingTests.SyntheticHistoryReducesExpectedFailureTimeWithoutNewTransitions),
        ("non-fail-fast execution remains complete", FailFastOrderingTests.NonFailFastExecutionRemainsComplete),
        ("fail-fast ordering result metadata is bounded", FailFastOrderingTests.ResultMetadataExplainsHistoricalOrderingBoundedly),
        ("fail-fast ordering context is versioned and bounded", FailFastOrderingTests.HistoricalOrderingContextIsVersionedAndBounded),
        ("suite cancellation stops new children", SuiteCancellationStopsNewChildren),
        ("suite duplicate tests execute once", SuiteDuplicateTestsExecuteOnce),
        ("suite plan refusal blocks execution", SuitePlanRefusalBlocksExecution),
        ("suite child infrastructure failure is summarized", SuiteChildInfrastructureFailureIsSummarized),
        ("shared build prerequisite blocks every selected test", SharedBuildPrerequisiteBlocksEverySelectedTest),
        ("unannotated recipes use the safe path", UnannotatedRecipesUseSafePath),
        ("unsafe recipes never share state", UnsafeRecipesNeverShareState),
        ("mutation recipes never share state", MutationRecipesNeverShareState),
        ("incompatible reuse profiles fall back safely", IncompatibleReuseProfilesFallBackSafely),
        ("reuse planner groups compatible tests deterministically", ReusePlannerGroupsCompatibleTestsDeterministically),
        ("reuse planner preserves hard boundaries", ReusePlannerPreservesHardBoundaries),
        ("grouped suite execution avoids lifecycle transitions", GroupedSuiteExecutionAvoidsLifecycleTransitions),
        ("reuse cancellation cannot contaminate later tests", ReuseCancellationCannotContaminateLaterTests),
        ("DevBridge reuse refusal preserves its cause", DevBridgeReuseRefusalPreservesCause),
        ("compatible recipes reuse one generation", CompatibleRecipesReuseOneGeneration),
        ("fail-fast preserves compatible reuse", FailFastPreservesCompatibleReuse),
        ("resettable recipes require successful reset", ResettableRecipesRequireSuccessfulReset),
        ("failed reset invalidates reuse", FailedResetInvalidatesReuse),
        ("test failure cannot contaminate later recipes", TestFailureCannotContaminateLaterRecipes),
        ("generation and lease changes invalidate reuse", GenerationAndLeaseChangesInvalidateReuse),
        ("reuse result remains bounded and identities stay distinct", ReuseResultIsBoundedAndIdentitiesStayDistinct),
        ("lease adapter preserves owner and generation identity", LeaseAdapterPreservesOwnerAndGenerationIdentity),
        ("existing compatible lease retries a blocked recipe", ExistingCompatibleLeaseRetriesBlockedRecipe),
        ("fresh generation adapter proves readiness conservatively", FreshGenerationAdapterProvesReadinessConservatively),
        ("affected run uses conservative fallback", AffectedRunUsesConservativeFallback),
        ("suite run CLI is deterministic", SuiteRunCliIsDeterministic),
        ("capabilities discover the registered surface", CapabilitiesDiscoverRegisteredSurface),
        ("capabilities query filters the registry", CapabilitiesQueryFiltersRegistry),
        ("capabilities bound output", CapabilitiesBoundOutput),
        ("capabilities preserve parameter metadata", CapabilitiesPreserveParameterMetadata),
        ("capabilities report unavailable bridge", CapabilitiesReportUnavailableBridge),
        ("capabilities reject malformed response", CapabilitiesRejectMalformedResponse),
        ("capabilities reject incompatible response", CapabilitiesRejectIncompatibleResponse),
        ("capability discovery does not mutate lifecycle", CapabilityDiscoveryDoesNotMutateLifecycle),
        ("capability discovery forwards its lease", CapabilityDiscoveryForwardsLease),
        ("validation capability present", ValidationCapabilityPresent),
        ("validation capability absent blocks execution", ValidationCapabilityAbsentBlocksExecution),
        ("validation capability provider mismatch blocks", ValidationCapabilityProviderMismatchBlocks),
        ("validation capability schema mismatch blocks", ValidationCapabilitySchemaMismatchBlocks),
        ("validation capability discovery infrastructure is distinct", ValidationCapabilityDiscoveryInfrastructureIsDistinct),
        ("validation capability old recipe remains compatible", ValidationCapabilityOldRecipeRemainsCompatible),
        ("validation capability JSON is structured", ValidationCapabilityJsonIsStructured),
        ("validation capability suite aggregation", ValidationCapabilitySuiteAggregation),
        ("validation capability observability deduplicates", ValidationCapabilityObservabilityDeduplicates),
        ("validation capability product failure remains distinct", ValidationCapabilityProductFailureRemainsDistinct),
        ("validation capability infrastructure remains distinct", ValidationCapabilityInfrastructureRemainsDistinct),
        ("required validation failure blocks completion", ValidationPolicyTests.RequiredFailureBlocksCompletion),
        ("required validation success permits completion", ValidationPolicyTests.RequiredSuccessPermitsCompletion),
        ("unavailable best effort validation does not block", ValidationPolicyTests.UnavailableBestEffortDoesNotBlockCompletion),
        ("recommended validation does not block", ValidationPolicyTests.DiscoveredRecommendationDoesNotBlockCompletion),
        ("executed optional mod defect surfaces", ValidationPolicyTests.ExecutedOptionalModDefectIsStillSurfaced),
        ("discovered validation cannot escalate", ValidationPolicyTests.DiscoveredValidationCannotEscalateToRequired),
        ("structured validation output separates defect and recommendation", ValidationPolicyTests.StructuredOutputSeparatesDefectAndRecommendation),
        ("nonblocking recommendation persists after completion", ValidationPolicyTests.NonBlockingRecommendationPersistsAfterCompletion),
        ("Golden Path completes successfully", GoldenPathTests.SuccessfulGoldenPathCompletion),
        ("Golden Path emits structured production state", GoldenPathTests.ProductionStateAndEventsAreStructured),
        ("optional runtime absence still passes", GoldenPathTests.OptionalRuntimeUnavailableStillPasses),
        ("required runtime blocks only dependent claim", GoldenPathTests.RequiredRuntimeUnavailableBlocksOnlyDependentClaim),
        ("Golden Path retry succeeds", GoldenPathTests.InfrastructureRetrySucceeds),
        ("failed retry creates tooling incident", GoldenPathTests.FailedRetryCreatesIncidentWithoutToolDevelopment),
        ("Golden Path recommendation persists", GoldenPathTests.RecommendationPersistsAfterCompletion),
        ("ui target enumeration", UiTargetEnumeration),
        ("ui target object schema is supported", UiTargetObjectSchemaIsSupported),
        ("ui target discovery recovers a required lease", UiTargetDiscoveryRecoversRequiredLease),
        ("ui bridge calls carry workflow identity", UiBridgeCallsCarryWorkflowIdentity),
        ("ui targeted screenshot uses clipping", UiTargetedScreenshotUsesClipping),
        ("ui missing target fails before capture", UiMissingTargetFailsBeforeCapture),
        ("ui reports unavailable bridge", UiReportsUnavailableBridge),
        ("ui reports visual readiness failure", UiReportsVisualReadinessFailure),
        ("ui cell capture preserves camera", UiCellCapturePreservesCamera),
        ("ui requests do not mutate lifecycle", UiRequestsDoNotMutateLifecycle),
        ("transactional ui viewport captures and restores", TransactionalUiViewportCapturesAndRestores),
        ("transactional ui viewport restores after ui failure", TransactionalUiViewportRestoresAfterUiFailure),
        ("transactional ui viewport surfaces restoration failure", TransactionalUiViewportSurfacesRestorationFailure),
        ("transactional ui viewport validates explicit dimensions", TransactionalUiViewportValidatesExplicitDimensions),
        ("ui output is compact", UiOutputIsCompact),
        ("canonical UI guidance is generated", CanonicalUiGuidanceIsGenerated),
        ("doctor healthy output is compact", DoctorHealthyOutputIsCompact),
        ("doctor preserves identity mismatch details", DoctorPreservesIdentityMismatchDetails),
        ("doctor reports blocked component", DoctorReportsBlockedComponent),
        ("stack manifest defaults are used", StackManifestDefaultsAreUsed),
        ("explicit CLI overrides beat manifest", ExplicitCliOverridesBeatManifest),
        ("malformed stack schema is blocked", MalformedStackSchemaIsBlocked),
        ("unknown stack schema is blocked", UnknownStackSchemaIsBlocked),
        ("missing stack manifest is blocked", MissingStackManifestIsBlocked),
        ("local configuration does not leak", LocalConfigurationDoesNotLeak),
        ("init creates an empty repository handoff", InitCreatesEmptyRepositoryHandoff),
        ("init fills a missing manifest field safely", InitFillsMissingManifestFieldSafely),
        ("init merges explicit configuration safely", InitMergesExplicitConfigurationSafely),
        ("init preserves existing AGENTS", InitPreservesExistingAgents),
        ("init preserves existing manifest", InitPreservesExistingManifest),
        ("init is idempotent", InitIsIdempotent),
        ("init force behavior is intentional", InitForceBehaviorIsIntentional),
        ("manifest-only repair preserves AGENTS", ManifestOnlyRepairPreservesAgents),
        ("doctor missing project provides a handoff", DoctorMissingProjectProvidesHandoff),
        ("doctor missing fallback provides a handoff", DoctorMissingFallbackProvidesHandoff),
        ("doctor missing catalog provides a handoff", DoctorMissingCatalogProvidesHandoff),
        ("doctor invalid catalog provides a handoff", DoctorInvalidCatalogProvidesHandoff),
        ("affected discovers Git changes without paths", AffectedDiscoversGitChangesWithoutPaths),
        ("clean affected run is explicit and does not launch", CleanAffectedRunIsExplicitAndDoesNotLaunch),
        ("affected source run performs freshness transaction", AffectedSourceRunPerformsFreshnessTransaction),
        ("fail-fast affected run still proves freshness", FailFastAffectedRunStillProvesFreshness),
        ("affected companion recipe may use a later generation", AffectedCompanionRecipeMayUseLaterGeneration),
        ("affected identical artifact uses no-deploy proof", AffectedIdenticalArtifactUsesNoDeployProof),
        ("tracked build-owned artifact mutation continues", TrackedBuildOwnedArtifactMutationContinues),
        ("source mutation during artifact transaction is rejected", SourceMutationDuringArtifactTransactionIsRejected),
        ("unrelated tracked mutation during artifact transaction is rejected", UnrelatedTrackedMutationDuringArtifactTransactionIsRejected),
        ("artifact mutation without build provenance is rejected", ArtifactMutationWithoutBuildProvenanceIsRejected),
        ("artifact mutation with unexpected bytes is rejected", ArtifactMutationWithUnexpectedBytesIsRejected),
        ("multiple build-owned artifact mutations are classified per output", MultipleBuildOwnedArtifactMutationsAreClassifiedPerOutput),
        ("affected build prerequisite blocks all selected tests", AffectedBuildPrerequisiteBlocksAllSelectedTests),
        ("affected deployment failure blocks pass", AffectedDeploymentFailureBlocksPass),
        ("affected structured DevBridge failure preserves causal chain", AffectedStructuredDevBridgeFailurePreservesCausalChain),
        ("affected readiness failure blocks pass", AffectedReadinessFailureBlocksPass),
        ("affected run recovers readiness once", AffectedRunRecoversReadinessOnce),
        ("identity generation mismatch recovers", IdentityGenerationMismatchRecovers),
        ("identity process mismatch recovers", IdentityProcessMismatchRecovers),
        ("identity root mismatch refuses recovery", IdentityRootMismatchRefusesRecovery),
        ("identity recovery exhaustion is structured", IdentityRecoveryExhaustionIsStructured),
        ("canonical affected identity exhaustion is JSON", CanonicalAffectedIdentityExhaustionIsJson),
        ("canonical affected identity recovery continues", CanonicalAffectedIdentityRecoveryContinues),
        ("identity parser classifies fields", IdentityParserClassifiesFields),
        ("shared runtime transitions recover on a fresh generation", SharedRuntimeTransitionsRecoverOnFreshGeneration),
        ("repeated shared transition protocol failure is exhausted", RepeatedSharedTransitionProtocolFailureIsExhausted),
        ("stale generation proof is rejected after transition recovery", StaleGenerationProofIsRejectedAfterTransitionRecovery),
        ("source failure does not enter transition recovery", SourceFailureDoesNotEnterTransitionRecovery),
        ("affected generation mismatch blocks pass", AffectedGenerationMismatchBlocksPass),
        ("affected unknown freshness blocks pass", AffectedUnknownFreshnessBlocksPass),
        ("affected incomplete freshness metadata blocks pass", AffectedIncompleteFreshnessMetadataBlocksPass),
        ("affected propagates transaction identities", AffectedPropagatesTransactionIdentities),
        ("mod-development adapter parses bounded freshness response", ModDevelopmentAdapterParsesBoundedFreshnessResponse),
        ("mod-development adapter uses packaged transaction consumer", ModDevelopmentAdapterUsesPackagedTransactionConsumer),
        ("internal transaction service avoids PowerShell boundary", InternalTransactionServiceAvoidsPowerShellBoundary),
        ("mod-development adapter uses the source script root", ModDevelopmentAdapterUsesSourceScriptRoot),
        ("mod-development source root ignores whitespace override", ModDevelopmentSourceRootIgnoresWhitespaceOverride),
        ("mod-development owner manifest uses runtime deployment root", ModDevelopmentOwnerManifestUsesRuntimeDeploymentRoot),
        ("mod-development response contract matrix is deterministic", ModDevelopmentResponseContractMatrix),
        ("mod-development adapter binds descriptor output provenance", ModDevelopmentAdapterBindsDescriptorOutputProvenance),
        ("mod-development build failure exports compiler diagnostics", ModDevelopmentBuildFailureExportsCompilerDiagnostics),
        ("pinned DevBridge build diagnostics cross the real wire boundary", PinnedDevBridgeBuildDiagnosticsCrossWireBoundary),
        ("valid development descriptor is preserved", ValidDevelopmentDescriptorIsPreserved),
        ("missing development descriptor is derived", MissingDevelopmentDescriptorIsDerived),
        ("malformed development descriptor is repaired", MalformedDevelopmentDescriptorIsRepaired),
        ("stale development descriptor is reconciled safely", StaleDevelopmentDescriptorIsReconciledSafely),
        ("ambiguous development descriptor is blocked", AmbiguousDevelopmentDescriptorIsBlocked),
        ("lease recovery retries the owner transaction once", LeaseRecoveryRetriesOwnerTransactionOnce),
        ("lease contention remains explicit", LeaseContentionRemainsExplicit),
        ("lease recovery has no loop", LeaseRecoveryHasNoLoop),
        ("freshness cleanup failure remains visible", FreshnessCleanupFailureRemainsVisible),
        ("cleanup failure remains independent in orchestration", CleanupFailureRemainsIndependentInOrchestration),
        ("Git discovery includes staged and untracked files", GitDiscoveryIncludesStagedAndUntrackedFiles),
        ("Git discovery preserves deleted and renamed paths", GitDiscoveryPreservesDeletedAndRenamedPaths),
        ("explicit affected paths take precedence", ExplicitAffectedPathsTakePrecedence),
        ("environment fallback drives affected fallback", EnvironmentFallbackDrivesAffectedFallback),
        ("affected deleted path uses conservative fallback", AffectedDeletedPathUsesConservativeFallback),
        ("affected rename without fallback blocks", AffectedRenameWithoutFallbackBlocks),
        ("Git discovery failure is conservative", GitDiscoveryFailureIsConservative),
        ("RimError diagnosis provides drill-down next action", RimErrorDiagnosisProvidesNextAction),
        ("DevBridge failure provides doctor next action", DevBridgeFailureProvidesNextAction),
        ("RimContext stale result provides recovery next action", RimContextStaleProvidesNextAction),
        ("observability associations and views stay scoped", ObservabilityTests.AssociationsAndViewsStayScoped),
        ("observability concurrent agents remain isolated", ObservabilityTests.ConcurrentInterleavedAgentsRemainIsolated),
        ("observability lifecycle completion and failure are structured", ObservabilityTests.LifecycleCompletionAndFailureAreStructured),
        ("observability failure recovery and references work", ObservabilityTests.FailureIssueRecoveryAndReferencesWork),
        ("observability retry heuristics and stalls are conservative", ObservabilityTests.RetryHeuristicsAndStallsAreConservative),
        ("observability diagnostic bundles exclude unrelated history", ObservabilityTests.DiagnosticBundlesExcludeUnrelatedHistory),
        ("observability v2 bundle contains structured build evidence", ObservabilityTests.DiagnosticBundleV2ContainsStructuredBuildEvidence),
        ("observability incomplete bundle reports missing build diagnostics", ObservabilityTests.DiagnosticBundleMissingBuildDiagnosticsIsExplicitlyIncomplete),
        ("observability diagnostic evidence survives reload", ObservabilityTests.DiagnosticEvidenceSurvivesStoreReloadOutsideWorktree),
        ("observability diagnostic evidence honors configured bounds", ObservabilityTests.DiagnosticEvidenceHonorsConfiguredBounds),
        ("observability durable store reloads structured state", ObservabilityTests.DurableStoreReloadsStructuredState),
        ("observability OTel correlation is optional and hierarchical", ObservabilityTests.OTelCorrelationIsOptionalAndHierarchical),
        ("observability OTel disabled still stores product state", ObservabilityTests.OTelDisabledStillStoresProductState),
        ("observability telemetry failure cannot break execution", ObservabilityTests.TelemetryFailureCannotBreakExecution),
        ("observability runtime operations need no model calls", ObservabilityTests.RuntimeOperationsDoNotNeedModelCalls),
        ("observability one failed invocation is not a retry", ObservabilityTests.SingleFailedInvocationDoesNotCreateRetryIncident),
        ("observability CLI wires the authoritative store", ObservabilityTests.CliWiresTheAuthoritativeStore),
        ("observability CLI resolves Frontier as the subject", ObservabilityTests.CliProjectTargetUsesCanonicalIdentity),
        ("observability CLI separates project targets", ObservabilityTests.CliProjectTargetsRemainDistinct),
        ("observability nested tooling preserves the primary subject", ObservabilityTests.NestedToolingPreservesThePrimarySubject),
        ("observability structured legacy target migration is idempotent", ObservabilityHydrationTests.LegacyToolTargetWithStructuredProjectEvidenceIsReassociated),
        ("observability package aliases share canonical identity", ObservabilityTests.PackageAliasesShareOneCanonicalModIdentity),
        ("observability recent hydration is bounded and deferred", ObservabilityHydrationTests.RecentHydrationIsBoundedAndDeferred),
        ("observability malformed state is degraded but usable", ObservabilityHydrationTests.MalformedAndTruncatedRecordsAreDegradedButUsable),
        ("observability legacy identity migration is durable and idempotent", ObservabilityHydrationTests.LegacyIdentityMigrationIsDurableAndIdempotent),
        ("observability persistence contention is bounded", ObservabilityHydrationTests.TemporaryPersistenceContentionIsBounded),
        ("observability history hydrates on demand", ObservabilityHydrationTests.HistoricalHydrationRemainsAvailableOnDemand),
        ("observability live records survive hydration", ObservabilityHydrationTests.RecordsArrivingAfterHydrationBecomeVisible),
        ("observability hydration cancellation is safe", ObservabilityHydrationTests.HydrationCancellationIsSafe),
        ("observability compaction retains bounded recent records", ObservabilityHydrationTests.CompactionRetainsRecentRecordsWithinThreshold),
        ("observability unresolved issue evidence survives retention", ObservabilityHydrationTests.UnresolvedIssueEvidenceSurvivesEvidenceRetention),
        ("observability startup diagnostics are bounded and external", ObservabilityHydrationTests.StartupDiagnosticIsBoundedAndExternal),
        ("observability canonical root is independent of worktrees", ObservabilityIsolationTests.CanonicalRootIsIndependentOfWorktree),
        ("observability workflow leaves worktree clean", ObservabilityIsolationTests.RepresentativeWorkflowLeavesWorktreeClean),
        ("observability shared store hydrates across processes", ObservabilityIsolationTests.SharedStoreHydratesAndPublishesAcrossProcesses),
        ("observability concurrent stores preserve identity boundaries", ObservabilityIsolationTests.ConcurrentStoresShareSequencesAndAgentIdentityBoundaries),
        ("observability historical runs do not duplicate live agents", ObservabilityIsolationTests.HistoricalRunsDoNotCreateDuplicateLiveAgents),
        ("observability temporary worktrees share repository identity", ObservabilityIsolationTests.TemporaryWorktreesShareProvenRepositoryIdentity),
        ("observability known temporary identity migration preserves boundaries", ObservabilityIsolationTests.KnownTemporaryIdentityMigrationPreservesBoundaries),
        ("observability CLI fixture store cannot write canonical root", ObservabilityIsolationTests.CliFixtureStoreCannotWriteCanonicalRoot),
        ("prompt 2 persisted qualification is fixture-classified", ObservabilityIsolationTests.QualificationRecordsPersistAsFixtureClassification),
        ("prompt 2 concurrent aliases remain canonical", ObservabilityIsolationTests.ConcurrentToolAliasesRemainOneCanonicalEntity),
        ("observability desktop retains concurrent runs", ObservabilityIsolationTests.UnscopedUiRetainsConcurrentRuns),
        ("prompt 2 integrity validator accepts coherent state", ObservabilityIsolationTests.IntegrityValidatorAcceptsCoherentStore),
        ("prompt 2 integrity validator reports unresolved activity", ObservabilityIsolationTests.IntegrityValidatorReportsUnresolvedActivity),
        ("prompt 2 integrity validator detects subject inversion", ObservabilityIsolationTests.IntegrityValidatorDetectsToolSubjectInversion),
        ("prompt 2 concurrent project subjects retain tool ownership", ObservabilityIsolationTests.ConcurrentProjectSubjectsRetainToolOwnership),
        ("prompt 2 multi-mod attribution remains canonical", ObservabilityIsolationTests.MultiModLogicalAgentRetainsAttribution),
        ("prompt 2 concurrent canonical registration remains unique", ObservabilityIsolationTests.ConcurrentCanonicalRegistrationDoesNotDuplicate),
        ("prompt 2 lifecycle reconnect remains one canonical agent", ObservabilityIsolationTests.LifecycleReconnectKeepsOneCanonicalAgent),
        ("prompt 3 multi-agent lifecycle and bundle audit", Prompt3AuditTests.MultiAgentLifecycleAndBundleAudit),
        ("prompt 3 redaction and issue bounds audit", Prompt3AuditTests.RedactionAndIssueBoundsAudit),
        ("prompt 3 abandoned agents become terminal", Prompt3AuditTests.AbandonedAgentBecomesTerminal),
        ("prompt 3 zero-exit successes resolve failures", Prompt3AuditTests.ZeroExitSuccessResolvesFailure),
        ("prompt 3 repeated work history is agent scoped", Prompt3AuditTests.RepeatedWorkHistoryIsAgentScoped),
        ("prompt 3 uncertain issue signals use qualified wording", Prompt3AuditTests.UncertainIssueSignalsUseQualifiedWording),
        ("prompt 3 persisted records remain bounded", Prompt3AuditTests.PersistedRecordsRemainBounded),
        ("prompt 3 content intelligence projection is incremental", Prompt3AuditTests.ContentIntelligenceLifecycleProjectionIsIncremental),
        ("prompt 3 execution impact lifecycle projection", Prompt3AuditTests.ExecutionImpactLifecycleAndProjection),
        ("prompt 3 failure and remediation observability", Prompt3AuditTests.FailureAndRemediationObservability),
        ("desktop All is the default view", DesktopObservabilityTests.AllIsDefault),
        ("desktop multiple agents appear", DesktopObservabilityTests.MultipleConcurrentAgentsAppear),
        ("desktop concurrent runs remain visible", DesktopObservabilityTests.MultipleConcurrentRunsRemainVisible),
        ("desktop repeated runs for one mod share a tab", DesktopObservabilityTests.RepeatedRunsForSameModShareOneTab),
        ("desktop eleven sessions count one logical agent", DesktopObservabilityTests.ElevenSessionsOfOneLogicalAgentCountOnce),
        ("desktop distinct concurrent logical agents remain separate", DesktopObservabilityTests.DistinctConcurrentLogicalAgentsRemainSeparate),
        ("observability tooling entities aggregate across processes", DesktopObservabilityTests.ToolingEntitiesAggregateAcrossRunsAndPersist),
        ("observability entities keep mods and tools distinct", DesktopObservabilityTests.EntityRoutingKeepsModsAndToolsDistinct),
        ("observability persisted tooling identity survives reload", DesktopObservabilityTests.PersistedToolingIdentityDoesNotDuplicateOnReload),
        ("prompt 3 concurrent tooling activities remain one entity", DesktopObservabilityTests.ConcurrentToolingActivitiesRemainOneEntity),
        ("prompt 3 Windows path identity normalization is stable", DesktopObservabilityTests.WindowsPathIdentityNormalizationIsStable),
        ("prompt 3 known tool fallback uses tool identity", DesktopObservabilityTests.KnownToolFallbackUsesToolIdentity),
        ("prompt 3 multiple tool identities aggregate across runs", DesktopObservabilityTests.MultipleToolIdentitiesAggregateAcrossRuns),
        ("prompt 2 RimLiaison sessions share one top-level tab", DesktopObservabilityTests.TopNavigationGroupsAllRimLiaisonSessions),
        ("prompt 2 tool aliases share one canonical identity", DesktopObservabilityTests.ToolAliasesShareOneCanonicalIdentity),
        ("prompt 2 qualification fixture is hidden", DesktopObservabilityTests.QualificationFixtureIsHiddenFromProductionNavigation),
        ("prompt 2 synthetic fixture is hidden", DesktopObservabilityTests.SyntheticFixtureModIsNotTopLevelProductionEntity),
        ("prompt 2 non-production taxonomy is hidden", DesktopObservabilityTests.NonProductionIdentityTaxonomyIsHidden),
        ("prompt 2 real mods and ecosystem tools remain distinct", DesktopObservabilityTests.RealModsAndEcosystemToolsRemainDistinct),
        ("prompt 2 malformed identity stays diagnostic only", DesktopObservabilityTests.MalformedLegacyIdentityStaysDiagnosticOnly),
        ("desktop legacy records use stable fallback entity", DesktopObservabilityTests.LegacyRecordsUseStableFallbackEntity),
        ("desktop completed agents remain visible", DesktopObservabilityTests.CompletedRunsRemainVisibleWithoutDismissal),
        ("prompt 3 production overview groups sessions", DesktopObservabilityTests.ProductionOverviewGroupsSessionsAndShowsCurrentState),
        ("prompt 3 recommendations are non-blocking", DesktopObservabilityTests.RecommendationsHaveSeparateNonBlockingSurface),
        ("prompt 3 issue categories are operator readable", DesktopObservabilityTests.IssueCategoriesAreOperatorReadable),
        ("desktop bounded navigation prioritizes active agents", DesktopObservabilityTests.ActiveAgentsArePrioritizedInBoundedNavigation),
        ("desktop interleaved activity is newest first", DesktopObservabilityTests.InterleavedEventsRemainChronological),
        ("desktop production and activity sort by real timestamp", DesktopObservabilityTests.ProductionAndActivitySortByRealTimestamp),
        ("desktop recommendation duplicates nest by stable operation", DesktopObservabilityTests.RecommendationDuplicatesNestByStableOperation),
        ("desktop issue duplicates nest without merging failures", DesktopObservabilityTests.IssueDuplicatesNestWithoutMergingDistinctFailures),
        ("desktop unknown agent identity uses occurrence fallback", DesktopObservabilityTests.UnknownAgentIdentityUsesOccurrenceFallback),
        ("desktop recommendation duplicate arrives live", DesktopObservabilityTests.RecommendationDuplicateArrivesLive),
        ("desktop issue duplicate arrives live", DesktopObservabilityTests.IssueDuplicateArrivesLive),
        ("desktop individual agent filters correctly", DesktopObservabilityTests.IndividualAgentViewFiltersCorrectly),
        ("desktop Issues contains only issues", DesktopObservabilityTests.IssuesViewContainsOnlyStructuredIssues),
        ("desktop recovered and unresolved states differ", DesktopObservabilityTests.RecoveredAndUnresolvedStatesAreDistinct),
        ("desktop issue detail resolves events", DesktopObservabilityTests.IssueDetailResolvesSupportingEvents),
        ("desktop issue activity navigation works", DesktopObservabilityTests.ViewActivityNavigatesToAgentContext),
        ("desktop multi-issue bundle is correct", DesktopObservabilityTests.MultipleIssueSelectionBuildsCorrectBundle),
        ("desktop new agents appear live", DesktopObservabilityTests.NewAgentsAppearLive),
        ("desktop new issues appear live", DesktopObservabilityTests.NewIssuesAppearLive),
        ("desktop failure does not stop another agent", DesktopObservabilityTests.OneAgentCanFailWhileAnotherContinues),
        ("desktop completion does not stop another agent", DesktopObservabilityTests.OneAgentCanCompleteWhileAnotherContinues),
        ("desktop view switching is local", DesktopObservabilityTests.ViewSwitchingIsLocalAndDoesNotCallModels),
        ("desktop bundle preparation is local", DesktopObservabilityTests.BundlePreparationIsLocalAndDoesNotCallModels),
        ("desktop works with OTel disabled", DesktopObservabilityTests.OTelDisabledDoesNotAffectDesktopViews),
        ("desktop large volume remains bounded", DesktopObservabilityTests.LargeVolumeViewsRemainBounded),
        ("desktop agent navigation identity and history are stable", DesktopObservabilityTests.AgentNavigationIdentityAndHistoryAreStable),
        ("desktop activity refresh is incremental", DesktopObservabilityTests.ActivityRefreshPlanIsIncremental),
        ("desktop presentation reconciliation is stable", DesktopObservabilityTests.DesktopPresentationReconciliationIsStable),
        ("desktop issue selection and assessment survive live updates", DesktopObservabilityTests.IssueSelectionAndAssessmentSurviveLiveUpdates),
        ("desktop activity selection resolves related details", DesktopObservabilityTests.ActivitySelectionResolvesRelatedDetailsAndSurvivesLiveEvents),
        ("observability one active agent excludes stale history", DesktopObservabilityTests.OneActiveAgentAndHistoricalSessionsExposeOneWorkingAgent),
        ("desktop stale overview re-evaluates without events", DesktopObservabilityTests.StaleNavigationAndOverviewReevaluateWithoutNewEvents),
        ("observability live activity insertion updates All", DesktopObservabilityTests.LiveActivityInsertionInvalidatesAllProjection),
        ("prompt 4 Frontier agent tab resolves to an agent", DesktopObservabilityTests.FrontierAgentTabSelectionResolvesAgentEntity),
        ("prompt 4 Frontier agent tab uses agent key", DesktopObservabilityTests.FrontierAgentTabDoesNotUseModDisplayTextAsKey),
        ("prompt 4 Frontier agent detail renders data", DesktopObservabilityTests.ClickingFrontierAgentTabRendersAgentDetailData),
        ("prompt 4 generic agent detail renders", DesktopObservabilityTests.GenericSecondAgentRendersThroughTheSameRoute),
        ("prompt 4 same display names stay separate", DesktopObservabilityTests.SameDisplayNameDoesNotMergeAgentTabs),
        ("prompt 4 canonical agent route is identity based", DesktopObservabilityTests.CanonicalAgentRouteDoesNotDependOnDisplayText),
        ("prompt 4 agent detail includes activity", DesktopObservabilityTests.AgentWithActivityShowsRecentActivity),
        ("prompt 4 agent detail has empty state", DesktopObservabilityTests.AgentWithoutDetailHistoryShowsExplicitEmptyState),
        ("prompt 4 selected agent receives live activity", DesktopObservabilityTests.NewActivityAppearsForSelectedAgentWithoutRefresh),
        ("prompt 4 selected agent receives live status", DesktopObservabilityTests.SelectedAgentStatusUpdatesLive),
        ("prompt 4 malformed agent route is diagnostic", DesktopObservabilityTests.MalformedAgentRouteShowsDiagnosticState),
        ("prompt 4 agent selection reloads by canonical identity", DesktopObservabilityTests.AgentSelectionSurvivesStoreReloadByCanonicalIdentity),
        ("prompt 4 desktop Frontier click renders agent detail", DesktopObservabilityTests.DesktopFrontierClickShowsAgentDetailPanel),
        ("prompt 5 system destination uses canonical tooling projection", DesktopObservabilityTests.SystemDestinationUsesCanonicalToolingProjection),
        ("prompt 5 content administration uses supplied service", DesktopObservabilityTests.ContentAdministrationUsesSuppliedServiceAndDisablesSafely),
        ("prompt 5 desktop content host mounts every primary view", DesktopObservabilityTests.DesktopContentHostMountsEveryPrimaryView),
        ("prompt 2 tooling detail with data uses canonical identity", DesktopObservabilityTests.ToolDetailWithDataUsesCanonicalToolIdentity),
        ("prompt 2 tooling detail without data has empty state", DesktopObservabilityTests.ToolDetailWithoutDataShowsExplicitEmptyState),
        ("prompt 2 duplicate names across namespaces stay separate", DesktopObservabilityTests.DuplicateDisplayNamesAcrossEntityTypesStaySeparate),
        ("prompt 2 repeated entity switching stays isolated", DesktopObservabilityTests.RepeatedEntitySwitchingDoesNotLeakDetailState),
        ("prompt 2 malformed desktop selection is diagnostic", DesktopObservabilityTests.DesktopMalformedSelectionShowsDiagnosticInsteadOfBlank),
        ("observability canonical mod tab loads activity", DesktopObservabilityTests.CanonicalModTabLoadsAliasActivityAndRealEmptyState),
        ("observability persisted Working state reconciles on restart", DesktopObservabilityTests.PersistedWorkingStateIsReconciledOnRestart),
        ("prompt 2 Issues projection is indexed, cached, and lazy", DesktopObservabilityTests.IssuesProjectionIsIndexedCachedAndLazy),
        ("prompt 2 triage classifies owners conservatively", DesktopObservabilityTests.IssueTriageClassifiesOwnersConservatively),
        ("prompt 1 generic wrappers do not claim shared impact", DesktopObservabilityTests.GenericWrapperCodesDoNotCreateSharedToolingCounts),
        ("prompt 2 shared tooling hints avoid unrelated failures", DesktopObservabilityTests.SharedToolingHintsAvoidUnrelatedFailures),
        ("prompt 2 ChatGPT packet is bounded and explicit", DesktopObservabilityTests.ChatPacketContainsBoundedTriageAndMissingEvidence),
        ("prompt 2 ChatGPT action supports checked issues", DesktopObservabilityTests.ChatGPTActionSupportsCheckedIssuesAndPreservesCausalEvidence),
        ("failure handling attributes project compiler errors", FailureHandlingTests.ProjectCompilerFailureIsOwnedByProject),
        ("failure handling attributes injected build errors", FailureHandlingTests.DevBridgeInjectedFailureIsOwnedByDevBridge),
        ("failure handling keeps missing build cause unproven", FailureHandlingTests.MissingBuildCauseRemainsUnproven),
        ("failure handling preserves causal handoff and raw evidence", FailureHandlingTests.CausalDiagnosticSurvivesBoundedHandoffAndRawEvidenceRetrieval),
        ("failure handling rejects truncated causal evidence", FailureHandlingTests.TruncatedCauseCannotBeComplete),
        ("failure handling repairs missing manifest before doctor", FailureHandlingTests.MissingManifestIsSafelyRepairedAndDoctorRetries),
        ("failure handling refuses unsafe manifest repair", FailureHandlingTests.UnsafeManifestRepairDoesNotMutateState),
        ("desktop preserves existing CLI UI", DesktopObservabilityTests.ExistingCliUiRemainsAvailable),
        ("efficiency profiler aggregates compact schema", ProfilerTests.AggregatesCompactSchema),
        ("efficiency profiler groups repeated operations", ProfilerTests.GroupsRepeatedOperations),
        ("efficiency profiler groups failures and retries safely", ProfilerTests.GroupsFailuresAndRetries),
        ("efficiency profiler records unchanged generations", ProfilerTests.RecordsUnchangedGenerations),
        ("efficiency profiler redacts raw values", ProfilerTests.RedactsRawValues),
        ("efficiency profiler fingerprints are deterministic", ProfilerTests.FingerprintsAreDeterministic),
        ("efficiency profiler prioritizes specialized evidence", ProfilerTests.PrioritizesSpecializedEvidence),
        ("efficiency profiler bounds sections and total output", ProfilerTests.BoundsSectionsAndOutput),
        ("efficiency profiler preserves overflow totals", ProfilerTests.PreservesOverflowTotals),
        ("efficiency profiler failures do not alter command results", ProfilerTests.FailuresDoNotAlterCommandResults),
        ("efficiency profiler preserves CLI output contracts", ProfilerTests.PreservesCliOutputContracts),
        ("efficiency profiler emits success and failure profiles", ProfilerTests.EmitsSuccessAndFailureProfiles),
        ("rimdev clean up-to-date repo", RimDevTests.CleanUpToDateRepo),
        ("rimdev ahead-only repo", RimDevTests.AheadOnlyRepo),
        ("rimdev behind-only fast-forward repo", RimDevTests.BehindOnlyFastForwardRepo),
        ("rimdev diverged repo", RimDevTests.DivergedRepo),
        ("rimdev dirty repo", RimDevTests.DirtyRepo),
        ("rimdev no upstream", RimDevTests.NoUpstream),
        ("rimdev generated state is ignored", RimDevTests.GeneratedStateIsIgnored),
        ("rimdev meaningful change summary retains unknown paths", RimDevTests.MeaningfulChangeSummaryRetainsUnknownPaths),
        ("rimdev owner-aware classification agrees across consumers", RimDevTests.OwnerAwareClassificationAgreesAcrossConsumers),
        ("rimdev generated-only worktree is not reported dirty", RimDevTests.GeneratedOnlyWorktreeIsNotReportedDirty),
        ("rimdev build failure", RimDevTests.BuildFailure),
        ("rimdev test failure", RimDevTests.TestFailure),
        ("rimdev infrastructure-blocked test cannot push", RimDevTests.InfrastructureBlockedTestCannotPush),
        ("rimdev failed test cannot push directly", RimDevTests.FailedTestCannotPushDirectly),
        ("rimdev invalidated evidence cannot push", RimDevTests.InvalidatedEvidenceCannotPush),
        ("rimdev missing canonical evidence blocks push", RimDevTests.MissingCanonicalEvidenceBlocksPush),
        ("rimdev passing process without canonical evidence is blocked", RimDevTests.PassingProcessWithoutCanonicalEvidenceIsBlocked),
        ("rimdev build evidence alone cannot authorize push", RimDevTests.BuildEvidenceAloneCannotAuthorizePush),
        ("rimdev documentation-only change can push", RimDevTests.DocumentationOnlyChangeCanPush),
        ("rimdev deployment failure", RimDevTests.DeploymentFailure),
        ("rimdev safe push", RimDevTests.SafePush),
        ("rimdev rejects non-fast-forward push", RimDevTests.RejectedNonFastForwardPush),
        ("rimdev merge candidate with passing checks", RimDevTests.MergeCandidateWithPassingChecks),
        ("rimdev rejects failing or pending merge checks", RimDevTests.RejectedMergeChecks),
        ("rimdev all never merges", RimDevTests.AllNeverMerges),
        ("rimdev all no changes avoids unnecessary work", RimDevTests.NoChangesAllAvoidsUnnecessaryWork),
        ("rimdev failed human summary uses canonical status", RimDevTests.FailedHumanSummaryUsesCanonicalStatus),
        ("rimdev merge requires exact source identity", RimDevTests.MergeRequiresExactSourceIdentity),
        ("rimdev partial multi-repository failure", RimDevTests.PartialMultiRepositoryFailure),
        ("rimdev affected-only build and test selection", RimDevTests.AffectedOnlyBuildAndTestSelection),
        ("rimdev failed dependency blocks dependent publication", RimDevTests.FailedDependencyBlocksDependentPublication),
        ("rimdev reuses canonical validation evidence", RimDevTests.TrustworthyTestEvidenceIsReused),
        ("rimdev legacy test evidence cannot authorize reuse", RimDevTests.LegacyTestEvidenceCannotAuthorizeReuse),
        ("rimdev All reuses canonical publication evidence", RimDevTests.CanonicalEvidenceIsReusedByAll),
        ("rimdev one changed leaf selects only that repository", RimDevTests.OneChangedLeafSelectsOnlyThatRepository),
        ("rimdev dirty sync preserves work and continues", RimDevTests.DirtySyncPreservesWorkAndContinues),
        ("rimdev all failure blocks only failed deployment", RimDevTests.AllFailureBlocksOnlyFailedDeployment),
        ("rimdev merge confirmation defaults to no", RimDevTests.MergeConfirmationDefaultsToNo),
        ("rimdev multiple merge candidates require selection", RimDevTests.MultipleMergeCandidatesRequireExplicitSelection),
        ("rimdev no-argument menu and help are beginner friendly", RimDevTests.CliNoArgumentAndHelpAreBeginnerFriendly),
    ];

    private const int DefaultTestTimeoutSeconds = 60;
    private const int MaximumTestTimeoutSeconds = 300;
    private const int MaximumChildOutputCharacters = 8_192;

    public static int Main(string[] args) =>
        RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 &&
            string.Equals(args[0], "--timeout-probe", StringComparison.Ordinal))
        {
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }

        if (args.Length == 2 &&
            string.Equals(args[0], "--child", StringComparison.Ordinal))
        {
            return RunChild(args[1]);
        }

        string? filter = args.Length == 2 &&
            string.Equals(args[0], "--filter", StringComparison.Ordinal)
                ? args[1]
                : null;
        (string Name, Action Test)[] selected = string.IsNullOrWhiteSpace(filter)
            ? Tests
            : Tests.Where(value => string.Equals(value.Name, filter, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"No test matched filter '{filter}'.");
            return 2;
        }

        int failures = 0;
        Queue<string> recentTests = new();
        int timeoutSeconds = ReadTestTimeoutSeconds();
        foreach ((string name, _) in selected)
        {
            Console.WriteLine($"BEGIN {name}");
            long started = Stopwatch.GetTimestamp();
            IsolatedTestResult result = await RunIsolatedAsync(name, timeoutSeconds);
            string duration = $"durationMs={ElapsedMilliseconds(started)}";
            if (result.TimedOut)
            {
                failures++;
                Console.Error.WriteLine("TEST_TIMEOUT");
                Console.Error.WriteLine($"test={name}");
                Console.Error.WriteLine(duration);
                Console.Error.WriteLine($"previousTest={(recentTests.Count == 0 ? "none" : recentTests.Last())}");
                Console.Error.WriteLine($"recentTests={(recentTests.Count == 0 ? "none" : string.Join(",", recentTests))}");
                Console.Error.WriteLine(ProcessDiagnostics());
            }
            else if (result.ExitCode == 0)
            {
                Console.WriteLine($"PASS {name} {duration}");
            }
            else
            {
                failures++;
                string details = FirstNonEmpty(result.StandardError, result.StandardOutput);
                Console.Error.WriteLine(
                    $"FAIL {name} {duration}: {BoundedSingleLine(details)}");
            }

            recentTests.Enqueue(name);
            while (recentTests.Count > 4)
            {
                recentTests.Dequeue();
            }
        }

        Console.WriteLine($"{selected.Length - failures}/{selected.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static int RunChild(string name)
    {
        (string Name, Action Test)? selected = Tests.FirstOrDefault(
            value => string.Equals(value.Name, name, StringComparison.Ordinal));
        if (selected is null)
        {
            Console.Error.WriteLine($"No test matched child '{name}'.");
            return 2;
        }

        string isolatedObservabilityRoot = Path.Combine(
            Path.GetTempPath(),
            "RimLiaison-test-observability-" + Guid.NewGuid().ToString("N"));
        string? previousObservabilityRoot = Environment.GetEnvironmentVariable(
            AgentObservabilityStorage.DirectoryEnvironmentVariable);
        Directory.CreateDirectory(isolatedObservabilityRoot);
        Environment.SetEnvironmentVariable(
            AgentObservabilityStorage.DirectoryEnvironmentVariable,
            isolatedObservabilityRoot);
        try
        {
            selected.Value.Test();
            Console.WriteLine($"PASS {name}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {name}: {exception}");
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AgentObservabilityStorage.DirectoryEnvironmentVariable,
                previousObservabilityRoot);
            DeleteDirectoryIncludingReadOnlyFiles(isolatedObservabilityRoot);
        }
    }

    private static Task<IsolatedTestResult> RunIsolatedAsync(
        string name,
        int timeoutSeconds) =>
        RunIsolatedAsync(["--child", name], timeoutSeconds);

    private static async Task<IsolatedTestResult> RunIsolatedAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds)
    {
        using Process process = new()
        {
            StartInfo = CreateChildStartInfo(arguments)
        };
        if (!process.Start())
        {
            return new IsolatedTestResult(-1, false, string.Empty, "child process did not start");
        }

        Task<string> outputTask = ReadBoundedAsync(process.StandardOutput);
        Task<string> errorTask = ReadBoundedAsync(process.StandardError);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited at the timeout boundary.
            }

            await process.WaitForExitAsync();
        }

        return new IsolatedTestResult(
            process.ExitCode,
            timedOut,
            await outputTask,
            await errorTask);
    }

    private static ProcessStartInfo CreateChildStartInfo(IReadOnlyList<string> arguments)
    {
        string processPath = Environment.ProcessPath ??
            throw new InvalidOperationException("The test runner process path is unavailable.");
        string assemblyPath = typeof(Program).Assembly.Location;
        bool isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
        ProcessStartInfo startInfo = new()
        {
            FileName = processPath,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isDotnetHost)
        {
            startInfo.ArgumentList.Add(assemblyPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        StringBuilder output = new();
        char[] buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            if (output.Length < MaximumChildOutputCharacters)
            {
                int retained = Math.Min(
                    read,
                    MaximumChildOutputCharacters - output.Length);
                output.Append(buffer, 0, retained);
            }
        }

        return output.ToString();
    }

    private static int ReadTestTimeoutSeconds()
    {
        string? value = Environment.GetEnvironmentVariable("RIMLIAISON_TEST_TIMEOUT_SECONDS");
        return int.TryParse(value, out int seconds)
            ? Math.Clamp(seconds, 1, MaximumTestTimeoutSeconds)
            : DefaultTestTimeoutSeconds;
    }

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static string BoundedSingleLine(string value)
    {
        string oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= MaximumChildOutputCharacters
            ? oneLine
            : oneLine[..MaximumChildOutputCharacters] + "...";
    }

    private static string ProcessDiagnostics()
    {
        using Process process = Process.GetCurrentProcess();
        return $"process=pid:{process.Id};threads:{process.Threads.Count};workingSetBytes:{process.WorkingSet64}";
    }

    private static long ElapsedMilliseconds(long started) =>
        (long)Math.Round(
            (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency);

    private readonly record struct IsolatedTestResult(
        int ExitCode,
        bool TimedOut,
        string StandardOutput,
        string StandardError);

    private static void RunnerTimeoutContainsHungChild()
    {
        IsolatedTestResult result = RunIsolatedAsync(["--timeout-probe"], 1)
            .GetAwaiter()
            .GetResult();
        Assert(result.TimedOut, "hung child was not timed out");
        Assert(result.ExitCode != 0, "timed out child reported success");
    }

    private static void ObservabilityRetainsBoundedParallelIdentities()
    {
        using AgentObservabilityStore parallelStore = new(
            options: new AgentObservabilityOptions { MaximumEvents = 20 });
        foreach (string runId in new[] { "run-a", "run-b" })
        {
            parallelStore.RegisterAgent(new AgentSnapshot
            {
                RunId = runId,
                AgentId = "agent-" + runId,
                ModId = "mod.test",
                ModName = "Test Mod",
                StartTime = 90
            });
            parallelStore.AppendEvent(new AgentEventRequest(
                runId,
                "agent-" + runId,
                "mod.test",
                DevelopmentStage.Testing,
                AgentEventTypes.CommandStarted,
                "started",
                new { command = "affected" },
                Timestamp: 100));
            parallelStore.AppendEvent(new AgentEventRequest(
                runId,
                "agent-" + runId,
                "mod.test",
                DevelopmentStage.Testing,
                AgentEventTypes.CommandCompleted,
                "completed",
                new { command = "affected", durationMs = 10 },
                Timestamp: 110));
        }

        IReadOnlyList<AgentEvent> parallelEvents = parallelStore.GetEvents(limit: 20);
        Assert(parallelEvents.Count == 4, "parallel events were overwritten");
        Assert(
            parallelEvents.Select(static value => value.RunId).Distinct(StringComparer.Ordinal).Count() == 2,
            "parallel run identity was not retained");

        using AgentObservabilityStore boundedStore = new(
            options: new AgentObservabilityOptions { MaximumEvents = 3 });
        boundedStore.RegisterAgent(new AgentSnapshot
        {
            RunId = "run-bounded",
            AgentId = "agent-bounded",
            ModId = "mod.test",
            ModName = "Test Mod",
            StartTime = 190
        });
        for (int index = 0; index < 6; index++)
        {
            boundedStore.AppendEvent(new AgentEventRequest(
                "run-bounded",
                "agent-bounded",
                "mod.test",
                DevelopmentStage.Testing,
                AgentEventTypes.ToolCompleted,
                "event",
                Timestamp: 200 + index));
        }

        IReadOnlyList<AgentEvent> boundedEvents = boundedStore.GetEvents(limit: 20);
        Assert(boundedEvents.Count == 3, "event retention exceeded its bound");
        Assert(
            boundedEvents.Select(static value => value.Sequence).SequenceEqual([4L, 5L, 6L]),
            "retention did not preserve the newest events in order");
    }

    private static void ObservedPerformanceStaysSeparateFromBenchmarks()
    {
        AgentEvent[] events =
        [
            ObservedEvent("run-a", 100, 1, AgentEventTypes.CommandStarted, new { command = "affected" }),
            ObservedEvent("run-a", 110, 2, AgentEventTypes.ValidationEvidenceDecision, new { action = RimContextDecisionActions.Reuse }),
            ObservedEvent("run-a", 120, 3, AgentEventTypes.SuiteCompleted, new { artifactFreshness = new { state = "fresh" } }),
            ObservedEvent("run-a", 200, 4, AgentEventTypes.CommandCompleted, new { command = "affected", durationMs = 100 }),
            ObservedEvent("run-b", 300, 5, AgentEventTypes.CommandStarted, new { command = "affected" }),
            ObservedEvent("run-b", 320, 6, AgentEventTypes.BuildStarted),
            ObservedEvent("run-b", 350, 7, AgentEventTypes.BuildSucceeded),
            ObservedEvent("run-b", 500, 8, AgentEventTypes.CommandCompleted, new { command = "affected", durationMs = 200 })
        ];

        RimContextObservedPerformanceSummary observed =
            RimLiaisonContextBundleProvider.ProjectObservedPerformance(events);
        Assert(observed.Status == "available", "observed sample did not become available");
        Assert(observed.SampleCount == 2, "observed samples were not grouped by run");
        Assert(observed.MedianWorkflowDurationMs == 100, "observed median is incorrect");
        Assert(observed.P90WorkflowDurationMs == 200, "observed p90 is incorrect");
        Assert(observed.ValidationReuseRate == 0.5, "observed reuse rate is incorrect");

        RimContextEfficiencyMetrics metrics = new()
        {
            BenchmarkSummary = GoldenWorkflowBenchmarkRunner.Summary(),
            ObservedPerformance = observed
        };
        string json = JsonSerializer.Serialize(metrics);
        Assert(json.Contains("\"benchmarkSummary\"", StringComparison.Ordinal), "synthetic benchmark missing");
        Assert(json.Contains("\"observedPerformance\"", StringComparison.Ordinal), "observed metrics missing");
        Assert(json.Contains("\"status\":\"available\"", StringComparison.Ordinal), "observed status missing");
    }

    private static void ObservedPerformanceReportsInsufficientData()
    {
        AgentEvent[] events =
        [
            ObservedEvent("run-a", 100, 1, AgentEventTypes.CommandStarted, new { command = "affected" }),
            ObservedEvent("run-a", 200, 2, AgentEventTypes.CommandCompleted, new { command = "affected", durationMs = 100 })
        ];

        RimContextObservedPerformanceSummary observed =
            RimLiaisonContextBundleProvider.ProjectObservedPerformance(events);
        Assert(observed.Status == "insufficient-data", "single workflow was treated as representative");
        Assert(observed.SampleCount == 1, "insufficient sample count is incorrect");
        Assert(observed.MedianWorkflowDurationMs is null, "insufficient median should be absent");
        Assert(observed.P90WorkflowDurationMs is null, "insufficient p90 should be absent");
    }

    private static AgentEvent ObservedEvent(
        string runId,
        long timestamp,
        long sequence,
        string type,
        object? data = null) =>
        new()
        {
            Id = $"{runId}-{sequence}",
            RunId = runId,
            AgentId = "agent-" + runId,
            ModId = "mod.test",
            Timestamp = timestamp,
            Sequence = sequence,
            Stage = DevelopmentStage.Testing,
            Type = type,
            Summary = type,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data)
        };

    private static void ValidCatalogLoads()
    {
        CatalogValidationResult result = CatalogValidator.Validate(CreateCatalog());

        Assert(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Code)));
        AssertSequence(
            ["assembler-smoke", "settings-smoke"],
            CatalogNavigator.ResolvedTestIds(CreateCatalog(), "smoke"));
        AssertSequence(
            ["smoke"],
            CatalogNavigator.ContainingSuiteIds(CreateCatalog(), "assembler-smoke"));
    }

    private static void IsolationMetadataValidatesSafeDefaults()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest
            {
                Id = "missing-key",
                Recipe = "recipe-a",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.SameGenerationSafe
                }
            },
            new CatalogTest
            {
                Id = "missing-reset",
                Recipe = "recipe-b",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FixtureResettable,
                    ReuseKey = "fixture"
                }
            },
            new CatalogTest
            {
                Id = "unexpected-reset",
                Recipe = "recipe-c",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.PureRead,
                    ReuseKey = "fixture",
                    ResetRecipe = "reset"
                }
            });

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "ISOLATION_REUSE_KEY_REQUIRED");
        AssertHasCode(result.Errors, "ISOLATION_RESET_RECIPE_REQUIRED");
        AssertHasCode(result.Errors, "ISOLATION_RESET_RECIPE_UNEXPECTED");
    }

    private static void DuplicateIdsFail()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "other-fixture"
        });

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "TEST_ID_DUPLICATE");
    }

    private static void MissingReferencesFail()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Suites[0].Tests.Add("does-not-exist");
        catalog.Suites[0].Suites.Add("missing-suite");

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "UNKNOWN_TEST_REFERENCE");
        AssertHasCode(result.Errors, "UNKNOWN_SUITE_REFERENCE");
    }

    private static void SuiteCyclesFail()
    {
        var catalog = new CatalogDocument
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest { Id = "one", Recipe = "fixture" }
            ],
            Suites =
            [
                new CatalogSuite { Id = "a", Suites = ["b"] },
                new CatalogSuite { Id = "b", Suites = ["a"] }
            ]
        };

        CatalogValidationResult result = CatalogValidator.Validate(catalog);

        AssertHasCode(result.Errors, "SUITE_CYCLE");
    }

    private static void MissingRecipesFail()
    {
        CatalogValidationResult result = CatalogValidator.Validate(
            CreateCatalog(),
            new HashSet<string>(["settings-fixture"], StringComparer.Ordinal));

        AssertHasCode(result.Errors, "MISSING_RECIPE_REFERENCE");
        Assert(!result.IsValid, "Unknown recipes must invalidate a checked catalog.");
    }

    private static void RecipeListLoads()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "recipes.json");
            File.WriteAllText(
                path,
                """
                {
                  "schemaVersion": "devbridge-test-recipe-list/v1",
                  "recipes": [
                    {"id": "assembler-fixture"},
                    {"id": "settings-fixture"}
                  ]
                }
                """);

            RecipeListLoadResult result = RecipeListLoader.Load(path);

            Assert(result.Errors.Count == 0, "Recipe list should load.");
            Assert(result.RecipeIds is not null, "Recipe ids should be returned.");
            Assert(result.RecipeIds!.Contains("assembler-fixture"), "Recipe id is missing.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ListIsMinimalAndSorted()
    {
        CliResult result = RunCli(CreateCatalog(), "list");

        AssertEqual(
            """{"tests":[{"id":"assembler-smoke","recipe":"assembler-fixture"},{"id":"settings-smoke","recipe":"settings-fixture"}]}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(string.IsNullOrEmpty(result.Stderr), "List should not write diagnostics.");
    }

    private static void ContextProviderProjectsOwnerState()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FixedGitRepositoryStateProvider(
                new GitRepositoryStateResult(
                    true,
                    new GitRepositoryStateSnapshot(
                        directory,
                        "git:fixture",
                        "main",
                        "head-1",
                        "upstream-1",
                        1,
                        0,
                        true,
                        [
                            new GitRepositoryChange("Source/Changed.cs", " M", false, false),
                            new GitRepositoryChange("obj/generated.dll", "??", true, true)
                        ])));
            var transport = new FakeTransport((request, cancellationToken) =>
            {
                Assert(
                    request.Arguments.Contains("doctor", StringComparer.Ordinal) ||
                    request.Arguments.Contains("snapshot", StringComparer.Ordinal),
                    "context runtime probe must use read-only DevBridge state commands");
                Assert(
                    !request.Arguments.Contains("start", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("restart", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("stop", StringComparer.Ordinal),
                    "context runtime probe must not mutate lifecycle state");
                string output = request.Arguments.Contains("snapshot", StringComparer.Ordinal)
                    ? "{\"generation\":7,\"phase\":\"READY\",\"quicktest\":{\"state\":\"idle\"},\"rimBridgeEndpoint\":{\"state\":\"ready\"}}"
                    : "{\"healthy\":true,\"generation\":7,\"processId\":123,\"lifecycleState\":\"READY\",\"runtimeIdentity\":{\"devBridgeSourceRoot\":\"C:/src/DevBridge2\",\"devBridgeRuntimeRoot\":\"C:/Games/RimWorld/Mods/DevBridge2\",\"devBridgePinnedWorktreeRoot\":\"C:/pinned/DevBridge2\",\"rimWorldRoot\":\"C:/Games/RimWorld\",\"rimWorldExecutable\":\"C:/Games/RimWorld/RimWorldWin64.exe\",\"resolutionSource\":\"canonical-machine-configuration\",\"rimWorldRootExists\":true,\"rimWorldExecutableExists\":true,\"devBridgeRuntimeRootExists\":true,\"installedRuntimeLayoutValid\":true,\"runtimeBelongsToRimWorld\":true}}";
                return new DevBridgeProcessResult(
                    0,
                    output,
                    string.Empty);
            });
            var provider = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = directory,
                    CatalogPath = catalogPath,
                    DevBridgePath = Path.Combine(directory, "DevBridge.cmd"),
                    DevBridgeRootPath = directory,
                    Project = "FixtureProject",
                    ObservabilityModId = "fixture.mod",
                    ObservabilityModName = "Fixture Mod",
                    GitProvider = git,
                    ProcessTransport = transport
                });

            DateTimeOffset observed = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
            RimContextProviderSnapshot snapshot = provider.CollectAsync(
                    new RimContextProviderRequest(
                        directory,
                        null,
                        [],
                        false,
                        observed,
                        16,
                        16,
                        16,
                        16))
                .GetAwaiter()
                .GetResult();
            RimContextBundle bundle = RimContextBundleBuilder.Build(
                new RimContextBundleRequest(RootPath: directory, NowUtc: observed),
                observed,
                [snapshot]);

            AssertEqual("available", bundle.Repository.Status);
            AssertEqual("main", bundle.Repository.Value!.Branch);
            AssertEqual(1, bundle.Repository.Value.ChangedFiles.Count);
            AssertEqual(1, bundle.Repository.Value.GeneratedFiles.Count);
            AssertEqual("generated", bundle.Repository.Value.GeneratedFiles[0].Category);
            AssertEqual("available", bundle.Runtime.Status);
            AssertEqual(7, bundle.Runtime.Value!.Generation);
            AssertEqual(
                "C:/Games/RimWorld/Mods/DevBridge2",
                bundle.Runtime.Value.RuntimeIdentity!.DevBridgeRuntimeRoot);
            AssertEqual("available", bundle.Testing.Status);
            Assert(
                bundle.Testing.Value!.AdditionalValidationRequired is null,
                "Git dirtiness without a RimTest decision must remain unknown test policy.");
            AssertEqual(
                RimContextBundleStatuses.Unknown,
                bundle.Environment.Value!.RimWorldVersion);
            string[] componentNames = bundle.Topology.Value!.Components
                .Select(static component => component.Name)
                .ToArray();
            Assert(
                componentNames.Contains("RimTest", StringComparer.Ordinal),
                "topology must include the RimTest owner node");
            Assert(
                componentNames.Contains("RimBridgeServer", StringComparer.Ordinal),
                "topology must include the RimBridgeServer dependency node");
            Assert(bundle.Topology.Value.Dependencies.All(dependency =>
                componentNames.Contains(dependency.From, StringComparer.Ordinal) &&
                componentNames.Contains(dependency.To, StringComparer.Ordinal)),
                "every topology dependency must resolve to a component node");
            Assert(transport.Requests.Count == 2, "context provider performs bounded doctor and agent snapshot probes");

            JsonElement configuration = snapshot.Extensions!
                .Single(extension => extension.Key == "configuration")
                .Value;
            AssertEqual("FixtureProject", configuration.GetProperty("project").GetString());
            AssertEqual("fixture.mod", configuration.GetProperty("modId").GetString());
            AssertEqual("Fixture Mod", configuration.GetProperty("modName").GetString());

            JsonElement stateHygiene = snapshot.Extensions!
                .Single(extension => extension.Key == "stateHygiene")
                .Value;
            Assert(
                !stateHygiene.TryGetProperty("canonicalObservability", out _),
                "compact context must omit routine absolute observability paths");
            Assert(
                snapshot.Topology!.Value!.Components.All(component => component.LocalPath is null),
                "compact context must omit component absolute paths");
            Assert(
                snapshot.Repository!.Value!.LocalPath is null,
                "compact context must omit repository absolute paths");
            Assert(
                snapshot.Environment!.Value!.Configuration.All(setting => !setting.Value.Contains(directory, StringComparison.OrdinalIgnoreCase)),
                "compact context must omit environment absolute paths");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ContextUsesCanonicalStackDiscovery()
    {
        string ecosystem = CreateTempDirectory();
        string root = Path.Combine(ecosystem, "RimLiaison");
        string devBridgeRoot = Path.Combine(ecosystem, "DevBridge2");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(devBridgeRoot);
        try
        {
            string catalogPath = Path.Combine(root, "catalog.json");
            string commandPath = Path.Combine(devBridgeRoot, "DevBridge.cmd");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            File.WriteAllText(commandPath, "@echo off");
            var git = new PathGitRepositoryStateProvider(new Dictionary<string, GitRepositoryStateResult>(
                StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(root)] = new GitRepositoryStateResult(
                    true,
                    new GitRepositoryStateSnapshot(
                        root,
                        "github:lanwoodall423/RimLiaison",
                        "main",
                        "liaison-head",
                        "liaison-upstream",
                        0,
                        0,
                        false,
                        [])),
                [Path.GetFullPath(devBridgeRoot)] = new GitRepositoryStateResult(
                    true,
                    new GitRepositoryStateSnapshot(
                        devBridgeRoot,
                        "github:lanwoodall423/DevBridge2",
                        "main",
                        "devbridge-head",
                        "devbridge-upstream",
                        1,
                        0,
                        true,
                        [new GitRepositoryChange("DevelopmentProjects/frontier.json", " M", false, false)]))
            });
            var transport = new FakeTransport((request, _) =>
            {
                AssertEqual(Path.GetFullPath(commandPath), request.FileName);
                AssertEqual(Path.GetFullPath(devBridgeRoot), request.WorkingDirectory);
                Assert(
                    !request.Arguments.Contains("start", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("restart", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("stop", StringComparer.Ordinal),
                    "canonical context discovery must remain read-only");
                string output = request.Arguments.Contains("snapshot", StringComparer.Ordinal)
                    ? "{\"generation\":9,\"quicktest\":{\"state\":\"idle\"},\"rimBridgeEndpoint\":{\"state\":\"ready\"},\"componentBuilds\":{\"mod\":{\"artifactSha256\":\"artifact-9\",\"loadedStatus\":\"loaded\"}}}"
                    : "{\"healthy\":true,\"generation\":9,\"processId\":456,\"lifecycleState\":\"READY\",\"rimWorldVersion\":\"1.6.1234\",\"components\":{\"coordinatorVersion\":\"3.0.0\",\"coordinatorBuild\":{\"sourceRevision\":\"devbridge-head\"}},\"rimBridge\":{\"lifecycleState\":\"READY\",\"version\":\"2.2.0\"}}";
                return new DevBridgeProcessResult(0, output, string.Empty);
            });
            var provider = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = root,
                    CatalogPath = catalogPath,
                    ObservabilityModId = "fixture.discovery",
                    GitProvider = git,
                    ProcessTransport = transport
                });

            RimContextProviderSnapshot snapshot = WithCurrentDirectory(
                root,
                () => provider.CollectAsync(new RimContextProviderRequest(
                        root,
                        null,
                        [],
                        false,
                        new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
                        16,
                        16,
                        16,
                        16))
                    .GetAwaiter()
                    .GetResult());

            AssertEqual(RimContextBundleStatuses.Available, snapshot.Runtime!.Status);
            AssertEqual(9, snapshot.Runtime.Value!.Generation);
            AssertEqual("1.6.1234", snapshot.Environment!.Value!.RimWorldVersion);
            RimContextRepositoryState related = snapshot.RelatedRepositories!.Single();
            AssertEqual("DevBridge2", related.Component);
            AssertEqual("devbridge-head", related.HeadSha);
            AssertEqual(1, related.Ahead);
            RimContextComponent component = snapshot.Topology!.Value!.Components
                .Single(value => value.Name == "DevBridge2");
            AssertEqual("github:lanwoodall423/DevBridge2", component.Repository);
            AssertEqual("devbridge-head", component.Commit);
            AssertEqual("3.0.0", component.Version);
            Assert(snapshot.Topology.Value.Components.Any(value =>
                value.Name == "RimBridgeServer" && value.Version == "2.2.0"),
                "owner component versions must flow into stack topology");
            JsonElement hygiene = snapshot.Extensions!
                .Single(extension => extension.Key == "stateHygiene")
                .Value;
            JsonElement devBridgeOwner = hygiene.GetProperty("externalOwnerState")
                .EnumerateArray()
                .Single(value => value.GetProperty("owner").GetString() == "DevBridge2");
            Assert(devBridgeOwner.GetProperty("configured").GetBoolean(),
                "canonical discovery must be reflected in state hygiene");
            AssertEqual(2, transport.Requests.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(ecosystem);
        }
    }

    private static void GitContextSeparatesDescriptorRecoveryState()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "DevelopmentProjects"));
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            string descriptor = Path.Combine(directory, "DevelopmentProjects", "frontier.json");
            string source = Path.Combine(directory, "Source", "Thing.cs");
            File.WriteAllText(descriptor, "{}");
            File.WriteAllText(source, "internal sealed class Thing {}");
            RunGit(directory, "init");
            RunGit(directory, "config", "user.email", "fixture@example.invalid");
            RunGit(directory, "config", "user.name", "Fixture");
            RunGit(directory, "add", ".");
            RunGit(directory, "commit", "-m", "fixture");

            string identity = "0123456789abcdef0123456789abcdef";
            File.WriteAllText(
                descriptor + ".recovery-backup-" + identity + ".json",
                "{\"backup\":true}");
            File.WriteAllText(
                descriptor + ".recovery-" + identity + ".tmp",
                "temporary");

            var provider = new SystemGitRepositoryStateProvider();
            GitRepositoryStateResult generatedOnly = provider.ReadAsync(directory)
                .GetAwaiter()
                .GetResult();
            Assert(generatedOnly.Resolved, "generated-only Git state should resolve");
            AssertEqual(2, generatedOnly.State!.Changes.Count);
            Assert(generatedOnly.State.Changes.All(static change => change.Generated),
                "legacy descriptor recovery state must be classified as generated");
            Assert(generatedOnly.State.SourceFingerprint is null,
                "generated-only dirtiness must not create a meaningful source fingerprint");

            File.AppendAllText(source, Environment.NewLine + "// meaningful edit");
            GitRepositoryStateResult meaningful = provider.ReadAsync(directory)
                .GetAwaiter()
                .GetResult();
            AssertEqual(1, meaningful.State!.Changes.Count(static change => !change.Generated));
            AssertEqual(2, meaningful.State.Changes.Count(static change => change.Generated));
            Assert(!string.IsNullOrWhiteSpace(meaningful.State.SourceFingerprint),
                "meaningful source dirtiness must retain a source fingerprint");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ContextKeepsUnavailableProvidersScoped()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FixedGitRepositoryStateProvider(new GitRepositoryStateResult(
                false,
                ErrorCode: "GIT_FIXTURE_UNAVAILABLE",
                Error: "fixture Git provider unavailable"));
            var transport = new FakeTransport((request, _) => new DevBridgeProcessResult(
                0,
                request.Arguments.Contains("snapshot", StringComparer.Ordinal)
                    ? "{\"generation\":4,\"rimBridgeEndpoint\":{\"state\":\"ready\"}}"
                    : "{\"healthy\":true,\"generation\":4,\"processId\":44,\"lifecycleState\":\"READY\"}",
                string.Empty));
            using var store = new AgentObservabilityStore();
            RimContextProviderSnapshot snapshot = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = directory,
                    CatalogPath = catalogPath,
                    DevBridgePath = Path.Combine(directory, "DevBridge.cmd"),
                    DevBridgeRootPath = directory,
                    ObservabilityModId = "fixture.unavailable",
                    GitProvider = git,
                    ProcessTransport = transport,
                    ObservabilityStore = store
                }).CollectAsync(new RimContextProviderRequest(
                    directory,
                    null,
                    [],
                    false,
                    DateTimeOffset.UtcNow,
                    16,
                    16,
                    16,
                    16)).GetAwaiter().GetResult();

            AssertEqual(RimContextBundleStatuses.Unavailable, snapshot.Repository!.Status);
            AssertEqual("GIT_FIXTURE_UNAVAILABLE", snapshot.Repository.ReasonCode);
            Assert(snapshot.Repository.Value is null, "unavailable Git must not fabricate repository state");
            AssertEqual(RimContextBundleStatuses.Available, snapshot.Runtime!.Status);
            AssertEqual(4, snapshot.Runtime.Value!.Generation);
            AssertEqual(RimContextBundleStatuses.Available, snapshot.Testing!.Status);
            AssertEqual(RimContextBundleStatuses.Available, snapshot.Environment!.Status);
            AssertEqual(RimContextBundleStatuses.Unknown, snapshot.Deployment!.Status);
            AssertEqual(2, transport.Requests.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ContextProjectsRimErrorStructuredStore()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            string storePath = Path.Combine(directory, "rimerror", "latest.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            DateTimeOffset observed = new(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
            new JsonFileDiagnosticStore(storePath).WriteAsync(new DiagnosticStoreSnapshot
            {
                CapturedAt = observed,
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "diag-runtime-1",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime_null_reference",
                        Message = "System.NullReferenceException in Fixture.Tick",
                        NormalizedMessage = "NullReferenceException in Fixture.Tick",
                        ExceptionType = "System.NullReferenceException",
                        OriginatingAssembly = "Fixture.Mod",
                        OriginatingType = "Fixture.Component",
                        OriginatingMethod = "Tick",
                        LastOccurrence = observed,
                        OccurrenceCount = 3
                    }
                ]
            }).GetAwaiter().GetResult();
            var git = new FixedGitRepositoryStateProvider(new GitRepositoryStateResult(
                true,
                new GitRepositoryStateSnapshot(
                    directory,
                    "git:fixture",
                    "main",
                    "head",
                    "upstream",
                    0,
                    0,
                    false,
                    [])));
            var transport = new FakeTransport((request, _) => new DevBridgeProcessResult(
                0,
                request.Arguments.Contains("snapshot", StringComparer.Ordinal)
                    ? "{\"generation\":4,\"rimBridgeEndpoint\":{\"state\":\"ready\"}}"
                    : "{\"healthy\":true,\"generation\":4,\"processId\":44,\"lifecycleState\":\"READY\"}",
                string.Empty));
            using var observability = new AgentObservabilityStore();
            RimContextProviderSnapshot snapshot = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = directory,
                    CatalogPath = catalogPath,
                    DevBridgePath = Path.Combine(directory, "DevBridge.cmd"),
                    DevBridgeRootPath = directory,
                    RimErrorStorePath = storePath,
                    ObservabilityModId = "fixture.rimerror",
                    GitProvider = git,
                    ProcessTransport = transport,
                    ObservabilityStore = observability
                }).CollectAsync(new RimContextProviderRequest(
                    directory,
                    null,
                    [],
                    false,
                    observed,
                    16,
                    16,
                    16,
                    16)).GetAwaiter().GetResult();

            RimContextFailure failure = snapshot.Failures!.Single();
            AssertEqual("diag-runtime-1", failure.SignatureCode);
            AssertEqual("Fixture.Mod", failure.OriginatingComponent);
            AssertEqual("runtime_null_reference", failure.Classification);
            Assert(failure.RootCause?.Contains("NullReferenceException", StringComparison.Ordinal) == true,
                "RimError's structured root-cause summary must be retained");
            AssertEqual("diag-runtime-1", failure.EvidenceId);
            AssertEqual("inspect-rimerror-diagnostic", failure.RecommendedAction);
            JsonElement status = snapshot.Extensions!
                .Single(extension => extension.Key == "providerStatus")
                .Value;
            AssertEqual("available", status.GetProperty("rimError").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ContextProviderProjectsSynchronizedOwnerEvidence()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FixedGitRepositoryStateProvider(
                new GitRepositoryStateResult(
                    true,
                    new GitRepositoryStateSnapshot(
                        directory,
                        "git:fixture",
                        "main",
                        "head-1",
                        "upstream-1",
                        0,
                        0,
                        false,
                        [],
                        "source-1")));
            bool staleRuntime = false;
            var transport = new FakeTransport((request, _) =>
            {
                Assert(
                    request.Arguments.Contains("doctor", StringComparer.Ordinal) ||
                    request.Arguments.Contains("snapshot", StringComparer.Ordinal),
                    "owner evidence uses read-only state probes");
                Assert(
                    !request.Arguments.Contains("start", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("restart", StringComparer.Ordinal) &&
                    !request.Arguments.Contains("stop", StringComparer.Ordinal),
                    "owner evidence never launches or restarts RimWorld");
                string output = request.Arguments.Contains("snapshot", StringComparer.Ordinal)
                    ? staleRuntime
                        ? "{\"generation\":6,\"phase\":\"READY\",\"currentGenerationTrust\":\"stale\",\"quicktest\":{\"state\":\"unknown\"},\"rimBridgeEndpoint\":{\"state\":\"stale\"},\"componentBuilds\":{\"mod\":{\"artifactSha256\":\"artifact-old\",\"loadedStatus\":\"stale\"}}}"
                        : "{\"generation\":7,\"phase\":\"READY\",\"currentGenerationTrust\":\"trusted\",\"quicktest\":{\"state\":\"passed\",\"evidence\":\"quick-1\"},\"rimBridgeEndpoint\":{\"state\":\"ready\",\"mode\":\"via-devbridge\"},\"requestingAgentLease\":{\"state\":\"held\",\"leaseId\":\"lease-1\",\"agentId\":\"agent-1\"},\"maintenance\":{\"ready\":true},\"componentBuilds\":{\"mod\":{\"artifactSha256\":\"artifact-1\",\"loadedStatus\":\"loaded\"}}}"
                    : staleRuntime
                        ? "{\"healthy\":false,\"generation\":6,\"processId\":123,\"lifecycleState\":\"READY\",\"operationalState\":{\"processRunning\":true,\"currentGenerationTrust\":\"stale\"},\"rimBridge\":{\"lifecycleState\":\"STALE\"}}"
                        : "{\"healthy\":true,\"generation\":7,\"processId\":123,\"lifecycleState\":\"READY\",\"operationalState\":{\"processRunning\":true,\"maintenanceReady\":true,\"currentGenerationTrust\":\"trusted\"},\"rimBridge\":{\"lifecycleState\":\"READY\"},\"components\":{\"coordinatorVersion\":\"2.1.0\"}}";
                return new DevBridgeProcessResult(0, output, string.Empty);
            });
            using var store = new AgentObservabilityStore();
            using var run = new AgentObservabilityRun(
                "run-context-evidence",
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.context-evidence",
                "Context Evidence");
            agent.Start();
            using IDisposable activation = agent.Activate();
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.SuiteCompleted,
                "authoritative suite completed",
                new
                {
                    operationKey = "suite:smoke",
                    suiteId = "smoke",
                    selectedSuites = new[] { "smoke" },
                    executedSuites = new[] { "smoke" },
                    selectedTests = new[] { "assembler-smoke" },
                    executedTests = new[] { "assembler-smoke" },
                    result = "pass",
                    status = "pass",
                    durationMs = 42,
                    reuseStatus = "used",
                    artifactFreshness = new
                    {
                        sourceFingerprint = "source-1",
                        builtArtifactSha256 = "artifact-1",
                        deployedArtifactSha256 = "artifact-1",
                        deploymentDecision = "unchanged",
                        evaluationStatus = "FRESH",
                        generation = 7,
                        transactionId = "tx-1",
                        proof = "proof-1"
                    }
                });

            var provider = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = directory,
                    CatalogPath = catalogPath,
                    DevBridgePath = Path.Combine(directory, "DevBridge.cmd"),
                    DevBridgeRootPath = directory,
                    ObservabilityModId = "mod.context-evidence",
                    GitProvider = git,
                    ProcessTransport = transport,
                    ObservabilityStore = store
                });
            RimContextProviderSnapshot snapshot = provider.CollectAsync(new RimContextProviderRequest(
                    directory,
                    null,
                    [],
                    false,
                    DateTimeOffset.UtcNow,
                    16,
                    16,
                    16,
                    16))
                .GetAwaiter()
                .GetResult();
            RimContextBundle bundle = RimContextBundleBuilder.Build(
                new RimContextBundleRequest(RootPath: directory, NowUtc: DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow,
                [snapshot]);

            AssertEqual("available", bundle.Runtime.Status);
            AssertEqual(7, bundle.Runtime.Value!.Generation);
            AssertEqual("passed", bundle.Runtime.Value.QuicktestState);
            AssertEqual("lease-1", bundle.Runtime.Value.LeaseId);
            AssertEqual("loaded", bundle.Runtime.Value.RuntimeArtifactStatus);
            AssertEqual("smoke", bundle.Testing.Value!.ExecutedSuites.Single());
            AssertEqual("hit", bundle.Testing.Value.CacheStatus);
            AssertEqual(false, bundle.Testing.Value.AdditionalValidationRequired);
            AssertEqual("synchronized", bundle.Deployment.Value!.Correspondence);
            AssertEqual("healthy", bundle.AgentSummary.Status);
            Assert(bundle.AgentSummary.ReusableEvidence.Contains("proof-1", StringComparer.Ordinal), "valid proof is reusable");
            AssertEqual(2, transport.Requests.Count);

            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.SuiteCompleted,
                "authoritative suite blocked by infrastructure",
                new
                {
                    operationKey = "suite:smoke",
                    suiteId = "smoke",
                    selectedSuites = new[] { "smoke" },
                    executedSuites = new[] { "smoke" },
                    selectedTests = new[] { "assembler-smoke" },
                    executedTests = Array.Empty<string>(),
                    result = "infrastructure",
                    status = "infrastructure",
                    durationMs = 17,
                    reuseStatus = "invalidated",
                    reuseInvalidationReason = "RUNTIME_GENERATION_STALE",
                    infrastructureFailure = true,
                    retryable = true,
                    artifactFreshness = new
                    {
                        sourceFingerprint = "source-1",
                        builtArtifactSha256 = "artifact-2",
                        deployedArtifactSha256 = "artifact-2",
                        deploymentDecision = "deployed",
                        evaluationStatus = "STALE",
                        generation = 7,
                        transactionId = "tx-2",
                        proof = "proof-invalid"
                    }
                });
            staleRuntime = true;
            DateTimeOffset degradedNow = DateTimeOffset.UtcNow;
            RimContextProviderSnapshot degradedSnapshot = provider.CollectAsync(
                    new RimContextProviderRequest(
                        directory,
                        null,
                        [],
                        false,
                        degradedNow,
                        16,
                        16,
                        16,
                        16))
                .GetAwaiter()
                .GetResult();
            RimContextBundle degraded = RimContextBundleBuilder.Build(
                new RimContextBundleRequest(RootPath: directory, NowUtc: degradedNow),
                degradedNow,
                [degradedSnapshot]);

            AssertEqual(RimContextBundleStatuses.Stale, degraded.Runtime.Status);
            AssertEqual("runtime-mismatch", degraded.Deployment.Value!.Correspondence);
            AssertEqual("mismatch", degraded.Deployment.Value.DeploymentRuntimeCorrespondence);
            AssertEqual(true, degraded.Testing.Value!.InfrastructureFailure);
            AssertEqual(true, degraded.Testing.Value.Retryable);
            AssertEqual(true, degraded.Testing.Value.AdditionalValidationRequired);
            AssertEqual("RUNTIME_GENERATION_STALE", degraded.Testing.Value.InvalidationReason);
            Assert(degraded.Testing.Value.InvalidatedEvidence.Any(evidence =>
                evidence.Id == "proof-invalid" && evidence.Status == "invalidated"),
                "owner invalidation evidence must remain explicit");
            AssertEqual("blocked", degraded.AgentSummary.Status);
            AssertEqual(4, transport.Requests.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidationEvidenceIdentityIsImmutableAndRelevant()
    {
        ValidationEvidenceIdentity identity = CreateValidationIdentity(
            "content-1",
            ["Source/Thing.cs"],
            ["compile", "quicktest"],
            runtimeGeneration: 4,
            build: "build-1",
            deployment: "deploy-1");
        ValidationEvidenceRecord first = ValidationEvidenceRecord.Create(
            identity,
            "pass",
            new DateTimeOffset(2026, 8, 22, 1, 2, 3, TimeSpan.Zero));
        ValidationEvidenceRecord later = ValidationEvidenceRecord.Create(
            identity,
            "pass",
            new DateTimeOffset(2026, 8, 22, 4, 5, 6, TimeSpan.Zero));

        Assert(first.IsSelfConsistent, "fresh evidence should be self-consistent");
        Assert(first.Reusable, "complete passing evidence should be reusable");
        Assert(
            first.EvidenceId == later.EvidenceId,
            "timestamps must not change identity");
        Assert(
            first.EvidenceId != ValidationEvidenceRecord.Create(
                    identity with { ContentFingerprint = "content-2" },
                    "pass",
                    first.RecordedAtUtc)
                .EvidenceId,
            "relevant source content must change identity");
        Assert(
            first.EvidenceId != ValidationEvidenceRecord.Create(
                    identity with { TestIds = ["compile", "different-test"] },
                    "pass",
                    first.RecordedAtUtc)
                .EvidenceId,
            "test identity must change evidence identity");
        Assert(
            first.EvidenceId != ValidationEvidenceRecord.Create(
                    identity with { DeploymentArtifactSha256 = "deploy-2" },
                    "pass",
                    first.RecordedAtUtc)
                .EvidenceId,
            "runtime artifact identity must change evidence identity");
    }

    private static void PublicationGateSeparatesReuseAndInvalidation()
    {
        ValidationChangeAnalysis documentation = ValidationChangeAnalyzer.Analyze(
            [new GitRepositoryChange("docs/README.md", "M", false, false)]);
        ValidationPublicationResult documentationResult = ValidationPublicationGate.Evaluate(
            documentation,
            CreateValidationIdentity("docs", ["docs/README.md"], []),
            [],
            DateTimeOffset.UtcNow);
        Assert(documentationResult.SafeToPublish, "documentation changes should skip safely");
        AssertEqual("skip", documentationResult.PublicationAction);

        ValidationChangeAnalysis runtime = ValidationChangeAnalyzer.Analyze(
            [new GitRepositoryChange("Source/Thing.cs", "M", false, false)]);
        ValidationEvidenceIdentity staticIdentity = CreateValidationIdentity(
            "source-1",
            ["Source/Thing.cs"],
            ["compile"]);
        ValidationEvidenceIdentity staleRuntimeIdentity = CreateValidationIdentity(
            "source-1",
            ["Source/Thing.cs"],
            ["quicktest"],
            runtimeGeneration: 1,
            build: "build-old",
            deployment: "deploy-old");
        ValidationEvidenceRecord[] evidence =
        [
            ValidationEvidenceRecord.Create(staticIdentity, "pass", DateTimeOffset.UtcNow),
            ValidationEvidenceRecord.Create(staleRuntimeIdentity, "pass", DateTimeOffset.UtcNow.AddSeconds(1))
        ];
        ValidationPublicationResult result = ValidationPublicationGate.Evaluate(
            runtime,
            CreateValidationIdentity(
                "source-1",
                ["Source/Thing.cs"],
                [],
                runtimeGeneration: 2,
                build: "build-current",
                deployment: "deploy-current"),
            evidence,
            DateTimeOffset.UtcNow);
        Assert(!result.SafeToPublish, "stale runtime evidence must block publication");
        AssertEqual(1, result.ReusedEvidenceCount);
        AssertEqual(1, result.InvalidatedEvidenceCount);
        Assert(
            result.Decisions.Any(decision =>
                decision.Action == RimContextDecisionActions.Invalidate &&
                decision.ReasonCode == ValidationDecisionReasonCodes.EvidenceDeploymentMismatch),
            "deployment mismatch should invalidate runtime evidence specifically");
        Assert(
            result.Decisions.Any(decision =>
                decision.Action == RimContextDecisionActions.Reuse &&
                decision.ValidationKind == ValidationEvidenceKinds.Static),
            "static evidence should remain reusable across deployment mismatch");
        ValidationEvidenceIdentity dependencyEvidenceIdentity = CreateValidationIdentity(
            "source-1",
            ["Source/Thing.cs"],
            ["quicktest"],
            runtimeGeneration: 4,
            build: "build-4",
            deployment: "deploy-4",
            dependencies: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["framework"] = "old"
            });
        ValidationPublicationResult dependencyResult = ValidationPublicationGate.Evaluate(
            runtime,
            CreateValidationIdentity(
                "source-1",
                ["Source/Thing.cs"],
                [],
                runtimeGeneration: 4,
                build: "build-4",
                deployment: "deploy-4",
                dependencies: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["framework"] = "new"
                }),
            [ValidationEvidenceRecord.Create(
                dependencyEvidenceIdentity,
                "pass",
                DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);
        Assert(!dependencyResult.SafeToPublish, "A dependency identity change must invalidate canonical evidence.");
        Assert(dependencyResult.Decisions.Any(decision =>
            decision.Action == RimContextDecisionActions.Invalidate &&
            decision.ReasonCode == ValidationDecisionReasonCodes.EvidenceInputMismatch),
            "Dependency changes must be reported as canonical evidence input invalidation.");
    }

    private static void PublicationGateRejectsNewlySelectedTests()
    {
        ValidationChangeAnalysis analysis = ValidationChangeAnalyzer.Analyze(
            [new GitRepositoryChange("Source/Thing.cs", "M", false, false)]);
        ValidationEvidenceIdentity evidenceIdentity = CreateValidationIdentity(
            "source-1",
            ["Source/Thing.cs"],
            ["compile"]);
        ValidationPublicationResult result = ValidationPublicationGate.Evaluate(
            analysis,
            CreateValidationIdentity(
                "source-1",
                ["Source/Thing.cs"],
                ["compile", "quicktest"]),
            [ValidationEvidenceRecord.Create(
                evidenceIdentity,
                "pass",
                DateTimeOffset.UtcNow)],
            DateTimeOffset.UtcNow);

        Assert(!result.SafeToPublish, "newly selected tests must not reuse narrower evidence");
        Assert(result.Decisions.Any(decision =>
            decision.Action == RimContextDecisionActions.Invalidate &&
            decision.ReasonCode == ValidationDecisionReasonCodes.EvidenceTestIdentityMismatch),
            "newly selected tests must invalidate canonical evidence");
    }

    private static void GoldenWorkflowBenchmarkMatchesBaseline()
    {
        GoldenWorkflowBenchmarkReport first = GoldenWorkflowBenchmarkRunner.Run();
        GoldenWorkflowBenchmarkReport second = GoldenWorkflowBenchmarkRunner.Run();
        AssertEqual(8, first.PassedScenarioCount);
        AssertEqual(0, first.RegressionCount);
        AssertEqual(8, first.Scenarios.Count);
        Assert(
            JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second),
            "golden benchmark output must be deterministic");
        Assert(
            first.Scenarios.Single(scenario => scenario.ScenarioId == GoldenWorkflowScenarioIds.GeneratedState)
                .ExpensiveOperationCount == 0,
            "generated observability state must not become expensive validation");

        AssertEqual("current-implementation", first.BaselineSource);
        AssertEqual("operation-counts", first.BaselineComparison);
        Assert(
            first.Scenarios.All(scenario => scenario.DurationBasis == "deterministic-cost-envelope-v1"),
            "deterministic benchmark scenarios must declare their duration basis");

        GoldenWorkflowBenchmarkReport measured = GoldenWorkflowBenchmarkRunner.RunMeasured();
        Assert(measured.MeasuredDurationMs is >= 0, "measured benchmark duration should be non-negative");
        Assert(
            measured.Scenarios.All(scenario => scenario.MeasuredDurationMs is >= 0),
            "measured benchmark scenarios should expose non-negative durations");
    }

    private static void FailureKnowledgeMatchesGeneratedState()
    {
        FailureKnowledgeMatch? exact = FailureKnowledgeCatalog.Match(
            FailureKnowledgeCatalog.GeneratedStateTransactionFailure,
            "transaction failed",
            "development-transaction");
        Assert(exact is not null, "reviewed signature should match");
        AssertEqual("reviewed", exact!.Entry.Confidence);
        Assert(exact.Entry.InappropriateActions.Count > 0, "knowledge should include unsafe actions");

        FailureKnowledgeMatch? generalized = FailureKnowledgeCatalog.Match(
            "UNKNOWN",
            "generated observability state caused a worktree change transaction failure",
            "infrastructure",
            [".rimdev/observability/events.json"]);
        Assert(generalized is not null, "generated state terms should use generalized knowledge");
        AssertEqual("generated-state-path", generalized!.MatchReason);
        Assert(
            generalized.Entry.EvidenceImpact.Contains("does not invalidate", StringComparison.OrdinalIgnoreCase),
            "knowledge should describe evidence impact");
    }

    private static void ContextExposesProvenanceBenchmarksAndFailureKnowledge()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FixedGitRepositoryStateProvider(
                new GitRepositoryStateResult(
                    true,
                    new GitRepositoryStateSnapshot(
                        directory,
                        "git:fixture",
                        "main",
                        "head-1",
                        null,
                        0,
                        0,
                        false,
                        [])));
            using var store = new AgentObservabilityStore();
            using var run = new AgentObservabilityRun(
                "run-provenance-context",
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.provenance-context",
                "Provenance Context");
            agent.Start();
            using IDisposable activation = agent.Activate();
            ValidationEvidenceRecord evidence = ValidationEvidenceRecord.Create(
                CreateValidationIdentity("content-1", ["Source/Thing.cs"], ["compile"]),
                "pass",
                DateTimeOffset.UtcNow);
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.ValidationEvidenceRecorded,
                "validation evidence recorded",
                new { validationEvidence = evidence });
            agent.Record(
                DevelopmentStage.Analysis,
                AgentEventTypes.IntegrationFailed,
                "generated observability state caused a worktree change transaction failure",
                new
                {
                    operationKey = FailureKnowledgeCatalog.GeneratedStateTransactionFailure,
                    classification = "development-transaction",
                    relatedFiles = new[] { ".rimdev/observability/events.json" },
                    retryable = true,
                    infrastructureOnly = true
                });
            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "unclassified command failure without an established cause",
                new
                {
                    operationKey = "UNCLASSIFIED_COMMAND_FAILURE",
                    classification = "application"
                });
            RimContextProviderSnapshot snapshot = new RimLiaisonContextBundleProvider(
                new RimLiaisonContextProviderOptions
                {
                    RootPath = directory,
                    CatalogPath = catalogPath,
                    ObservabilityModId = "mod.provenance-context",
                    GitProvider = git,
                    ObservabilityStore = store
                })
                .CollectAsync(new RimContextProviderRequest(
                    directory,
                    null,
                    [],
                    false,
                    DateTimeOffset.UtcNow,
                    16,
                    16,
                    16,
                    16))
                .GetAwaiter()
                .GetResult();
            RimContextBundle bundle = RimContextBundleBuilder.Build(
                new RimContextBundleRequest(RootPath: directory, NowUtc: DateTimeOffset.UtcNow),
                DateTimeOffset.UtcNow,
                [snapshot]);

            Assert(
                bundle.Testing.Value!.LatestEvidence.Any(reference => reference.Id == evidence.EvidenceId),
                "context should expose immutable evidence identity");
            AssertEqual(8, bundle.Testing.Value.BenchmarkSummary!.ScenarioCount);
            Assert(
                bundle.Failures.Any(failure => failure.Knowledge?.SignatureCode ==
                    FailureKnowledgeCatalog.GeneratedStateTransactionFailure),
                "context should consume reviewed failure knowledge");
            Assert(
                bundle.Failures.Any(failure => failure.Knowledge?.RecommendedAction.Contains(
                    "owning RimLiaison", StringComparison.OrdinalIgnoreCase) == true),
                "context should expose the reviewed knowledge action");
            RimContextFailure reviewed = bundle.Failures.First(failure =>
                failure.SignatureCode == FailureKnowledgeCatalog.GeneratedStateTransactionFailure);
            AssertEqual(reviewed.Knowledge!.KnownCause, reviewed.RootCause);
            RimContextFailure[] unclassified = bundle.Failures.Where(failure =>
                    failure.SignatureCode == "UNCLASSIFIED_COMMAND_FAILURE")
                .ToArray();
            Assert(
                unclassified.Length > 0 && unclassified.All(static failure => failure.RootCause is null),
                "an issue summary must not be promoted to an established root cause");
            Assert(
                unclassified.All(static failure => failure.RetryAppropriate is null),
                "retry policy must remain unknown without owner or reviewed knowledge evidence");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ValidationEvidenceIdentity CreateValidationIdentity(
        string content,
        IReadOnlyList<string> sourceInputs,
        IReadOnlyList<string> testIds,
        int? runtimeGeneration = null,
        string? build = null,
        string? deployment = null,
        IReadOnlyDictionary<string, string>? dependencies = null) =>
        new()
        {
            Repository = "git:fixture",
            ContentFingerprint = content,
            SelectedSourceInputs = sourceInputs,
            DependencyFingerprints = dependencies ??
                new Dictionary<string, string>(StringComparer.Ordinal),
            BuildArtifactSha256 = build,
            DeploymentArtifactSha256 = deployment,
            ValidationKind = runtimeGeneration.HasValue
                ? ValidationEvidenceKinds.Runtime
                : ValidationEvidenceKinds.Static,
            CoveredKinds = runtimeGeneration.HasValue
                ? [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime]
                : [ValidationEvidenceKinds.Static],
            SuiteId = "fixture-suite",
            TestIds = testIds,
            ToolVersions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rimliaison"] = "fixture"
            },
            Configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile"] = "fixture"
            },
            EnvironmentFingerprint = "fixture-environment",
            RuntimeGeneration = runtimeGeneration,
            RequiresRuntimeGeneration = runtimeGeneration.HasValue,
            DeploymentCorrespondence = runtimeGeneration.HasValue ? "synchronized" : null
        };

    private static void EnvironmentFallbackLeavesListUsable()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            "smoke",
            () => RunCli(CreateCatalog(), "list"));

        AssertEqual(
            """{"tests":[{"id":"assembler-smoke","recipe":"assembler-fixture"},{"id":"settings-smoke","recipe":"settings-fixture"}]}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add list diagnostics.");
    }

    private static void EnvironmentFallbackLeavesRunUsable()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        CliResult result = WithFallbackSuiteEnvironment(
            "settings",
            () => RunCatalogCliWithAdapter(
                CreateCatalog(),
                adapter,
                "run",
                "assembler-smoke"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual("assembler-smoke", root.GetProperty("test").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add run diagnostics.");
    }

    private static void EnvironmentFallbackLeavesDoctorUsable()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            "settings",
            () => RunDoctorFixture(contextAvailable: true));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ready", document.RootElement.GetProperty("status").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add doctor diagnostics.");
    }

    private static void ExplicitFallbackIsRejectedOnUnrelatedCommand()
    {
        CliResult result = WithFallbackSuiteEnvironment(
            null,
            () => RunCli(CreateCatalog(), "list", "--fallback-suite", "smoke"));

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("error", document.RootElement.GetProperty("status").GetString());
        AssertEqual("CLI_INVALID", document.RootElement.GetProperty("code").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An invalid fallback option must remain machine-readable.");
    }

    private static void MissingRunUsesNotFoundContract()
    {
        CliResult result = RunCli(CreateCatalog(), "run", "does-not-exist");

        AssertEqual(CliExitCodes.NotFound, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("rimtest-result/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("invalid", root.GetProperty("status").GetString());
        AssertEqual("does-not-exist", root.GetProperty("test").GetString());
        AssertEqual("TEST_NOT_FOUND", root.GetProperty("errorCode").GetString());
        Assert(string.IsNullOrEmpty(result.Stderr),
            "A missing test must not write a human stderr transcript.");
    }

    private static void MissingShowAndSuiteCommandsUseNotFoundExitCode()
    {
        CliResult show = RunCli(CreateCatalog(), "show", "does-not-exist");
        CliResult suiteShow = RunCli(CreateCatalog(), "suite", "show", "does-not-exist");
        CliResult suiteRun = RunCli(CreateCatalog(), "suite", "run", "does-not-exist");

        AssertEqual(CliExitCodes.NotFound, show.ExitCode);
        AssertEqual(CliExitCodes.NotFound, suiteShow.ExitCode);
        AssertEqual(CliExitCodes.NotFound, suiteRun.ExitCode);
        AssertEqual(
            "TEST_NOT_FOUND",
            JsonDocument.Parse(show.Stdout).RootElement.GetProperty("code").GetString());
        AssertEqual(
            "SUITE_NOT_FOUND",
            JsonDocument.Parse(suiteShow.Stdout).RootElement.GetProperty("code").GetString());
        AssertEqual(
            "SUITE_NOT_FOUND",
            JsonDocument.Parse(suiteRun.Stdout).RootElement.GetProperty("code").GetString());
        Assert(string.IsNullOrEmpty(show.Stderr) &&
            string.IsNullOrEmpty(suiteShow.Stderr) &&
            string.IsNullOrEmpty(suiteRun.Stderr),
            "Not-found commands must not write human stderr transcripts.");
    }

    private static void SuiteRunParseErrorsAreNotSingleTestResults()
    {
        CliResult result = RunCli(
            CreateCatalog(),
            "suite",
            "run",
            "smoke",
            "--fallback-suite",
            "settings");

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("CLI_INVALID", root.GetProperty("code").GetString());
        Assert(!root.TryGetProperty("schemaVersion", out _),
            "A suite parse error must not use the single-test result schema.");
        Assert(!result.Stdout.Contains("rimtest-result/v1", StringComparison.Ordinal),
            "A suite parse error must not pretend the suite name is a test id.");

        CliResult incompleteFailFast = RunCli(
            CreateCatalog(),
            "affected",
            "--fail-fast");
        AssertEqual(CliExitCodes.InvalidInput, incompleteFailFast.ExitCode);
        AssertEqual(
            "CLI_INVALID",
            JsonDocument.Parse(incompleteFailFast.Stdout)
                .RootElement
                .GetProperty("code")
                .GetString());
    }

    private static void ShowExposesMetadata()
    {
        CliResult result = RunCli(CreateCatalog(), "show", "assembler-smoke");

        AssertEqual(
            """{"test":{"id":"assembler-smoke","recipe":"assembler-fixture","cost":"low","suites":["smoke"],"description":"Checks assembler registration.","tags":["assembler","crafting"],"covers":[{"kind":"csharp_type","name":"CompAssembler"},{"kind":"def","name":"CCM_Assembler"}]}}""",
            result.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, result.ExitCode);
    }

    private static void InvalidCatalogFailsBeforeCommand()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "duplicate"
        });

        CliResult result = RunCli(catalog, "list");

        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        Assert(result.Stdout.Contains(
            "\"code\":\"CATALOG_INVALID\"",
            StringComparison.Ordinal), "Invalid catalog error was not returned.");
        Assert(!result.Stdout.Contains(
            "\"tests\":[",
            StringComparison.Ordinal), "Invalid catalog must not produce a list.");
    }

    private static void SuiteAndValidationCommandsWork()
    {
        CliResult suites = RunCli(CreateCatalog(), "suites");
        AssertEqual(
            """{"suites":[{"id":"settings"},{"id":"smoke"}]}""",
            suites.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, suites.ExitCode);

        CliResult suite = RunCli(CreateCatalog(), "suite", "show", "smoke");
        AssertEqual(
            """{"suite":{"id":"smoke","tests":["assembler-smoke"],"suites":["settings"],"resolvedTests":["assembler-smoke","settings-smoke"]}}""",
            suite.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, suite.ExitCode);

        CliResult validation = RunCli(CreateCatalog(), "validate");
        AssertEqual(
            """{"valid":true,"tests":2,"suites":2,"recipeVerification":"skipped"}""",
            validation.Stdout.Trim());
        AssertEqual(CliExitCodes.Success, validation.ExitCode);
    }

    private static void CatalogTestDelegatesRecipeId()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": true,
                  "generation": 3,
                  "finalNextAction": "status"
                }
                """));
        var runner = new CatalogTestRecipeRunner(CreateAdapter(transport));

        CatalogTestRunResult result = runner.RunAsync(
                CreateCatalog(),
                "assembler-smoke")
            .GetAwaiter()
            .GetResult();

        AssertEqual("assembler-smoke", result.TestId);
        AssertEqual("assembler-fixture", result.RecipeId);
        AssertEqual(DevBridgeOutcomeKind.Success, result.RecipeResult.Status.Outcome);
        AssertEqual("assembler-fixture", transport.Requests.Single().Arguments[5]);
    }

    private static void WorkflowCorrelationReachesDevBridge()
    {
        const string workflowId = "rw-correlation-1";
        var transport = new FakeTransport(
            (request, _) =>
            {
                int index = request.Arguments.ToList().IndexOf("--workflow-id");
                Assert(index >= 0 && request.Arguments[index + 1] == workflowId,
                    "RimLiaison did not pass workflowId to DevBridge.");
                return ProcessResult(
                    $$"""
                    {
                      "schemaVersion":"devbridge-test-recipe-run/v1",
                      "recipe":"fixture",
                      "success":true,
                      "workflowId":"{{workflowId}}",
                      "generation":2,
                      "operations":[
                        {"tool":"rimworld/fixture","success":true,"operationId":"op-1","workflowId":"{{workflowId}}","generation":2,"launchId":"launch-2"}
                      ]
                    }
                    """);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", workflowId)
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual(workflowId, result.WorkflowId);
        AssertEqual("op-1", result.Operations.Single().OperationId);
        AssertEqual(workflowId, result.Operations.Single().WorkflowId);
    }

    private static void OldDevBridgeResponsesRemainCompatible()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion":"devbridge-test-recipe-run/v1",
                  "recipe":"fixture",
                  "success":true,
                  "generation":2
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-old-response")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual("rw-old-response", result.WorkflowId);
    }

    private static void OldDevBridgeRequestParsersRemainCompatible()
    {
        int calls = 0;
        var transport = new FakeTransport(
            (request, _) =>
            {
                calls++;
                bool hasWorkflowOption = request.Arguments.Contains(
                    "--workflow-id", StringComparer.Ordinal);
                if (calls == 1)
                {
                    Assert(hasWorkflowOption, "The first request must carry workflowId.");
                    return ProcessResult(
                        """
                        {
                          "schemaVersion":"devbridge-test-recipe-run/v1",
                          "recipe":"fixture",
                          "success":false,
                          "errorCode":"TEST_RECIPE_USAGE",
                          "error":"unknown recipe run option."
                        }
                        """,
                        exitCode: 2);
                }

                Assert(!hasWorkflowOption, "Compatibility retry must omit workflowId.");
                return ProcessResult(
                    """
                    {
                      "schemaVersion":"devbridge-test-recipe-run/v1",
                      "recipe":"fixture",
                      "success":true,
                      "generation":2
                    }
                    """);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-old-parser")
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, calls);
        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        AssertEqual("rw-old-parser", result.WorkflowId);
    }

    private static void MismatchedWorkflowIdsFailClosed()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion":"devbridge-test-recipe-run/v1",
                  "recipe":"fixture",
                  "success":true,
                  "workflowId":"rw-other"
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", "rw-requested")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.MalformedResponse, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_WORKFLOW_ID_MISMATCH", result.Status.ErrorCode);
        AssertEqual("rw-requested", result.WorkflowId);
    }

    private static void CompactFinalOutputIncludesWorkflowId()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");

        CliResult result = RunCatalogCliWithAdapter(
            CreateCatalog(),
            adapter,
            "run",
            "assembler-smoke",
            "--json");
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;

        Assert(root.TryGetProperty("workflowId", out JsonElement workflow) &&
               workflow.GetString()!.StartsWith("rw-", StringComparison.Ordinal),
            "RimLiaison final output did not expose the workflow id.");
        Assert(!root.TryGetProperty("operations", out _),
            "RimLiaison final output embedded operation telemetry.");
        Assert(!result.Stdout.Contains("Player.log", StringComparison.Ordinal),
            "RimLiaison final output embedded log content.");
    }

    private static void PlanPreservesNoLaunchResult()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "fixture",
                  "alreadySatisfied": true,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "none",
                  "blockedBy": []
                }
                """));

        DevBridgeRecipePlanResult result = CreateAdapter(transport)
            .PlanAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        Assert(result.Plan is not null, "Plan was not returned.");
        Assert(result.Plan!.AlreadySatisfied, "Plan should be already satisfied.");
        AssertEqual(0, result.Plan.EstimatedRimWorldLaunches);
        AssertEqual("plan", transport.Requests.Single().Arguments[4]);
    }

    private static void RecipeCliDelegatesStructuredPlan()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "fixture",
                  "alreadySatisfied": true,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "none",
                  "blockedBy": []
                }
                """));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using AgentObservabilityStore observabilityStore = new();

        int exitCode = CliApplication.RunAsync(
                ["recipe", "plan", "fixture"],
                stdout,
                stderr,
                CreateAdapter(transport),
                observabilityStore: observabilityStore)
            .GetAwaiter()
            .GetResult();

        AssertEqual(CliExitCodes.Success, exitCode);
        Assert(stdout.ToString().Contains(
            "\"alreadySatisfied\":true",
            StringComparison.Ordinal), "CLI did not return the structured plan.");
        AssertEqual("plan", transport.Requests.Single().Arguments[4]);
    }

    private static void CatalogRunCliDelegatesExecution()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": true,
                  "generation": 8,
                  "finalNextAction": "status"
                }
                """));
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using AgentObservabilityStore observabilityStore = new();

            int exitCode = CliApplication.RunAsync(
                    ["run", "assembler-smoke", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    CreateAdapter(transport),
                    observabilityStore: observabilityStore)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            Assert(stdout.ToString().Contains(
                "\"test\":\"assembler-smoke\"",
                StringComparison.Ordinal), "Catalog test id was not reported.");
            Assert(stdout.ToString().Contains(
                "\"schemaVersion\":\"rimtest-result/v1\"",
                StringComparison.Ordinal), "Catalog run result schema was not reported.");
            Assert(!stdout.ToString().Contains(
                "\"recipe\"",
                StringComparison.Ordinal), "Catalog run should not copy recipe payload data.");
            AssertEqual("run", transport.Requests.Single().Arguments[4]);
            AssertEqual("assembler-fixture", transport.Requests.Single().Arguments[5]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CatalogRunAcquiresAndPropagatesLease()
    {
        var transport = new FakeTransport((request, _) =>
        {
            if (request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
            {
                return ProcessResult(
                    "{\"schemaVersion\":\"devbridge-test-lease/v1\",\"success\":true,\"exitCode\":0,\"generation\":8,\"leaseId\":\"lease-direct\"}");
            }

            if (request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
            {
                return ProcessResult(
                    "{\"schemaVersion\":\"devbridge-test-lease/v1\",\"success\":true,\"exitCode\":0,\"generation\":8,\"leaseId\":\"lease-direct\"}");
            }

            if (request.Arguments.Contains("run", StringComparer.OrdinalIgnoreCase) &&
                !request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase))
            {
                return ProcessResult(
                    "{\"schemaVersion\":\"devbridge-test-recipe-run/v1\",\"recipe\":\"assembler-fixture\",\"success\":false,\"errorCode\":\"RIMBRIDGE_LEASE_REQUIRED\",\"error\":\"lease required\",\"generation\":null,\"runId\":null,\"leaseId\":null,\"evidence\":null,\"evidenceId\":null,\"failureFingerprint\":null,\"finalNextAction\":null,\"restartRequired\":false,\"launchesConsumed\":0,\"workflowId\":null,\"operations\":[]}",
                    exitCode: 4);
            }

            return ProcessResult(
                "{\"schemaVersion\":\"devbridge-test-recipe-run/v1\",\"recipe\":\"assembler-fixture\",\"success\":true,\"errorCode\":null,\"error\":null,\"generation\":8,\"leaseId\":\"lease-direct\",\"runId\":\"run-direct\",\"evidence\":null,\"evidenceId\":null,\"failureFingerprint\":null,\"finalNextAction\":\"status\",\"restartRequired\":false,\"launchesConsumed\":0,\"workflowId\":null,\"operations\":[]}");
        });
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using AgentObservabilityStore observabilityStore = new();

            int exitCode = CliApplication.RunAsync(
                    ["run", "assembler-smoke", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    processTransport: transport,
                    observabilityStore: observabilityStore)
                .GetAwaiter()
                .GetResult();

            Assert(
                exitCode == CliExitCodes.Success,
                $"Expected successful lease recovery; got {exitCode}: {stdout}");
            Assert(stdout.ToString().Contains(
                "\"status\":\"pass\"",
                StringComparison.Ordinal), "Lease-recovered catalog run did not pass.");
            AssertEqual(2, transport.Requests.Count(request =>
                request.Arguments.Contains("run", StringComparer.OrdinalIgnoreCase)));
            AssertEqual(1, transport.Requests.Count(request =>
                request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase)));
            AssertEqual(1, transport.Requests.Count(request =>
                request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase)));
            Assert(transport.Requests.Any(request =>
                    request.Arguments.Contains("run", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("lease-direct", StringComparer.Ordinal)),
                "The retried catalog run must carry the acquired lease.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunResultCategoriesAreCompact()
    {
        CliResult pass = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": true,
                          "runId": "run-8",
                          "generation": 8,
                          "finalNextAction": "status"
                        }
                        """))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Success, pass.ExitCode);
        Assert(pass.Stdout.Contains(
            "\"schemaVersion\":\"rimtest-result/v1\"",
            StringComparison.Ordinal), "Pass schema version is missing.");
        Assert(pass.Stdout.Contains(
            "\"status\":\"pass\"",
            StringComparison.Ordinal), "Pass status is missing.");
        Assert(pass.Stdout.Contains(
            "\"runId\":\"run-8\"",
            StringComparison.Ordinal), "Pass run id was lost.");
        Assert(!pass.Stdout.Contains(
            "\"operations\"",
            StringComparison.Ordinal), "Pass output contains operation telemetry.");
        Assert(!pass.Stdout.Contains(
            "\"recipe\"",
            StringComparison.Ordinal), "Pass output contains redundant recipe data.");

        CliResult failure = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": false,
                          "generation": 8,
                          "failureFingerprint": "fp-8",
                          "evidenceId": "evidence-8",
                          "errorCode": "RECIPE_ASSERTION_FAILED",
                          "operations": [
                            {
                              "tool": "rimworld/fixture",
                              "success": false,
                              "errorCode": "RECIPE_ASSERTION_FAILED",
                              "assertions": [
                                {"pointer": "/value", "success": false}
                              ]
                            }
                          ]
                        }
                        """,
                        exitCode: 4))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.TestFailure, failure.ExitCode);
        Assert(failure.Stdout.Contains(
            "\"status\":\"fail\"",
            StringComparison.Ordinal), "Failure status is missing.");
        Assert(failure.Stdout.Contains(
            "\"failureFingerprint\":\"fp-8\"",
            StringComparison.Ordinal), "Failure fingerprint was lost.");
        Assert(failure.Stdout.Contains(
            "\"evidenceId\":\"evidence-8\"",
            StringComparison.Ordinal), "Evidence id was lost.");
        Assert(!failure.Stdout.Contains(
            "\"operations\"",
            StringComparison.Ordinal), "Failure output contains operation telemetry.");

        CliResult infrastructure = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        "hidden diagnostic",
                        StartError: "DevBridge did not start."))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InternalError, infrastructure.ExitCode);
        Assert(infrastructure.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "Infrastructure status is missing.");
        Assert(!infrastructure.Stdout.Contains(
            "hidden diagnostic",
            StringComparison.Ordinal), "Raw stderr leaked into the result.");

        CliResult refusal = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": false,
                          "errorCode": "TEST_RECIPE_NOT_FOUND",
                          "error": "recipe was refused"
                        }
                        """,
                        exitCode: 4))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InternalError, refusal.ExitCode);
        Assert(refusal.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "DevBridge refusal should be infrastructure output.");

        CliResult cancelled = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        string.Empty,
                        Cancelled: true))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Cancelled, cancelled.ExitCode);
        Assert(cancelled.Stdout.Contains(
            "\"status\":\"cancelled\"",
            StringComparison.Ordinal), "Cancellation status is missing.");

        CatalogDocument invalidCatalog = CreateCatalog();
        invalidCatalog.Tests.Add(new CatalogTest
        {
            Id = "assembler-smoke",
            Recipe = "duplicate"
        });
        CliResult invalid = RunCatalogCliWithAdapter(
            invalidCatalog,
            CreateAdapter(
                new FakeTransport(
                    (_, _) => throw new InvalidOperationException(
                        "execution must not be reached"))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.InvalidInput, invalid.ExitCode);
        Assert(invalid.Stdout.Contains(
            "\"status\":\"invalid\"",
            StringComparison.Ordinal), "Invalid status is missing.");

        CliResult timeout = RunCatalogCliWithAdapter(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => new DevBridgeProcessResult(
                        null,
                        string.Empty,
                        string.Empty,
                        TimedOut: true))),
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Timeout, timeout.ExitCode);
        Assert(timeout.Stdout.Contains(
            "\"status\":\"infrastructure\"",
            StringComparison.Ordinal), "Timeout should be compact infrastructure output.");
    }

    private static void AgentOutputContractsAreGoldenAndBounded()
    {
        string pass = CatalogJsonFacade.Serialize(new RimTestResult
        {
            Status = "pass",
            Test = "assembler-smoke",
            DurationMs = 4821,
            RunId = "run-123"
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-result/v1\",\"status\":\"pass\",\"test\":\"assembler-smoke\",\"durationMs\":4821,\"runId\":\"run-123\"}",
            pass);
        Assert(RimTestOutputBudgets.Utf8Bytes(pass) <=
            RimTestOutputBudgets.SingleTestPassMaxBytes,
            "Single-test pass exceeded its normal output budget.");

        string failure = CatalogJsonFacade.Serialize(new RimTestResult
        {
            Status = "fail",
            Test = "assembler-smoke",
            DurationMs = 4821,
            RunId = "run-123",
            Generation = 7,
            FailureFingerprint = "fp-123",
            EvidenceId = "evidence-123",
            ErrorCode = "RECIPE_ASSERTION_FAILED",
            Diagnostic = new RimTestDiagnosticSummary
            {
                Id = "RE-81F72",
                Category = "runtime",
                Method = "CCM.CompAssembler.Tick",
                Source = "Source/Comps/CompAssembler.cs",
                Line = 131
            }
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-result/v1\",\"status\":\"fail\",\"test\":\"assembler-smoke\",\"durationMs\":4821,\"runId\":\"run-123\",\"generation\":7,\"failureFingerprint\":\"fp-123\",\"evidenceId\":\"evidence-123\",\"errorCode\":\"RECIPE_ASSERTION_FAILED\",\"diagnostic\":{\"id\":\"RE-81F72\",\"category\":\"runtime\",\"method\":\"CCM.CompAssembler.Tick\",\"source\":\"Source/Comps/CompAssembler.cs\",\"line\":131}}",
            failure);
        Assert(RimTestOutputBudgets.Utf8Bytes(failure) <=
            RimTestOutputBudgets.SingleTestFailureMaxBytes,
            "Single-test failure exceeded its normal output budget.");

        string suite = CatalogJsonFacade.Serialize(new RimTestSuiteResult
        {
            Status = "pass",
            Suite = "smoke",
            Passed = 7,
            Failed = 0,
            DurationMs = 18432
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-suite-result/v1\",\"status\":\"pass\",\"suite\":\"smoke\",\"passed\":7,\"failed\":0,\"durationMs\":18432}",
            suite);
        Assert(RimTestOutputBudgets.Utf8Bytes(suite) <=
            RimTestOutputBudgets.SuitePassMaxBytes,
            "Suite pass exceeded its normal output budget.");

        string selection = CatalogJsonFacade.Serialize(new RimTestSelectionResult
        {
            Status = "ok",
            Tests = ["assembler-smoke", "recipe-smoke"],
            ReasonCount = 3
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-selection/v1\",\"status\":\"ok\",\"tests\":[\"assembler-smoke\",\"recipe-smoke\"],\"reasonCount\":3}",
            selection);
        Assert(RimTestOutputBudgets.Utf8Bytes(selection) <=
            RimTestOutputBudgets.AffectedSelectionMaxBytes,
            "Affected selection exceeded its normal output budget.");

        string affectedSuite = CatalogJsonFacade.Serialize(new RimTestSuiteResult
        {
            Status = "pass",
            Suite = "affected",
            Passed = 2,
            Failed = 0,
            DurationMs = 4821
        });
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-suite-result/v1\",\"status\":\"pass\",\"suite\":\"affected\",\"passed\":2,\"failed\":0,\"durationMs\":4821}",
            affectedSuite);
        Assert(RimTestOutputBudgets.Utf8Bytes(affectedSuite) <=
            RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Affected suite pass exceeded its normal output budget.");
    }

    private static void RimErrorDiagnosisIsNormalized()
    {
        string directory = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(directory, "latest.json");
            var store = new JsonFileDiagnosticStore(storePath);
            store.WriteAsync(new DiagnosticStoreSnapshot
            {
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "RE-81F72",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "controlled failure",
                        OriginatingType = "CCM.CompAssembler",
                        OriginatingMethod = "Tick",
                        SourceFile = "Source/Comps/CompAssembler.cs",
                        SourceLine = 131,
                        RunId = "run-7",
                        TestId = "assembler-smoke",
                        OccurrenceCount = 20
                    }
                ]
            }).GetAwaiter().GetResult();

            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = storePath
                });
            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "assembler-smoke",
                        "run-7",
                        7,
                        "evidence-7",
                        "fp-7",
                        "RECIPE_ASSERTION_FAILED"))
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            AssertEqual("RE-81F72", result.Diagnosis!.Id);
            AssertEqual("runtime", result.Diagnosis.Category);
            AssertEqual("CCM.CompAssembler.Tick", result.Diagnosis.Method);
            AssertEqual("Source/Comps/CompAssembler.cs", result.Diagnosis.Source);
            AssertEqual(131, result.Diagnosis.Line);

            var diagnosisAdapter = new FakeRimErrorDiagnosisAdapter(result);
            CliResult cli = RunCatalogCliWithAdapters(
                CreateCatalog(),
                CreateAdapter(
                    new FakeTransport(
                        (_, _) => ProcessResult(
                            """
                            {
                              "schemaVersion": "devbridge-test-recipe-run/v1",
                              "recipe": "assembler-fixture",
                              "success": false,
                              "failureFingerprint": "fp-7",
                              "evidence": "evidence-7",
                              "errorCode": "RECIPE_ASSERTION_FAILED",
                              "operations": [
                                {
                                  "tool": "rimworld/fixture",
                                  "success": false,
                                  "assertions": [
                                    {"pointer": "/value", "success": false}
                                  ]
                                }
                              ]
                            }
                            """,
                            exitCode: 4))),
                diagnosisAdapter,
                "run",
                "assembler-smoke");
            AssertEqual(CliExitCodes.TestFailure, cli.ExitCode);
            Assert(!cli.Stdout.Contains(
                "\"diagnosticStatus\":\"available\"",
                StringComparison.Ordinal), "Available diagnosis should not repeat a redundant status.");
            Assert(cli.Stdout.Contains(
                "\"id\":\"RE-81F72\"",
                StringComparison.Ordinal), "Diagnostic id is missing.");
            Assert(cli.Stdout.Contains(
                "\"source\":\"Source/Comps/CompAssembler.cs\"",
                StringComparison.Ordinal), "Diagnostic source is missing.");
            Assert(!cli.Stdout.Contains(
                "\"symbol\"",
                StringComparison.Ordinal), "Default output should not copy the full RimError summary.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimErrorScopedSourceUsesCore()
    {
        string directory = CreateTempDirectory();
        const string sourceContent = "System.NullReferenceException: controlled failure\n   at Fixture.Test()\n";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(sourceContent);
            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = Path.Combine(directory, "latest.json")
                });
            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "fixture-test",
                        "run-scoped",
                        3,
                        "evidence-scoped",
                        "fingerprint",
                        "RECIPE_ASSERTION_FAILED",
                        ScopedSource: new RimErrorScopedDiagnosticSource(
                            RimErrorSchemas.ScopedDiagnosticSource,
                            3,
                            sourceContent,
                            bytes.Length,
                            1,
                            false,
                            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())))
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            Assert(result.Diagnosis is not null, "The in-memory scoped source was not diagnosed.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DevBridgeDiagnosticSourceIsBoundedAndGenerationScoped()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 1,
                  "sinceLaunch": true,
                  "available": true,
                  "rawBytes": 4096,
                  "semanticBytes": 128,
                  "truncated": false,
                  "records": [
                    {
                      "sequence": 1,
                      "generation": 1,
                      "sinceLaunch": true,
                      "severity": "ERROR",
                      "component": "RimWorld",
                      "message": "controlled fixture failure",
                      "stackFrames": ["at Fixture.Test()"]
                    }
                  ]
                }
                """));
        var adapter = new DevBridgeDiagnosticSourceAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeDiagnosticSourceResult result = adapter.AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();

        Assert(result.Status.IsAvailable, "The bounded source should be available.");
        Assert(result.Source is not null, "The source payload is missing.");
        AssertEqual(1, result.Source!.Generation);
        Assert(result.Source.Content.Contains("controlled fixture failure", StringComparison.Ordinal),
            "The semantic log message was not projected.");
        Assert(result.Source.Content.Contains("at Fixture.Test()", StringComparison.Ordinal),
            "The semantic stack frame was not projected.");
        Assert(result.Source.SourceBytes <= 64 * 1024, "The source exceeded its bound.");
        AssertEqual(64, result.Source.Sha256.Length);
        Assert(transport.Requests[0].Arguments.Contains("--since-launch"),
            "The query was not launch scoped.");
        Assert(transport.Requests[0].Arguments.Contains("--generation") &&
            transport.Requests[0].Arguments.Contains("1"),
            "The query was not generation scoped.");

        var staleTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 2,
                  "sinceLaunch": true,
                  "available": true,
                  "truncated": false,
                  "records": []
                }
                """));
        DevBridgeDiagnosticSourceResult stale = new DevBridgeDiagnosticSourceAdapter(
                staleTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        AssertEqual(
            "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH",
            stale.Status.ErrorCode);
        Assert(stale.Source is null, "A stale source must not be handed to RimError.");

        var missingTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-logs-query/v1",
                  "contract": "devbridge-logs-query/v1",
                  "success": true,
                  "generation": 1,
                  "sinceLaunch": true,
                  "available": false,
                  "truncated": false,
                  "records": [],
                  "errorCode": "PLAYER_LOG_UNAVAILABLE"
                }
                """));
        DevBridgeDiagnosticSourceResult missing = new DevBridgeDiagnosticSourceAdapter(
                missingTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        AssertEqual("PLAYER_LOG_UNAVAILABLE", missing.Status.ErrorCode);

        string[] records = Enumerable.Range(1, 64)
            .Select(index =>
                $$"""{"sequence":{{index}},"generation":1,"sinceLaunch":true,"severity":"ERROR","component":"RimWorld","message":"{{new string('x', 2048)}}"}""")
            .ToArray();
        string oversizedJson =
            $$"""{"schemaVersion":"devbridge-logs-query/v1","contract":"devbridge-logs-query/v1","success":true,"generation":1,"sinceLaunch":true,"available":true,"truncated":false,"records":[{{string.Join(',', records)}}]}""";
        var oversizedTransport = new FakeTransport((_, _) => ProcessResult(oversizedJson));
        DevBridgeDiagnosticSourceResult oversized = new DevBridgeDiagnosticSourceAdapter(
                oversizedTransport,
                new DevBridgeAdapterOptions
                {
                    CommandPath = "DevBridge.cmd",
                    RootPath = "DevBridgeRoot",
                    ShowPlanTimeout = TimeSpan.FromSeconds(1)
                })
            .AcquireAsync(
                "assembler-smoke",
                FailedRun("assembler-fixture", "fp-source", "RECIPE_ASSERTION_FAILED"))
            .GetAwaiter()
            .GetResult();
        Assert(oversized.Source is not null, "The bounded source should remain usable.");
        Assert(oversized.Source!.Truncated, "Oversized semantic evidence was not marked truncated.");
        Assert(oversized.Source.SourceBytes <= 64 * 1024,
            "Oversized semantic evidence exceeded the source bound.");
    }

    private static void AutomaticDiagnosticsCarryScopedIdentities()
    {
        const string workflowId = "workflow-diagnostic-1";
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-scoped",
            "RECIPE_ASSERTION_FAILED",
            generation: 7,
            workflowId: workflowId,
            operations: [new DevBridgeOperationSummary(
                "rimworld/fixture",
                false,
                "RECIPE_ASSERTION_FAILED",
                ["/value"],
                "operation-diagnostic-1",
                workflowId,
                7,
                "launch-diagnostic-1")]);
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("RE-scoped"));
        var source = new FakeDiagnosticSourceAdapter(AvailableSource(7));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp(),
                workflowId: workflowId)
            .GetAwaiter()
            .GetResult();

        AssertEqual("fail", execution.Result.Status);
        AssertEqual("RE-scoped", execution.Result.Diagnostic!.Id);
        AssertEqual(1, source.Calls);
        AssertEqual(1, diagnosis.Calls);
        RimErrorDiagnosisRequest request = diagnosis.Request!;
        AssertEqual("workflow-diagnostic-1", request.WorkflowId);
        AssertEqual("run-assembler-fixture", request.RunId);
        AssertEqual(7, request.Generation);
        AssertEqual("operation-diagnostic-1", request.Operations![0].OperationId);
        AssertEqual("rimtest-devbridge-diagnostic-source/v1", request.ScopedSource!.SchemaVersion);
        AssertEqual(7, request.ScopedSource.Generation);
    }

    private static void NormalCliFailureAcquiresDiagnosticsAutomatically()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var recipe = new FakeRecipeAdapter();
            recipe.Runs["assembler-fixture"] = FailedRun(
                "assembler-fixture",
                "fp-cli-auto",
                "RECIPE_ASSERTION_FAILED");
            var transport = new FakeTransport(
                (request, _) => request.Arguments[0] switch
                {
                    "logs" => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-logs-query/v1",
                          "contract": "devbridge-logs-query/v1",
                          "success": true,
                          "generation": 1,
                          "sinceLaunch": true,
                          "available": true,
                          "rawBytes": 64,
                          "semanticBytes": 64,
                          "truncated": false,
                          "records": [
                            {
                              "sequence": 1,
                              "generation": 1,
                              "sinceLaunch": true,
                              "severity": "ERROR",
                              "component": "RimWorld",
                              "message": "System.NullReferenceException: controlled CLI failure",
                              "stackFrames": ["at Fixture.Test()"]
                            }
                          ]
                        }
                        """),
                    _ => throw new InvalidOperationException(
                        "Unexpected process in automatic diagnostic test: " +
                        request.Arguments[0])
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        [
                            "run",
                            "assembler-smoke",
                            "--json",
                            "--catalog",
                            catalogPath
                        ],
                        stdout,
                        stderr,
                        recipe,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement result = document.RootElement;
            AssertEqual(CliExitCodes.TestFailure, exitCode);
            AssertEqual("fail", result.GetProperty("status").GetString());
            Assert(result.GetProperty("diagnostic").GetProperty("id").GetString() is { Length: > 0 },
                "The direct RimError.Core diagnosis did not produce a bounded diagnostic id.");
            Assert(transport.Requests.Any(request => request.Arguments[0] == "logs"),
                "The normal CLI did not acquire a DevBridge diagnostic source.");
            Assert(!transport.Requests.Any(request => request.Arguments[0] is "ingest" or "latest"),
                "The normal CLI crossed the obsolete RimError CLI boundary.");
            Assert(!stdout.ToString().Contains("Player.log", StringComparison.Ordinal),
                "The normal failure result exposed a Player.log path.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuccessfulTestSkipsDiagnosticAcquisition()
    {
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("unused"));
        var source = new FakeDiagnosticSourceAdapter(AvailableSource(7));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp())
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", execution.Result.Status);
        AssertEqual(0, source.Calls);
        AssertEqual(0, diagnosis.Calls);
    }

    private static void StaleDiagnosticSourceCannotProduceTrustworthyResult()
    {
        var recipe = new FakeRecipeAdapter();
        recipe.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-stale",
            "RECIPE_ASSERTION_FAILED");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("must-not-use"));
        var source = new FakeDiagnosticSourceAdapter(
            new DevBridgeDiagnosticSourceResult(
                new DevBridgeDiagnosticSourceStatus(
                    DevBridgeDiagnosticSourceOutcome.Unavailable,
                    "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH"),
                null));
        var service = new CatalogTestExecutionService(
            recipe,
            () => diagnosis,
            () => source);

        CatalogTestExecutionResult execution = service.RunAsync(
                CreateCatalog(),
                "assembler-smoke",
                System.Diagnostics.Stopwatch.GetTimestamp())
            .GetAwaiter()
            .GetResult();

        AssertEqual("fail", execution.Result.Status);
        AssertEqual("unavailable", execution.Result.DiagnosticStatus);
        AssertEqual(
            "DEVBRIDGE_DIAGNOSTIC_GENERATION_MISMATCH",
            execution.Result.DiagnosticErrorCode);
        AssertEqual(0, diagnosis.Calls);
    }

    private static void ScopedRimErrorDiagnosisFiltersNearbyRuns()
    {
        string directory = CreateTempDirectory();
        try
        {
            string storePath = Path.Combine(directory, "latest.json");
            new JsonFileDiagnosticStore(storePath).WriteAsync(new DiagnosticStoreSnapshot
            {
                Items =
                [
                    new DiagnosticRecord
                    {
                        Id = "RE-other",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "other run",
                        RunId = "run-other"
                    },
                    new DiagnosticRecord
                    {
                        Id = "RE-current",
                        Severity = DiagnosticSeverity.Error,
                        Category = "runtime",
                        Message = "current run",
                        RunId = "run-current"
                    }
                ]
            }).GetAwaiter().GetResult();
            var adapter = new RimErrorDiagnosisAdapter(
                new RimErrorAdapterOptions
                {
                    CommandPath = "rimerror-compat",
                    WorkingDirectory = directory,
                    StorePath = storePath
                });

            RimErrorDiagnosisResult result = adapter.DiagnoseAsync(
                    new RimErrorDiagnosisRequest(
                        "assembler-smoke",
                        "run-current",
                        7,
                        "evidence-current",
                        "fp-current",
                        "RECIPE_ASSERTION_FAILED"))
                .GetAwaiter()
                .GetResult();
            AssertEqual(RimErrorDiagnosisOutcome.Available, result.Outcome);
            AssertEqual("RE-current", result.Diagnosis!.Id);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DiagnosticFailurePreservesTestFailure()
    {
        var recipeTransport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "assembler-fixture",
                  "success": false,
                  "failureFingerprint": "fp-preserved",
                  "evidence": "evidence-preserved",
                  "errorCode": "RECIPE_ASSERTION_FAILED",
                  "operations": [
                    {
                      "tool": "rimworld/fixture",
                      "success": false,
                      "assertions": [
                        {"pointer": "/value", "success": false}
                      ]
                    }
                  ]
                }
                """,
                exitCode: 4));
        var diagnosis = new FakeRimErrorDiagnosisAdapter(
            new RimErrorDiagnosisResult(
                RimErrorDiagnosisOutcome.Timeout,
                new RimErrorAdapterStatus(
                    RimErrorDiagnosisOutcome.Timeout,
                    "RIMERROR_TIMEOUT",
                    "timeout"),
                null,
                null));

        CliResult result = RunCatalogCliWithAdapters(
            CreateCatalog(),
            CreateAdapter(recipeTransport),
            diagnosis,
            "run",
            "assembler-smoke");

        AssertEqual(CliExitCodes.TestFailure, result.ExitCode);
        Assert(result.Stdout.Contains(
            "\"status\":\"fail\"",
            StringComparison.Ordinal), "Test failure status changed.");
        Assert(result.Stdout.Contains(
            "\"failureFingerprint\":\"fp-preserved\"",
            StringComparison.Ordinal), "Test failure fingerprint was lost.");
        Assert(result.Stdout.Contains(
            "\"diagnosticStatus\":\"unavailable\"",
            StringComparison.Ordinal), "Degraded diagnostic status is missing.");
        AssertEqual(1, diagnosis.Calls);
        AssertEqual("fp-preserved", diagnosis.Request!.FailureFingerprint);

        var passDiagnosis = new FakeRimErrorDiagnosisAdapter(diagnosis.Result);
        CliResult pass = RunCatalogCliWithAdapters(
            CreateCatalog(),
            CreateAdapter(
                new FakeTransport(
                    (_, _) => ProcessResult(
                        """
                        {
                          "schemaVersion": "devbridge-test-recipe-run/v1",
                          "recipe": "assembler-fixture",
                          "success": true
                        }
                        """))),
            passDiagnosis,
            "run",
            "assembler-smoke");
        AssertEqual(CliExitCodes.Success, pass.ExitCode);
        Assert(pass.Stdout.Contains(
            "\"status\":\"pass\"",
            StringComparison.Ordinal), "RimError must not alter a passing test.");
        AssertEqual(0, passDiagnosis.Calls);
    }

    private static void SuccessfulRecipeRunIsNormalized()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "fixture",
                  "success": true,
                  "generation": 7,
                  "runId": "run-7",
                  "evidence": "Runtime/readiness.json",
                  "evidenceId": "evidence-7",
                  "failureFingerprint": null,
                  "finalNextAction": "status",
                  "restartRequired": false,
                  "launchesConsumed": 0,
                  "operations": null
                }
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Success, result.Status.Outcome);
        Assert(result.Passed == true, "Run should pass.");
        AssertEqual("run-7", result.RunId);
        AssertEqual(7, result.Generation);
        AssertEqual("evidence-7", result.EvidenceId);
        AssertEqual("Runtime/readiness.json", result.Evidence);
    }

    private static void RecipeAssertionFailureIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-run/v1",
                  "recipe": "fixture",
                  "success": false,
                  "generation": 7,
                  "failureFingerprint": "fp-7",
                  "finalNextAction": "inspect-evidence",
                  "errorCode": "RECIPE_ASSERTION_FAILED",
                  "error": "assertion failed",
                  "operations": [
                    {
                      "tool": "rimworld/fixture",
                      "success": false,
                      "errorCode": "RECIPE_ASSERTION_FAILED",
                      "assertions": [
                        {"pointer": "/value", "success": false}
                      ]
                    }
                  ]
                }
                """,
                exitCode: 4));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.TestFailure, result.Status.Outcome);
        Assert(result.Passed == false, "Failed recipe should not pass.");
        AssertEqual("fp-7", result.FailureFingerprint);
        AssertEqual("/value", result.Operations.Single().FailedAssertionPointers.Single());
    }

    private static void DevBridgeRefusalIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {
                  "schemaVersion": "devbridge-test-recipe-plan/v1",
                  "recipe": "missing",
                  "alreadySatisfied": false,
                  "estimatedRimWorldLaunches": 0,
                  "steps": [],
                  "nextAction": "inspect-evidence",
                  "blockedBy": ["TEST_RECIPE_NOT_FOUND"],
                  "errorCode": "TEST_RECIPE_NOT_FOUND",
                  "error": "not found"
                }
                """,
                exitCode: 4));

        DevBridgeRecipePlanResult result = CreateAdapter(transport)
            .PlanAsync("missing")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.DevBridgeRefusal, result.Status.Outcome);
        AssertEqual("TEST_RECIPE_NOT_FOUND", result.Status.ErrorCode);
        Assert(result.Plan is null, "Refused plan must not be treated as executable.");
    }

    private static void InfrastructureFailureIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                "cannot start",
                StartError: "DevBridge command was not found."));

        DevBridgeRecipeShowResult result = CreateAdapter(transport)
            .ShowAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.InfrastructureFailure, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_START_FAILED", result.Status.ErrorCode);
    }

    private static void TimeoutIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => new DevBridgeProcessResult(
                null,
                string.Empty,
                string.Empty,
                TimedOut: true));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Timeout, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_CLIENT_TIMEOUT", result.Status.ErrorCode);
    }

    private static void CancellationIsClassified()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new FakeTransport(
            (_, token) =>
            {
                Assert(token.IsCancellationRequested, "Cancellation was not forwarded.");
                return new DevBridgeProcessResult(
                    null,
                    string.Empty,
                    string.Empty,
                    Cancelled: true);
            });

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture", cancellation.Token)
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.Cancelled, result.Status.Outcome);
    }

    private static void MalformedResponseIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult("{"));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.MalformedResponse, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_RESPONSE_INVALID", result.Status.ErrorCode);
    }

    private static void IncompatibleSchemaIsClassified()
    {
        var transport = new FakeTransport(
            (_, _) => ProcessResult(
                """
                {"schemaVersion":"devbridge-test-recipe-run/v2"}
                """));

        DevBridgeRecipeRunResult result = CreateAdapter(transport)
            .RunAsync("fixture")
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.IncompatibleSchema, result.Status.Outcome);
        AssertEqual("DEVBRIDGE_SCHEMA_UNSUPPORTED", result.Status.ErrorCode);
    }

    private static void RimContextDirectCoverageSelectsTest()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    "Defs/Assembler.xml",
                    12,
                    "changed_file",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Defs/Assembler.xml"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("ok", result.Status);
        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextTransitiveCoverageSelectsTest()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "dependent",
                    "csharp_type",
                    "type-assembler",
                    "CompAssembler",
                    "Source/CompAssembler.cs",
                    44,
                    "csharp_type_usage",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Base.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("ok", result.Status);
        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextSharedCoverageIsDeduplicated()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null),
                new RimContextImpact(
                    "dependent",
                    "csharp_type",
                    "type-assembler",
                    "CompAssembler",
                    null,
                    null,
                    null,
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Assembler.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertSequence(["assembler-smoke"], result.Tests);
        AssertEqual(2, result.ReasonCount);
    }

    private static void RimContextNoImpactSelectsNoTests()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact()));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Isolated.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("conservative", result.Status);
        AssertEqual(0, result.Tests.Count);
        AssertEqual("RIMCONTEXT_NO_TESTS", result.ErrorCode);
        AssertEqual("rimliaison affected --run --fallback-suite <suite>", result.NextAction);
        AssertEqual(1, result.ReasonCount);
    }

    private static void RimContextUnknownImpactUsesFallback()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unknown,
                    "RIMCONTEXT_RESULT_TRUNCATED"),
                [],
                [],
                true)));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Unknown.cs"],
                "smoke",
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("conservative", result.Status);
        AssertEqual("smoke", result.FallbackSuite);
        AssertSequence(["assembler-smoke", "settings-smoke"], result.Tests);
    }

    private static void RimContextUnavailableIsConservative()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unavailable,
                    "INDEX_NOT_FOUND"),
                [],
                [],
                false)));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Unknown.cs"],
                null,
                false)
            .GetAwaiter()
            .GetResult();

        AssertEqual("blocked", result.Status);
        AssertEqual("CONTEXT_STALE", result.ErrorCode);
        AssertEqual("rimliaison affected --run --json", result.NextAction);
        AssertEqual(0, result.Tests.Count);
        Assert(result.ReasonCount > 0, "Conservative selection needs a reason.");
    }

    private static void RimContextSelectionOrderingIsDeterministic()
    {
        var selector = new RimContextTestSelector(
            new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "dependent",
                    "feature",
                    "feature-settings",
                    "settings",
                    null,
                    null,
                    "feature",
                    null),
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    "changed_file",
                    null))));

        RimTestSelectionResult result = selector.SelectAsync(
                CreateCatalog(),
                ["Source/Mixed.cs"],
                null,
                true)
            .GetAwaiter()
            .GetResult();

        AssertSequence(["assembler-smoke", "settings-smoke"], result.Tests);
        Assert(result.Reasons is not null, "Explain should return reasons.");
        AssertEqual("direct", result.Reasons![0].Tier);
        AssertEqual("dependent", result.Reasons[1].Tier);
    }

    private static void RimContextAdapterUsesCore()
    {
        string directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "Source.cs"),
                "namespace Fixture; public class Changed { public int Value; }\n");
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    StorePath = Path.Combine(directory, ".rimctx", "index.sqlite"),
                    Depth = 8,
                    Limit = 100
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual("Source.cs", result.Changed.Single());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextCompleteIndexStaysReady()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (request, _) =>
                {
                    calls++;
                    Assert(!request.Force, "A complete index must not trigger a forced rebuild.");
                    return CompleteContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual(PrerequisiteRecoveryState.Ready, result.Status.RecoveryState);
            AssertEqual(0, result.Status.RecoveryAttempts);
            AssertEqual(1, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextPartialIndexIsRecoveredOnce()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (request, _) =>
                {
                    calls++;
                    return request.Force
                        ? CompleteContextAnalysis(directory)
                        : PartialContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Success, result.Status.Outcome);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            AssertEqual(2, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RimContextFailedRecoveryPreservesDiagnostics()
    {
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var adapter = new RimContextImpactAdapter(
                new RimContextAdapterOptions
                {
                    RootPath = directory,
                    Depth = 8,
                    Limit = 100
                },
                refreshAndAffected: (_, _) =>
                {
                    calls++;
                    return PartialContextAnalysis(directory);
                });

            RimContextImpactResult result = adapter.AffectedAsync(["Source.cs"])
                .GetAwaiter()
                .GetResult();

            AssertEqual(RimContextImpactOutcome.Unknown, result.Status.Outcome);
            AssertEqual("RIMCONTEXT_INDEX_RECOVERY_FAILED", result.Status.ErrorCode);
            AssertEqual(PrerequisiteRecoveryState.RecoveryFailed, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            Assert(result.Status.Error?.Contains("BROKEN_SOURCE", StringComparison.Ordinal) == true,
                "A failed rebuild must retain bounded index diagnostics.");
            AssertEqual(2, calls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static RimContextAffectedAnalysis CompleteContextAnalysis(string root) =>
        new(
            new IndexBuildResult(
                new StoreMetadata(1, "test", "fixture", root, "fingerprint", "now"),
                new IndexCounts(1, 0, 0),
                1,
                new IndexStatistics(1, 1, 0, 0, 0),
                1,
                []),
            new AffectedResult([], [], [], [], false));

    private static RimContextAffectedAnalysis PartialContextAnalysis(string root) =>
        new(
            new IndexBuildResult(
                new StoreMetadata(1, "test", "fixture", root, "fingerprint", "now"),
                new IndexCounts(1, 0, 0),
                1,
                new IndexStatistics(1, 1, 0, 0, 0),
                1,
                [new IndexDiagnostic("Source.cs", "source parse failed", "BROKEN_SOURCE")]),
            null);

    private static void RimContextCancellationIsBounded()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new RimContextImpactAdapter(
            new RimContextAdapterOptions
            {
                RootPath = "Workspace",
                Depth = 8,
                Limit = 100
            });
        RimContextImpactResult result = adapter.AffectedAsync(
                ["Source/Base.cs"],
                cancellation.Token)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RimContextImpactOutcome.Cancelled, result.Status.Outcome);
    }

    private static void AffectedCliEmitsCompactSelection()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Defs/Assembler.xml",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(
                "{\"schemaVersion\":\"rimtest-selection/v1\",\"status\":\"ok\",\"tests\":[\"assembler-smoke\"],\"reasonCount\":1}",
                stdout.ToString().Trim());
            Assert(!stdout.ToString().Contains(
                "Defs/Assembler.xml",
                StringComparison.Ordinal), "Normal selection output should omit impact details.");
            Assert(string.IsNullOrEmpty(stderr.ToString()), "Selection should not write diagnostics.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RimContextImpactResult SuccessfulImpact(
        params RimContextImpact[] impacts) =>
        new(
            new RimContextAdapterStatus(RimContextImpactOutcome.Success),
            ["changed.cs"],
            impacts,
            false);

    private static void AffectedRunPassIsCompact()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Defs/Assembler.xml",
                        "--run",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual(1, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            JsonElement orchestration = root.GetProperty("orchestration");
            AssertEqual("rimtest-orchestration/v1",
                orchestration.GetProperty("schemaVersion").GetString());
            AssertEqual("PASS", orchestration.GetProperty("overall").GetString());
            AssertEqual("NOT_RUN", orchestration.GetProperty("sourceBuild").GetString());
            AssertEqual("PASS", orchestration.GetProperty("staticTests").GetString());
            AssertEqual("NOT_EVALUATED", orchestration.GetProperty("deployment").GetString());
            AssertEqual("PASS", orchestration.GetProperty("runtimeValidation").GetString());
            AssertEqual("READY", orchestration.GetProperty("infrastructure").GetString());
            Assert(!root.TryGetProperty("selectionStatus", out _),
                "Known-safe affected runs should omit redundant selection status.");
            Assert(!stdout.ToString().Contains("operations", StringComparison.Ordinal),
                "Affected pass should not emit child telemetry.");
            Assert(RimTestOutputBudgets.Utf8Bytes(stdout.ToString()) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
                "Affected pass exceeded its normal output budget.");
            Assert(string.IsNullOrEmpty(stderr.ToString()),
                "Affected pass should not write a human transcript.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedZeroImpactUsesFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Isolated.cs"),
                "class Isolated {}\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Isolated.cs",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("RIMCONTEXT_NO_TESTS", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void AffectedChangedPathWithoutFallbackBlocks()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Isolated.cs"),
                "class Isolated {}\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());

            CliResult result = WithFallbackSuiteEnvironment(
                null,
                () => WithCurrentDirectory(
                    directory,
                    () =>
                    {
                        var stdout = new StringWriter();
                        var stderr = new StringWriter();
                        int exitCode = CliApplication.RunAsync(
                                [
                                    "affected",
                                    "Source/Isolated.cs",
                                    "--run",
                                    "--json",
                                    "--catalog",
                                    catalogPath
                                ],
                                stdout,
                                stderr,
                                adapter,
                                impactAdapter: impactAdapter)
                            .GetAwaiter()
                            .GetResult();
                        return new CliResult(
                            exitCode,
                            stdout.ToString(),
                            stderr.ToString());
                    }));

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
            AssertEqual("conservative", root.GetProperty("status").GetString());
            AssertEqual("RIMCONTEXT_NO_TESTS", root.GetProperty("errorCode").GetString());
            AssertEqual(
                "rimliaison affected --run --fallback-suite <suite>",
                root.GetProperty("nextAction").GetString());
            Assert(!root.TryGetProperty("suite", out _),
                "An affected run without fallback must not execute an empty suite.");
            AssertEqual(0, adapter.RunCalls.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuiteAllPassAggregationIsCompact()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var executor = new CatalogTestExecutionService(adapter);
        var runner = new CatalogSuiteRunner(adapter, executor);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["settings-smoke", "assembler-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 18432);
        string output = CatalogJsonFacade.Serialize(result);

        AssertEqual("pass", result.Status);
        AssertEqual(2, result.Passed);
        AssertEqual(0, result.Failed);
        Assert(!output.Contains("operations", StringComparison.Ordinal),
            "Suite output must not contain child transcripts.");
        Assert(!output.Contains("assembler-fixture", StringComparison.Ordinal),
            "Suite output must not contain child recipe payloads.");
        Assert(!output.Contains("failures", StringComparison.Ordinal),
            "Successful suite must omit failures.");
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.PlanCalls);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
    }

    private static void EmptySuiteExecutionIsConservative()
    {
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            new CatalogSuiteExecutionResult("empty", [], 0, false),
            100,
            selectionStatus: "ok");

        AssertEqual("conservative", result.Status);
        AssertEqual(0, result.Passed);
        AssertEqual(0, result.Failed);
        AssertEqual("conservative", result.SelectionStatus);
        AssertEqual("RIMTEST_EMPTY_EXECUTION", result.SelectionErrorCode);
        AssertEqual("rimliaison suites", result.NextAction);
        Assert(!string.Equals(result.Status, "pass", StringComparison.Ordinal),
            "An empty suite execution must never be a normal pass.");
    }

    private static void SuiteOneFailureIsSummarized()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var diagnosis = new FakeRimErrorDiagnosisAdapter(
            AvailableDiagnosis("RE-81F72"));
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter, () => diagnosis));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("fail", result.Status);
        AssertEqual(1, result.Passed);
        AssertEqual(1, result.Failed);
        AssertEqual(1, result.Failures!.Count);
        AssertEqual("assembler-smoke", result.Failures[0].Test);
        AssertEqual("RE-81F72", result.Failures[0].DiagnosticId);
        AssertEqual("fp-assembler", result.Failures[0].FailureFingerprint);
    }

    private static void SuiteMultipleFailuresAreDeterministic()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = FailedRun(
            "settings-fixture",
            "fp-settings",
            "RECIPE_ASSERTION_FAILED");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["settings-smoke", "assembler-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("fail", result.Status);
        AssertEqual(0, result.Passed);
        AssertEqual(2, result.Failed);
        AssertSequence(
            ["assembler-smoke", "settings-smoke"],
            result.Failures!.Select(static failure => failure.Test).ToArray());
        AssertSequence(
            ["assembler-fixture", "settings-fixture"],
            adapter.RunCalls);
        Assert(execution.FailFast is null,
            "The default suite mode must not add fail-fast execution metadata.");
    }

    private static void SuiteFailFastStopsAfterFirstFailure()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-assembler",
            "RECIPE_ASSERTION_FAILED");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "smoke",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);
        string json = CatalogJsonFacade.Serialize(result);

        AssertEqual("fail", result.Status);
        AssertEqual(1, result.Failed);
        AssertEqual(2, result.Skipped);
        AssertSequence(["assembler-fixture"], adapter.RunCalls);
        AssertEqual("assembler-smoke", result.FailFast!.FirstFailure);
        AssertEqual(2, result.FailFast.NotLaunched);
        Assert(!result.FailFast.ValidationCompleted,
            "A stopped failure path must report incomplete validation.");
        Assert(!json.Contains("third-fixture", StringComparison.Ordinal),
            "Fail-fast output must not include unlaunched recipe payloads.");
        Assert(!json.Contains("operations", StringComparison.Ordinal),
            "Fail-fast output must not include child transcripts.");
        Assert(RimTestOutputBudgets.Utf8Bytes(json) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Fail-fast output exceeded the bounded suite budget.");
    }

    private static void SuiteFailFastPassExecutesEverySelectedTest()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "smoke",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);
        string json = CatalogJsonFacade.Serialize(result);

        AssertEqual("pass", result.Status);
        AssertEqual(3, result.Passed);
        AssertSequence(
            ["assembler-fixture", "settings-fixture", "third-fixture"],
            adapter.RunCalls);
        Assert(result.FailFast!.FirstFailure is null,
            "A passing fail-fast run must not invent a failure reference.");
        AssertEqual(0, result.FailFast.NotLaunched);
        Assert(result.FailFast.ValidationCompleted,
            "A fail-fast PASS must prove complete selected-test execution.");
        RimTestSuiteResult partial = RimTestSuiteResultFactory.FromExecution(
            execution with
            {
                FailFast = new CatalogSuiteFailFastSummary(null, 1, false)
            },
            100);
        AssertEqual("conservative", partial.Status);
        Assert(RimTestOutputBudgets.Utf8Bytes(json) <=
                RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Passing fail-fast output exceeded the bounded suite budget.");
    }

    private static void SuiteCancellationStopsNewChildren()
    {
        CatalogDocument catalog = CreateCatalog();
        catalog.Tests.Add(new CatalogTest
        {
            Id = "third-smoke",
            Recipe = "third-fixture"
        });
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = CancelledRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        adapter.Runs["third-fixture"] = PassRun("third-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "affected",
                ["third-smoke", "assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("cancelled", result.Status);
        AssertEqual(1, result.Cancelled!.Value);
        AssertEqual(2, result.Skipped!.Value);
        AssertSequence(["assembler-fixture"], adapter.RunCalls);
        Assert(result.FailFast is not null &&
                result.FailFast.FirstFailure is null &&
                !result.FailFast.ValidationCompleted,
            "Cancellation must remain conservative in fail-fast mode.");
    }

    private static void SuiteDuplicateTestsExecuteOnce()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "affected",
                ["settings-smoke", "assembler-smoke", "assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, execution.Tests.Count);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
    }

    private static void SuiteChildInfrastructureFailureIsSummarized()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = InfrastructureRun(
            "assembler-fixture",
            "DEVBRIDGE_CLIENT_TIMEOUT");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"],
                failFast: true)
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);

        AssertEqual("infrastructure", result.Status);
        AssertEqual(1, result.Passed);
        AssertEqual(0, result.Failed);
        AssertEqual(1, result.InfrastructureFailureCount);
        AssertEqual("infrastructure", result.Failures![0].Status);
        AssertEqual("DEVBRIDGE_CLIENT_TIMEOUT", result.Failures[0].ErrorCode);
        AssertSequence(["assembler-fixture", "settings-fixture"], adapter.RunCalls);
        Assert(result.FailFast is not null &&
                result.FailFast.FirstFailure is null &&
                result.FailFast.ValidationCompleted,
            "Infrastructure failures must not be treated as ordinary fail-fast failures.");
    }

    private static void SuitePlanRefusalBlocksExecution()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Plans["assembler-fixture"] = new DevBridgeRecipePlanResult(
            "assembler-fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.DevBridgeRefusal,
                "TEST_RECIPE_NOT_FOUND"),
            null);
        adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
        adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 100);
        AssertEqual("infrastructure", result.Status);
        AssertEqual(0, result.Failed);
        AssertEqual(1, result.InfrastructureFailureCount);
        AssertEqual(1, result.Skipped);
        AssertEqual(0, adapter.RunCalls.Count);
        AssertEqual("TEST_RECIPE_NOT_FOUND", result.Failures![0].ErrorCode);
    }

    private static void SharedBuildPrerequisiteBlocksEverySelectedTest()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Plans["assembler-fixture"] = new DevBridgeRecipePlanResult(
            "assembler-fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "DEVELOPMENT_BUILD_FAILED"),
            null);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter));

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                CreateCatalog(),
                "smoke",
                ["assembler-smoke", "settings-smoke"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(
            execution,
            100,
            selectedTestIds: ["assembler-smoke", "settings-smoke"]);

        AssertEqual(2, result.SelectedTestCount);
        AssertEqual(0, result.ExecutedTestCount);
        AssertEqual(2, result.BlockedTestCount);
        AssertEqual(0, result.FailedTestCount);
        AssertEqual(0, result.Failed);
        AssertEqual("infrastructure", result.Status);
        Assert(result.Failures is null,
            "A shared prerequisite must not create per-test failure records.");
        AssertSequence(["assembler-smoke", "settings-smoke"], result.SelectedTests!);
        AssertSequence(["assembler-smoke", "settings-smoke"],
            result.BlockedTests!.Select(static blocked => blocked.Test).ToArray());
        Assert(result.BlockedTests!.All(static blocked =>
                blocked.CausalFailureId == "shared-prerequisite:DEVELOPMENT_BUILD_FAILED"),
            "All blocked tests must link to one prerequisite failure.");
        AssertEqual(0, adapter.RunCalls.Count);
    }

    private static void UnannotatedRecipesUseSafePath()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest { Id = "unknown-a", Recipe = "recipe-a" },
            new CatalogTest { Id = "unknown-b", Recipe = "recipe-b" });
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, 7) with { WorkflowId = workflow }
        };
        var lease = new FakeLeaseAdapter();
        var fresh = new FakeFreshGenerationAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            resetAdapter: null,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "unknown",
                ["unknown-a", "unknown-b"],
                workflowId: "workflow-unknown")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        AssertEqual(0, fresh.Calls.Count);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Unannotated recipes must not receive a reusable lease.");
        Assert(execution.Reuse is null,
            "An unannotated suite should not claim a reuse transaction.");
    }

    private static void UnsafeRecipesNeverShareState()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            new CatalogTest
            {
                Id = "fresh-a",
                Recipe = "recipe-a",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FreshGenerationRequired
                }
            },
            new CatalogTest
            {
                Id = "fresh-b",
                Recipe = "recipe-b",
                Isolation = new CatalogRecipeIsolation
                {
                    Mode = CatalogRecipeIsolationMode.FreshGameRequired
                }
            });
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, index + 10) with { WorkflowId = workflow }
        };
        var lease = new FakeLeaseAdapter();
        var fresh = new FakeFreshGenerationAdapter(11, 12);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            resetAdapter: null,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "unsafe",
                ["fresh-a", "fresh-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(2, fresh.Calls.Count);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Fresh-state recipes must never receive a shared lease.");
        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void MutationRecipesNeverShareState()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("mutation-a", "recipe-a"),
            ReusableTest("mutation-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithGeneration(recipe, 7) with { WorkflowId = workflow }
        };
        using (JsonDocument document = JsonDocument.Parse(
                   "{\"projects\":[],\"inputs\":{},\"allowInGameMutation\":true}"))
        {
            adapter.ShowDefinitions["recipe-a"] = document.RootElement.Clone();
        }

        var lease = new FakeLeaseAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "mutation",
                ["mutation-a", "mutation-b"],
                workflowId: "workflow-mutation")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "A recipe that explicitly allows in-game mutation must not receive a shared lease.");
        AssertEqual("RIMTEST_RECIPE_MUTATION_NOT_SHAREABLE", execution.Reuse!.FallbackReason);
    }

    private static void IncompatibleReuseProfilesFallBackSafely()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("profile-project", "recipe-project"),
            ReusableTest("profile-baseline", "recipe-baseline"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-profile-" + index, "operation-profile-" + index)
        };
        SetRecipeProfile(adapter, "recipe-project", ["frontier"]);
        SetRecipeProfile(adapter, "recipe-baseline", []);
        adapter.Plans["recipe-project"] = SatisfiedPlan("recipe-project");
        adapter.Plans["recipe-baseline"] = SatisfiedPlan("recipe-baseline");
        var lease = new FakeLeaseAdapter();
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "profile-incompatible",
                ["profile-baseline", "profile-project"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(0, lease.BeginCalls);
        Assert(adapter.ExecutionContexts.All(static context => context is null),
            "Recipes with incompatible profiles must not share a supplied lease.");
        Assert(execution.Reuse is not null,
            "A rejected reuse group must remain visible in the bounded summary.");
        AssertEqual(0, execution.Reuse!.GroupsPlanned);
        AssertEqual("RIMTEST_REUSE_PROFILE_INCOMPATIBLE", execution.Reuse.FallbackReason);
    }

    private static void ReusePlannerGroupsCompatibleTestsDeterministically()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared-1", "recipe-a", "shared"),
            ReusableTestWithKey("b-other-1", "recipe-b", "other"),
            ReusableTestWithKey("c-shared-2", "recipe-c", "shared"),
            ReusableTestWithKey("d-other-2", "recipe-d", "other"));
        var profiles = new Dictionary<string, CatalogSuiteRecipeProfile?>(
            StringComparer.Ordinal)
        {
            ["recipe-a"] = RecipeProfile("profile-shared"),
            ["recipe-b"] = RecipeProfile("profile-other"),
            ["recipe-c"] = RecipeProfile("profile-shared"),
            ["recipe-d"] = RecipeProfile("profile-other")
        };

        CatalogSuiteReusePlan first = CatalogSuiteReusePlanner.Plan(
            catalog,
            ["d-other-2", "c-shared-2", "b-other-1", "a-shared-1"],
            profiles);
        CatalogSuiteReusePlan second = CatalogSuiteReusePlanner.Plan(
            catalog,
            ["a-shared-1", "b-other-1", "c-shared-2", "d-other-2"],
            profiles);

        string[] expectedOrder =
            ["a-shared-1", "c-shared-2", "b-other-1", "d-other-2"];
        AssertSequence(expectedOrder, first.ExecutionOrder.ToArray());
        AssertSequence(expectedOrder, second.ExecutionOrder.ToArray());
        AssertEqual(2, first.Groups.Count);
        AssertSequence(["a-shared-1", "c-shared-2"], first.Groups[0].TestIds.ToArray());
        AssertSequence(["b-other-1", "d-other-2"], first.Groups[1].TestIds.ToArray());
        AssertEqual(first.Groups[0].ReuseKey, second.Groups[0].ReuseKey);
        AssertEqual(first.Groups[0].Mode, second.Groups[0].Mode);
        AssertEqual(first.Groups[0].ProfileSignature, second.Groups[0].ProfileSignature);
        AssertEqual(first.Groups[1].ReuseKey, second.Groups[1].ReuseKey);
        AssertEqual(first.Groups[1].Mode, second.Groups[1].Mode);
        AssertEqual(first.Groups[1].ProfileSignature, second.Groups[1].ProfileSignature);
        AssertSequence(first.Groups[0].TestIds.ToArray(), second.Groups[0].TestIds.ToArray());
        AssertSequence(first.Groups[1].TestIds.ToArray(), second.Groups[1].TestIds.ToArray());
        AssertEqual(null, first.FallbackReason);
    }

    private static void ReusePlannerPreservesHardBoundaries()
    {
        CatalogTest fresh = new()
        {
            Id = "b-fresh",
            Recipe = "recipe-fresh",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FreshGenerationRequired
            }
        };
        CatalogDocument freshCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared", "recipe-a", "shared"),
            fresh,
            ReusableTestWithKey("c-shared", "recipe-c", "shared"));
        var compatibleProfiles = new Dictionary<string, CatalogSuiteRecipeProfile?>(
            StringComparer.Ordinal)
        {
            ["recipe-a"] = RecipeProfile("same"),
            ["recipe-c"] = RecipeProfile("same")
        };
        CatalogSuiteReusePlan freshPlan = CatalogSuiteReusePlanner.Plan(
            freshCatalog,
            ["a-shared", "b-fresh", "c-shared"],
            compatibleProfiles);
        AssertSequence(
            ["a-shared", "b-fresh", "c-shared"],
            freshPlan.ExecutionOrder.ToArray());
        AssertEqual(0, freshPlan.Groups.Count);

        CatalogDocument unavailableCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-unavailable", "recipe-a", "shared"),
            ReusableTestWithKey("b-unavailable", "recipe-b", "shared"));
        CatalogSuiteReusePlan unavailablePlan = CatalogSuiteReusePlanner.Plan(
            unavailableCatalog,
            ["a-unavailable", "b-unavailable"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same")
            });
        AssertEqual(0, unavailablePlan.Groups.Count);
        AssertEqual("RIMTEST_REUSE_PROFILE_UNAVAILABLE", unavailablePlan.FallbackReason);

        CatalogSuiteReusePlan incompatiblePlan = CatalogSuiteReusePlanner.Plan(
            unavailableCatalog,
            ["a-unavailable", "b-unavailable"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("projects-a"),
                ["recipe-b"] = RecipeProfile("projects-b")
            });
        AssertEqual(0, incompatiblePlan.Groups.Count);
        AssertEqual("RIMTEST_REUSE_PROFILE_INCOMPATIBLE", incompatiblePlan.FallbackReason);

        CatalogTest wrongMode = ReusableTestWithKey(
            "b-mode", "recipe-b", "shared", CatalogRecipeIsolationMode.SameGenerationSafe);
        CatalogDocument modeCatalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-mode", "recipe-a", "shared"),
            wrongMode);
        CatalogSuiteReusePlan modePlan = CatalogSuiteReusePlanner.Plan(
            modeCatalog,
            ["a-mode", "b-mode"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same"),
                ["recipe-b"] = RecipeProfile("same")
            });
        AssertEqual(0, modePlan.Groups.Count);

        CatalogTest invalidResetA = new()
        {
            Id = "a-invalid-reset",
            Recipe = "recipe-a",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "shared"
            }
        };
        CatalogTest invalidResetB = new()
        {
            Id = "b-invalid-reset",
            Recipe = "recipe-b",
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "shared",
                ResetRecipe = ""
            }
        };
        CatalogSuiteReusePlan invalidResetPlan = CatalogSuiteReusePlanner.Plan(
            CreateIsolationCatalog(invalidResetA, invalidResetB),
            ["a-invalid-reset", "b-invalid-reset"],
            new Dictionary<string, CatalogSuiteRecipeProfile?>(StringComparer.Ordinal)
            {
                ["recipe-a"] = RecipeProfile("same"),
                ["recipe-b"] = RecipeProfile("same")
            });
        AssertEqual(0, invalidResetPlan.Groups.Count);
    }

    private static void GroupedSuiteExecutionAvoidsLifecycleTransitions()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTestWithKey("a-shared-1", "recipe-a", "shared"),
            ReusableTestWithKey("b-other-1", "recipe-b", "other"),
            ReusableTestWithKey("c-shared-2", "recipe-c", "shared"),
            ReusableTestWithKey("d-other-2", "recipe-d", "other"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(
                    recipe,
                    context?.LeaseId == "lease-other" ? 8 : 7,
                    context?.LeaseId,
                    workflow,
                    "run-grouped-" + index,
                    "operation-grouped-" + index)
        };
        foreach (string recipe in new[] { "recipe-a", "recipe-b", "recipe-c", "recipe-d" })
        {
            adapter.Plans[recipe] = SatisfiedPlan(recipe);
        }
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-shared", 7));
        lease.BeginResults.Enqueue(SuccessLease("lease-other", 8));
        var fresh = new FakeFreshGenerationAdapter(8);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "grouped",
                ["d-other-2", "c-shared-2", "b-other-1", "a-shared-1"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertSequence(
            ["recipe-a", "recipe-c", "recipe-b", "recipe-d"],
            adapter.RunCalls);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, execution.Reuse!.GroupsPlanned);
        AssertEqual(2, execution.Reuse.GroupsUsed);
        AssertEqual(2, execution.Reuse.GenerationsAvoided);
        AssertEqual(2, execution.Reuse.RelaunchesAvoided);
        AssertEqual(1, execution.Reuse.Relaunches);
    }

    private static void ReuseCancellationCannotContaminateLaterTests()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("cancel-a", "recipe-a"),
            ReusableTest("cancel-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["recipe-a"] = CancelledRun("recipe-a");
        adapter.Runs["recipe-b"] = PassRunWithLease(
            "recipe-b", 8, "lease-recovered", null,
            "cancel-recovered", "cancel-recovered-operation");
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-cancel", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "cancel-reuse",
                ["cancel-a", "cancel-b"])
            .GetAwaiter()
            .GetResult();

        AssertSequence(["recipe-a"], adapter.RunCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
    }

    private static void DevBridgeReuseRefusalPreservesCause()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("refusal-a", "recipe-a"),
            ReusableTest("refusal-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-refusal-pass", "operation-refusal-pass")
                : new DevBridgeRecipeRunResult(
                    recipe,
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.DevBridgeRefusal,
                        "RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
                        "A supplied lease cannot authorize an autonomous restart."),
                    false,
                    "run-refusal-fail",
                    7,
                    context?.LeaseId,
                    null,
                    null,
                    null,
                    "ensure-ready",
                    true,
                    0,
                    [],
                    workflow)
        };
        SetRecipeProfile(adapter, "recipe-a", []);
        SetRecipeProfile(adapter, "recipe-b", []);
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-refusal", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "refusal",
                ["refusal-a", "refusal-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Reuse!.ReuseInvalidationReason);
        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Tests[1].ErrorCode);
        Assert(execution.Reuse.Mismatch is not null,
            "A supplied-lease refusal must expose its bounded mismatch details.");
        AssertEqual(true, execution.Reuse.Mismatch!.RestartRequired);
        AssertEqual(0, execution.Reuse.Mismatch.LaunchesConsumed);
        AssertEqual("RECIPE_SUPPLIED_LEASE_REQUIRES_READY",
            execution.Reuse.Mismatch.ErrorCode);
        AssertEqual("infrastructure",
            RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void CompatibleRecipesReuseOneGeneration()
    {
        AssertCompatibleRecipesReuse(failFast: false);
    }

    private static void FailFastPreservesCompatibleReuse()
    {
        AssertCompatibleRecipesReuse(failFast: true);
    }

    private static void AssertCompatibleRecipesReuse(bool failFast)
    {
        const string workflowId = "workflow-reuse";
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("read-a", "recipe-a"),
            ReusableTest("read-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(
                    recipe,
                    7,
                    context?.LeaseId,
                    workflow,
                    "run-reuse-" + index,
                    "operation-reuse-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-reuse", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reuse",
                ["read-b", "read-a"],
                workflowId: workflowId,
                failFast: failFast)
            .GetAwaiter()
            .GetResult();
        CatalogSuiteReuseSummary reuse = execution.Reuse!;

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(1, lease.BeginCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual(1, reuse.GroupsUsed);
        AssertEqual(1, reuse.GenerationsUsed);
        AssertEqual(0, reuse.Relaunches);
        AssertEqual(1, reuse.GenerationsAvoided);
        AssertEqual(1, reuse.RelaunchesAvoided);
        AssertEqual("used", reuse.Status);
        AssertEqual(2, adapter.ExecutionContexts.Count);
        Assert(adapter.ExecutionContexts.All(
                context => context?.LeaseId == "lease-reuse"),
            "Compatible recipes must execute under the same lease.");
        AssertSequence(
            ["run-reuse-0", "run-reuse-1"],
            adapter.RunResults.Select(static result => result.RunId!).ToArray());
        AssertSequence(
            ["operation-reuse-0", "operation-reuse-1"],
            adapter.RunResults.SelectMany(static result => result.Operations)
                .Select(static operation => operation.OperationId!)
                .ToArray());
        Assert(adapter.RunResults.All(result => result.WorkflowId == workflowId),
            "Workflow identity must propagate to every shared-generation recipe.");
        if (failFast)
        {
            Assert(execution.FailFast is not null &&
                    execution.FailFast.NotLaunched == 0 &&
                    execution.FailFast.ValidationCompleted,
                "Fail-fast PASS must preserve complete reuse execution.");
        }
    }

    private static void ResettableRecipesRequireSuccessfulReset()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ResettableTest("reset-a", "recipe-a"),
            ResettableTest("reset-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 8, context?.LeaseId, workflow,
                    "run-reset-" + index, "operation-reset-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var reset = new FakeResetAdapter
        {
            Result = SuccessfulReset("lease-reset", 8)
        };
        var lease = new FakeLeaseAdapter { BeginResult = SuccessLease("lease-reset", 8) };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            reset);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reset",
                ["reset-a", "reset-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, reset.Calls.Count);
        AssertEqual("fixture-reset", reset.Calls[0].RecipeId);
        AssertEqual(1, execution.Reuse!.FixtureResets);
        AssertEqual(1, execution.Reuse.GenerationsAvoided);
        AssertEqual(1, execution.Reuse.RelaunchesAvoided);
        AssertEqual("used", execution.Reuse.Status);
        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void FailedResetInvalidatesReuse()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ResettableTest("reset-fail-a", "recipe-a"),
            ResettableTest("reset-fail-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
            {
                int generation = context?.LeaseId == "lease-reset-2" ? 9 : 8;
                return PassRunWithLease(recipe, generation, context?.LeaseId, workflow,
                    "run-reset-fail-" + index, "operation-reset-fail-" + index);
            }
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var reset = new FakeResetAdapter
        {
            Result = new DevBridgeResetResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "RESET_NOT_VERIFIED"),
                8,
                "lease-reset-1")
        };
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-reset-1", 8));
        lease.BeginResults.Enqueue(SuccessLease("lease-reset-2", 9));
        lease.RenewResults.Enqueue(SuccessLease("lease-reset-1", 8));
        var fresh = new FakeFreshGenerationAdapter(9);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            reset,
            fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "reset-fail",
                ["reset-fail-a", "reset-fail-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, reset.Calls.Count);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual("reset-fail-b", execution.Reuse.ReuseInvalidatedAfter);
        AssertEqual("RESET_NOT_VERIFIED", execution.Reuse.ReuseInvalidationReason);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
        AssertEqual("infrastructure", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual("lease-reset-2", adapter.ExecutionContexts[1]!.LeaseId);
    }

    private static void TestFailureCannotContaminateLaterRecipes()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("fail-a", "recipe-a"),
            ReusableTest("fail-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? FailedRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "failure-run", "failure-operation")
                : PassRunWithLease(recipe, 8, context?.LeaseId, workflow,
                    "recovered-run", "recovered-operation")
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter();
        lease.BeginResults.Enqueue(SuccessLease("lease-failure-1", 7));
        lease.BeginResults.Enqueue(SuccessLease("lease-failure-2", 8));
        var fresh = new FakeFreshGenerationAdapter(8);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "failure-recovery",
                ["fail-a", "fail-b"])
            .GetAwaiter()
            .GetResult();
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 10);

        AssertEqual("fail", result.Status);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(2, lease.BeginCalls);
        AssertEqual(0, execution.Reuse?.GenerationsAvoided ?? 0);
        AssertEqual("lease-failure-2", adapter.ExecutionContexts[1]!.LeaseId);
        AssertEqual("failure-run", adapter.RunResults[0].RunId);
    }

    private static void GenerationAndLeaseChangesInvalidateReuse()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("mismatch-a", "recipe-a"),
            ReusableTest("mismatch-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "run-mismatch-" + index, "operation-mismatch-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-mismatch", 7)
        };
        lease.RenewResults.Enqueue(SuccessLease("lease-mismatch", 9));
        var fresh = new FakeFreshGenerationAdapter(10);
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease,
            freshGenerationAdapter: fresh);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "mismatch",
                ["mismatch-a", "mismatch-b"])
            .GetAwaiter()
            .GetResult();

        AssertEqual("invalidated", execution.Reuse!.Status);
        AssertEqual("RIMTEST_REUSE_LEASE_INVALID", execution.Reuse.ReuseInvalidationReason);
        AssertEqual(1, fresh.Calls.Count);
        AssertEqual(0, execution.Reuse.GenerationsAvoided);
        AssertEqual(0, execution.Reuse.RelaunchesAvoided);
        AssertEqual("infrastructure", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
    }

    private static void ReuseResultIsBoundedAndIdentitiesStayDistinct()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("bounded-a", "recipe-a"),
            ReusableTest("bounded-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) =>
                PassRunWithLease(recipe, 7, context?.LeaseId, workflow,
                    "bounded-run-" + index, "bounded-operation-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            new FakeLeaseAdapter { BeginResult = SuccessLease("lease-bounded", 7) });

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "bounded",
                ["bounded-a", "bounded-b"],
                workflowId: "workflow-bounded")
            .GetAwaiter()
            .GetResult();
        string json = CatalogJsonFacade.Serialize(
            RimTestSuiteResultFactory.FromExecution(execution, 10));

        Assert(RimTestOutputBudgets.Utf8Bytes(json) <= RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Reuse summary exceeded the bounded suite output budget.");
        Assert(json.Contains("\"reuse\"", StringComparison.Ordinal),
            "The bounded suite result must expose reuse planning information.");
        Assert(!json.Contains("bounded-operation", StringComparison.Ordinal),
            "Suite output must not expose child operation transcripts.");
        AssertEqual(2, adapter.RunResults.Select(static result => result.RunId)
            .Distinct(StringComparer.Ordinal).Count());
        AssertEqual(2, adapter.RunResults.SelectMany(static result => result.Operations)
            .Select(static operation => operation.OperationId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    private static void FreshGenerationAdapterProvesReadinessConservatively()
    {
        var recipe = new FakeRecipeAdapter();
        using JsonDocument definition = JsonDocument.Parse(
            "{\"id\":\"recipe-a\",\"projects\":[\"fixture\"],\"inputs\":{\"quicktest\":true}}");
        recipe.ShowDefinitions["recipe-a"] = definition.RootElement.Clone();
        int calls = 0;
        var transport = new FakeTransport((request, _) =>
        {
            calls++;
            Assert(request.Arguments.Contains("restart"),
                "Fresh-generation preparation must use DevBridge restart.");
            Assert(request.Arguments.Contains("--projects"),
                "Recipe project intent must be supplied to DevBridge.");
            Assert(request.Arguments.Contains("quicktest=true"),
                "Recipe test inputs must be supplied to DevBridge.");
            Assert(request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.ContainsKey("DEVBRIDGE_AGENT"),
                "Lifecycle requests must carry a stable owner identity.");
            return ProcessResult(calls == 1
                ? "{\"success\":true,\"exitCode\":0,\"state\":\"READY\",\"generation\":8,\"restartPending\":false}"
                : "{\"success\":true,\"exitCode\":0,\"state\":\"LOADING\",\"generation\":8,\"restartPending\":true}");
        });
        var adapter = new DevBridgeFreshGenerationAdapter(
            recipe,
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                RunTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeFreshGenerationResult ready = adapter.EnsureFreshGenerationAsync(
                "recipe-a",
                7,
                "workflow-fresh")
            .GetAwaiter()
            .GetResult();
        DevBridgeFreshGenerationResult unready = adapter.EnsureFreshGenerationAsync(
                "recipe-a",
                8,
                "workflow-fresh")
            .GetAwaiter()
            .GetResult();

        Assert(ready.IsUsable, "A typed newer READY generation should be usable.");
        AssertEqual(8, ready.Generation);
        AssertEqual(1, ready.LaunchesConsumed);
        AssertEqual("DEVBRIDGE_FRESH_GENERATION_NOT_READY", unready.Status.ErrorCode);
    }

    private static void LeaseAdapterPreservesOwnerAndGenerationIdentity()
    {
        var transport = new FakeTransport((request, _) =>
        {
            string operation = request.Arguments.Count > 1
                ? request.Arguments[1]
                : string.Empty;
            string leaseId = operation == "begin"
                ? "lease-adapter"
                : request.Arguments.FirstOrDefault(value =>
                    value.StartsWith("lease-", StringComparison.Ordinal)) ?? "lease-adapter";
            return ProcessResult(
                $"progress\n{{\"success\":true,\"exitCode\":0,\"generation\":12,\"leaseId\":\"{leaseId}\"}}");
        });
        var adapter = new DevBridgeLeaseAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeLeaseResult begin = adapter.BeginLeaseAsync("workflow-lease")
            .GetAwaiter()
            .GetResult();
        DevBridgeLeaseResult renew = adapter.RenewLeaseAsync(
                "lease-adapter",
                "workflow-lease")
            .GetAwaiter()
            .GetResult();
        DevBridgeLeaseResult end = adapter.EndLeaseAsync(
                "lease-adapter",
                "workflow-lease")
            .GetAwaiter()
            .GetResult();

        Assert(begin.IsUsable && renew.IsUsable && end.Status.IsSuccess,
            "Lifecycle JSON responses should remain usable across lease operations.");
        AssertEqual("lease-adapter", begin.LeaseId);
        AssertEqual(12, begin.Generation);
        AssertEqual(3, transport.Requests.Count);
        string? owner = transport.Requests[0].EnvironmentVariables!["DEVBRIDGE_AGENT"];
        Assert(transport.Requests.All(request =>
                request.EnvironmentVariables!["DEVBRIDGE_AGENT"] == owner),
            "All workflow lease operations must use one stable DevBridge owner identity.");
    }

    private static void ExistingCompatibleLeaseRetriesBlockedRecipe()
    {
        CatalogDocument catalog = CreateIsolationCatalog(
            ReusableTest("lease-a", "recipe-a"),
            ReusableTest("lease-b", "recipe-b"));
        var adapter = new FakeRecipeAdapter
        {
            RunFactory = static (recipe, workflow, context, index) => index == 0
                ? LeaseRequiredRun(recipe, workflow, context?.LeaseId)
                : PassRunWithLease(
                    recipe,
                    7,
                    context?.LeaseId,
                    workflow,
                    "run-compatible-" + index,
                    "operation-compatible-" + index)
        };
        adapter.Plans["recipe-a"] = SatisfiedPlan("recipe-a");
        adapter.Plans["recipe-b"] = SatisfiedPlan("recipe-b");
        var lease = new FakeLeaseAdapter
        {
            BeginResult = SuccessLease("lease-existing", 7)
        };
        var runner = new CatalogSuiteRunner(
            adapter,
            new CatalogTestExecutionService(adapter),
            lease);

        CatalogSuiteExecutionResult execution = runner.RunAsync(
                catalog,
                "lease-recovery",
                ["lease-a", "lease-b"],
                workflowId: "wf-compatible-lease")
            .GetAwaiter()
            .GetResult();

        AssertEqual("pass", RimTestSuiteResultFactory.FromExecution(execution, 10).Status);
        AssertEqual(1, lease.BeginCalls);
        AssertEqual(1, lease.EndCalls);
        AssertEqual(3, adapter.RunCalls.Count);
        Assert(execution.PrerequisiteRecovery?.Single().State == "recovered",
            "A blocked operation resumed under the already-valid compatible lease.");
        Assert(adapter.ExecutionContexts.All(
                context => context?.LeaseId == "lease-existing"),
            "The retry must reuse the supported transaction lease rather than acquire another one.");
    }

    private static void EnvironmentFallbackDrivesAffectedFallback()
    {
        IReadOnlyList<string> runCalls = [];
        CliResult result = WithFallbackSuiteEnvironment(
            "smoke",
            () =>
            {
                string directory = CreateTempDirectory();
                try
                {
                    string catalogPath = Path.Combine(directory, "catalog.json");
                    File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
                    var adapter = new FakeRecipeAdapter();
                    adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
                    adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
                    var developmentAdapter = new FakeModDevelopmentAdapter();
                    var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                        new RimContextAdapterStatus(
                            RimContextImpactOutcome.Unknown,
                            "RIMCONTEXT_RESULT_TRUNCATED"),
                        [],
                        [],
                        true));
                    var stdout = new StringWriter();
                    var stderr = new StringWriter();
                    int exitCode = CliApplication.RunAsync(
                            [
                                "affected",
                                "Source/Unknown.cs",
                                "--run",
                                "--json",
                                "--catalog",
                                catalogPath
                            ],
                            stdout,
                            stderr,
                            adapter,
                            cancellationToken: default,
                            impactAdapter: impactAdapter,
                            developmentAdapter: developmentAdapter)
                        .GetAwaiter()
                        .GetResult();

                    runCalls = adapter.RunCalls.ToArray();
                    return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }
            });

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
        AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
        AssertSequence(
            ["assembler-fixture", "settings-fixture"],
            runCalls);
        Assert(string.IsNullOrEmpty(result.Stderr),
            "An environment fallback must not add affected diagnostics.");
    }

    private static void AffectedRunUsesConservativeFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unknown,
                    "RIMCONTEXT_RESULT_TRUNCATED"),
                [],
                [],
                true));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Unknown.cs",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    cancellationToken: default,
                    impactAdapter: impactAdapter,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedDeletedPathUsesConservativeFallback()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var git = new FakeGitChangeProvider(
                new GitChangeDiscoveryResult(true, ["Source/Deleted.cs"])
                {
                    Changes =
                    [
                        new GitChangedPath("Source/Deleted.cs", "D ")
                    ]
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "--run",
                        "--json",
                        "--fallback-suite",
                        "smoke",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("conservative", root.GetProperty("selectionStatus").GetString());
            AssertEqual("RIMCONTEXT_CHANGE_UNPROVEN", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("smoke", root.GetProperty("fallbackSuite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertSequence(
                ["assembler-fixture", "settings-fixture"],
                adapter.RunCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void AffectedRenameWithoutFallbackBlocks()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact());
            var git = new FakeGitChangeProvider(
                new GitChangeDiscoveryResult(
                    true,
                    ["Source/Old.cs", "Source/New.cs"])
                {
                    Changes =
                    [
                        new GitChangedPath(
                            "Source/New.cs",
                            "R100",
                            "Source/Old.cs")
                    ]
                });

            CliResult result = WithFallbackSuiteEnvironment(
                null,
                () => WithCurrentDirectory(
                    directory,
                    () =>
                    {
                        var stdout = new StringWriter();
                        var stderr = new StringWriter();
                        int exitCode = CliApplication.RunAsync(
                                [
                                    "affected",
                                    "--run",
                                    "--json",
                                    "--catalog",
                                    catalogPath
                                ],
                                stdout,
                                stderr,
                                adapter,
                                impactAdapter: impactAdapter,
                                gitChangeProvider: git)
                            .GetAwaiter()
                            .GetResult();
                        return new CliResult(
                            exitCode,
                            stdout.ToString(),
                            stderr.ToString());
                    }));

            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
            AssertEqual("conservative", root.GetProperty("status").GetString());
            AssertEqual("RIMCONTEXT_CHANGE_UNPROVEN", root.GetProperty("errorCode").GetString());
            AssertEqual(
                "rimliaison affected --run --fallback-suite <suite>",
                root.GetProperty("nextAction").GetString());
            Assert(!root.TryGetProperty("suite", out _),
                "A rename without fallback must never become an empty-suite pass.");
            AssertEqual(0, adapter.RunCalls.Count);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SuiteRunCliIsDeterministic()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = PassRun("assembler-fixture");
            adapter.Runs["settings-fixture"] = PassRun("settings-fixture");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "suite", "run", "smoke", "--fail-fast", "--json", "--catalog", catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual("pass", root.GetProperty("status").GetString());
            AssertEqual("smoke", root.GetProperty("suite").GetString());
            AssertEqual(2, root.GetProperty("passed").GetInt32());
            AssertEqual(0, root.GetProperty("failed").GetInt32());
            Assert(root.GetProperty("failFast").GetProperty("validationCompleted").GetBoolean(),
                "The suite CLI must expose complete fail-fast validation on PASS.");
            AssertEqual(0, root.GetProperty("failFast").GetProperty("notLaunched").GetInt32());
            Assert(!stdout.ToString().Contains("operations", StringComparison.Ordinal),
                "Suite CLI must not emit child operations.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidationCapabilityPresent()
    {
        var capability = new DevBridgeCapability(
            "rimworld/get_game_state",
            [],
            "Game state",
            "Read state",
            "inspection",
            "rimworld",
            "Core",
            [],
            true,
            "2.1.0",
            "rimbridge-capability/v2");
        ValidationCapabilityPreflightResult result = new ValidationCapabilityNegotiator(
                new FakeCapabilityAdapter([capability]))
            .NegotiateAsync(
                CapabilityTestCatalog(
                    new CatalogCapabilityRequirement
                    {
                        CapabilityId = capability.Id,
                        ExpectedProvider = "rimworld",
                        MinimumVersion = "2.0.0",
                        MinimumSchemaVersion = "rimbridge-capability/v1",
                        Purpose = "Read live state",
                        Owner = "RimBridgeServer"
                    }).Tests[0])
            .GetAwaiter()
            .GetResult();
        Assert(
            result.IsAvailable,
            $"A compatible declared capability must permit validation: {result.Outcome}/{result.ErrorCode}/{string.Join(",", result.Evidence.Select(value => value.ErrorCode + ":provider=" + value.ExpectedProvider + "/" + value.DiscoveredProvider + ":schema=" + value.MinimumSchemaVersion + "/" + value.DiscoveredSchemaVersion + ":version=" + value.MinimumVersion + "/" + value.DiscoveredVersion))}");
    }

    private static void ValidationCapabilityAbsentBlocksExecution()
    {
        var adapter = new FakeRecipeAdapter
        {
            Runs = { ["recipe"] = FailedRun("recipe", "product", "PRODUCT_ASSERTION_FAILED") }
        };
        var capability = new FakeCapabilityAdapter([]);
        CatalogTestRunResult run = new CatalogTestRecipeRunner(adapter, capability)
            .RunAsync(CapabilityTestCatalog(new CatalogCapabilityRequirement
            {
                CapabilityId = "rimworld/get_game_state",
                Purpose = "Read live state",
                Owner = "RimBridgeServer"
            }), "test")
            .GetAwaiter()
            .GetResult();
        AssertEqual(0, adapter.RunCalls.Count);
        Assert(run.CapabilityPreflight?.IsBlocked == true, "Missing capability must block preflight.");
        AssertEqual(ValidationCapabilitySchema.UnavailableCode, run.CapabilityPreflight!.ErrorCode);
    }

    private static void ValidationCapabilityProviderMismatchBlocks()
    {
        ValidationCapabilityPreflightResult result = Negotiate(
            new CatalogCapabilityRequirement
            {
                CapabilityId = "capability/state",
                ExpectedProvider = "owner-a",
                Purpose = "Read state"
            },
            new DevBridgeCapability(
                "capability/state", [], "State", "State", null, "owner-b", null, [], true));
        Assert(result.IsBlocked, "A provider mismatch must block validation.");
        AssertEqual(ValidationCapabilitySchema.IncompatibleCode, result.ErrorCode);
        AssertEqual("owner-b", result.Evidence[0].DiscoveredProvider);
    }

    private static void ValidationCapabilitySchemaMismatchBlocks()
    {
        ValidationCapabilityPreflightResult result = Negotiate(
            new CatalogCapabilityRequirement
            {
                CapabilityId = "capability/state",
                MinimumSchemaVersion = "schema/v2",
                MinimumVersion = "3.0.0",
                Purpose = "Read state"
            },
            new DevBridgeCapability(
                "capability/state", [], "State", "State", null, "owner", null, [], true,
                "2.0.0",
                "schema/v1"));
        Assert(result.IsBlocked, "An incompatible schema/version must block validation.");
        AssertEqual(ValidationCapabilitySchema.IncompatibleCode, result.Evidence[0].ErrorCode);
    }

    private static void ValidationCapabilityDiscoveryInfrastructureIsDistinct()
    {
        var recipe = new FakeRecipeAdapter
        {
            Runs = { ["recipe"] = FailedRun("recipe", "product", "PRODUCT_ASSERTION_FAILED") }
        };
        var service = new CatalogTestExecutionService(
            recipe,
            capabilityAdapter: new FakeCapabilityAdapter(
                status: new DevBridgeCapabilityStatus(
                    DevBridgeCapabilityOutcome.InfrastructureFailure,
                    "DEVBRIDGE_CAPABILITIES_TIMEOUT")));
        CatalogTestExecutionResult result = service.RunAsync(
                CapabilityTestCatalog(new CatalogCapabilityRequirement
                {
                    CapabilityId = "capability/state",
                    Purpose = "Read state"
                }),
                "test",
                Stopwatch.GetTimestamp())
            .GetAwaiter()
            .GetResult();
        AssertEqual("infrastructure", result.Result.Status);
        AssertEqual("DEVBRIDGE_CAPABILITIES_TIMEOUT", result.Result.ErrorCode);
        AssertEqual(0, recipe.RunCalls.Count);
    }

    private static void ValidationCapabilityOldRecipeRemainsCompatible()
    {
        var recipe = new FakeRecipeAdapter
        {
            Runs = { ["recipe"] = SuccessfulRun("recipe") }
        };
        CatalogTestRunResult result = new CatalogTestRecipeRunner(recipe)
            .RunAsync(CapabilityTestCatalog(), "test")
            .GetAwaiter()
            .GetResult();
        AssertEqual(1, recipe.RunCalls.Count);
        Assert(result.CapabilityPreflight?.IsAvailable == true,
            "Recipes without requirements must retain the old execution path.");
    }

    private static void ValidationCapabilityJsonIsStructured()
    {
        ValidationCapabilityPreflightResult result = Negotiate(
            new CatalogCapabilityRequirement
            {
                CapabilityId = "capability/state",
                ExpectedProvider = "owner",
                Purpose = "Read state",
                Owner = "RimBridgeServer"
            });
        string json = JsonSerializer.Serialize(result.Evidence);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement evidence = document.RootElement[0];
        AssertEqual("VALIDATION_CAPABILITY_UNAVAILABLE", evidence.GetProperty("errorCode").GetString());
        AssertEqual("test", evidence.GetProperty("validationId").GetString());
        AssertEqual("capability/state", evidence.GetProperty("requiredCapabilityId").GetString());
        Assert(!evidence.GetProperty("operationAttempted").GetBoolean(),
            "Capability blocker evidence must state that execution was not attempted.");
        AssertEqual("RimBridgeServer", evidence.GetProperty("probableOwner").GetString());
    }

    private static void ValidationCapabilitySuiteAggregation()
    {
        var execution = new CatalogSuiteExecutionResult(
            "suite",
            [
                RimTestResultFactory.CapabilityBlocked(
                    "blocked-test",
                    [
                        new ValidationCapabilityEvidence
                        {
                            ErrorCode = ValidationCapabilitySchema.UnavailableCode,
                            ValidationId = "blocked-test",
                            RequiredCapabilityId = "capability/state",
                            Reason = "Read state",
                            ProbableOwner = "owner",
                            RecommendedRemediation = "Install capability",
                            Fingerprint = "capability|capability/state|provider|any"
                        }
                    ]),
                new RimTestResult
                {
                    Status = "pass",
                    Test = "pass-test"
                }
            ],
            0,
            false);
        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 1);
        AssertEqual("blocked", result.Status);
        AssertEqual(1, result.Blocked);
        AssertEqual(1, result.Passed);
        AssertEqual(1, result.BlockedTestCount);
        Assert(result.Failures is null,
            "Capability blocking is not a failed test.");
    }

    private static void ValidationCapabilityObservabilityDeduplicates()
    {
        using var store = new AgentObservabilityStore();
        RegisterObservationAgent(store, "run-a", "agent-a", "mod-a");
        RegisterObservationAgent(store, "run-b", "agent-b", "mod-b");
        AppendCapabilityGap(store, "run-a", "agent-a", "mod-a");
        AppendCapabilityGap(store, "run-b", "agent-b", "mod-b");
        AgentIssue[] issues = store.GetIssues().ToArray();
        AgentIssue issue = issues.Single(value => value.Category == AgentIssueCategory.CapabilityGap);
        AssertEqual(2, issue.Occurrences);
        AssertEqual(2, issue.AffectedAgentIds!.Count);
        Assert(issue.Summary.Contains("CAPABILITY GAP / BLOCKED", StringComparison.Ordinal),
            "Capability gaps must be visibly classified.");
    }

    private static void ValidationCapabilityProductFailureRemainsDistinct()
    {
        using var store = new AgentObservabilityStore();
        RegisterObservationAgent(store, "run", "agent", "mod");
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.TestFailed,
            "Test failed.",
            new { errorCode = "PRODUCT_ASSERTION_FAILED", operationKey = "test:test" }));
        AgentIssue issue = store.GetIssues().Single();
        AssertEqual(AgentIssueCategory.Error, issue.Category);
        Assert(issue.Category != AgentIssueCategory.CapabilityGap,
            "Product failures must not be classified as capability gaps.");
    }

    private static void ValidationCapabilityInfrastructureRemainsDistinct()
    {
        using var store = new AgentObservabilityStore();
        RegisterObservationAgent(store, "run", "agent", "mod");
        store.AppendEvent(new AgentEventRequest(
            "run",
            "agent",
            "mod",
            DevelopmentStage.Testing,
            AgentEventTypes.ToolFailed,
            "Capability discovery failed.",
            new { errorCode = "DEVBRIDGE_CAPABILITIES_TIMEOUT", operationKey = "capability-discovery" }));
        AgentIssue issue = store.GetIssues().Single();
        AssertEqual(AgentIssueCategory.Error, issue.Category);
        Assert(issue.Category != AgentIssueCategory.CapabilityGap,
            "Infrastructure failures must not be classified as capability gaps.");
    }

    private static ValidationCapabilityPreflightResult Negotiate(
        CatalogCapabilityRequirement requirement,
        DevBridgeCapability? capability = null) =>
        new ValidationCapabilityNegotiator(
                new FakeCapabilityAdapter(
                    capability is null ? [] : [capability]))
            .NegotiateAsync(CapabilityTestCatalog(requirement).Tests[0])
            .GetAwaiter()
            .GetResult();

    private static CatalogDocument CapabilityTestCatalog(
        params CatalogCapabilityRequirement[] requirements) =>
        new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "test",
                    Recipe = "recipe",
                    RequiredCapabilities = requirements.Length == 0 ? null : [.. requirements]
                }
            ],
            Suites = []
        };

    private static void RegisterObservationAgent(
        AgentObservabilityStore store,
        string runId,
        string agentId,
        string modId) =>
        store.RegisterAgent(new AgentSnapshot
        {
            RunId = runId,
            AgentId = agentId,
            ModId = modId,
            ModName = modId,
            StartTime = 1
        });

    private static void AppendCapabilityGap(
        AgentObservabilityStore store,
        string runId,
        string agentId,
        string modId) =>
        store.AppendEvent(new AgentEventRequest(
            runId,
            agentId,
            modId,
            DevelopmentStage.Testing,
            AgentEventTypes.ValidationCapabilityBlocked,
            "Validation blocked: required capability capability/state is unavailable.",
            new
            {
                operationKey = "test:test",
                errorCode = ValidationCapabilitySchema.UnavailableCode,
                requiredCapabilityId = "capability/state",
                probableOwner = "RimBridgeServer",
                fingerprint = "capability|capability/state|provider|any",
                operationAttempted = false
            }));


    private static void CapabilitiesDiscoverRegisteredSurface()
    {
        (CliResult result, FakeTransport transport) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    { "id": "rimworld/get_screenshot", "aliases": ["screenshot"], "title": "Screenshot", "summary": "Capture a screenshot", "category": "screenshots", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_screen_targets", "title": "Screen targets", "summary": "Inspect visible UI targets", "category": "ui", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/set_camera", "title": "Camera view", "summary": "Control the camera view", "category": "camera", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_game_state", "title": "Runtime state", "summary": "Inspect live game state", "category": "inspection", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/click", "title": "Live interaction", "summary": "Interact with a live-game target", "category": "interaction", "providerId": "rimworld", "source": "Core", "mutating": true },
                    { "id": "rimtest/invoke_companion", "title": "Companion test", "summary": "Invoke a registered companion test", "category": "companion", "providerId": "rimtest", "source": "Optional" },
                    { "id": "rimworld/run_lua", "title": "Lua script", "summary": "Run a Lua inspection script", "category": "scripts", "providerId": "rimworld", "source": "Optional" }
                  ]
                }
              }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-capabilities/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("RimBridgeServer", root.GetProperty("source").GetString());
        AssertEqual(7, root.GetProperty("count").GetInt32());
        Assert(!root.GetProperty("truncated").GetBoolean(),
            "A small capability registry should not be truncated.");
        string[] ids = root.GetProperty("capabilities")
            .EnumerateArray()
            .Select(capability => capability.GetProperty("id").GetString()!)
            .ToArray();
        Assert(ids.Contains("rimworld/get_screenshot"), "Screenshot capability was not discoverable.");
        Assert(ids.Contains("rimworld/get_screen_targets"), "UI target capability was not discoverable.");
        Assert(ids.Contains("rimworld/set_camera"), "Camera capability was not discoverable.");
        Assert(ids.Contains("rimworld/get_game_state"), "Runtime state capability was not discoverable.");
        Assert(ids.Contains("rimworld/click"), "Live interaction capability was not discoverable.");
        Assert(ids.Contains("rimtest/invoke_companion"), "Companion capability was not discoverable.");
        Assert(ids.Contains("rimworld/run_lua"), "Lua capability was not discoverable.");
        AssertEqual(1, transport.Requests.Count);
    }

    private static void CapabilitiesQueryFiltersRegistry()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    { "id": "rimworld/get_screenshot", "title": "Screenshot", "summary": "Capture the game screen", "category": "screenshots", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/set_camera", "title": "Camera view", "summary": "Control the camera", "category": "view", "providerId": "rimworld", "source": "Core" },
                    { "id": "rimworld/get_game_state", "title": "Runtime state", "summary": "Inspect state", "category": "inspection", "providerId": "rimworld", "source": "Core" }
                  ]
                }
              }
            }
            """,
            "--query",
            "camera",
            "--category",
            "view",
            "--provider",
            "rimworld",
            "--source",
            "Core");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("camera", root.GetProperty("query").GetString());
        AssertEqual("view", root.GetProperty("category").GetString());
        AssertEqual("rimworld", root.GetProperty("providerId").GetString());
        AssertEqual("Core", root.GetProperty("source").GetString());
        AssertEqual(1, root.GetProperty("totalMatches").GetInt32());
        AssertEqual(
            "rimworld/set_camera",
            root.GetProperty("capabilities")[0].GetProperty("id").GetString());
    }

    private static void CapabilitiesBoundOutput()
    {
        var tools = Enumerable.Range(1, 25)
            .Select(index => new
            {
                id = $"rimworld/tool_{index:00}",
                title = $"Tool {index}",
                summary = "Registered capability",
                category = "inspection",
                providerId = "rimworld",
                source = "Core"
            })
            .ToArray();
        string response = JsonSerializer.Serialize(new
        {
            success = true,
            rimBridgeRoute = new
            {
                success = true,
                result = new { tools }
            }
        });

        (CliResult result, _) = RunCapabilitiesFixture(response, "--limit", "3");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(3, root.GetProperty("count").GetInt32());
        AssertEqual(25, root.GetProperty("totalMatches").GetInt32());
        Assert(root.GetProperty("truncated").GetBoolean(),
            "Capability discovery must report bounded output.");
        AssertEqual(3, root.GetProperty("capabilities").GetArrayLength());
    }

    private static void CapabilitiesPreserveParameterMetadata()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": {
                "success": true,
                "result": {
                  "tools": [
                    {
                      "id": "rimworld/get_game_state",
                      "title": "Runtime state",
                      "summary": "Inspect live game state",
                      "parameters": [
                        { "name": "includeColonists", "parameterType": "boolean", "description": "Include colonists", "required": true, "defaultValue": false },
                        { "name": "mapId", "parameterType": "string", "description": "Map identifier", "required": false }
                      ]
                    },
                    {
                      "name": "legacy/get_state",
                      "description": "Legacy state inspection",
                      "inputSchema": {
                        "type": "object",
                        "properties": { "target": { "type": "string", "description": "State target" } },
                        "required": ["target"]
                      }
                    }
                  ]
                }
              }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement state = root.GetProperty("capabilities")
            .EnumerateArray()
            .Single(capability => capability.GetProperty("id").GetString() == "rimworld/get_game_state");
        JsonElement includeColonists = state.GetProperty("parameters")
            .EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "includeColonists");
        AssertEqual("boolean", includeColonists.GetProperty("type").GetString());
        AssertEqual("Include colonists", includeColonists.GetProperty("description").GetString());
        Assert(includeColonists.GetProperty("required").GetBoolean(),
            "Required capability parameters must be marked required.");
        Assert(!includeColonists.GetProperty("default").GetBoolean(),
            "Capability parameter defaults must be preserved.");

        JsonElement legacy = root.GetProperty("capabilities")
            .EnumerateArray()
            .Single(capability => capability.GetProperty("id").GetString() == "legacy/get_state");
        Assert(legacy.GetProperty("parameters")[0].GetProperty("required").GetBoolean(),
            "Legacy inputSchema required parameters must remain authorable.");
    }

    private static void CapabilitiesReportUnavailableBridge()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            { "success": false, "errorCode": "RIMBRIDGE_NOT_READY", "error": "No ready live-game route" }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("rimbridge", root.GetProperty("component").GetString());
        AssertEqual("RIMBRIDGE_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("bridge tools", StringComparison.Ordinal),
            "Unavailable discovery must not hand agents a manual bridge probe.");
    }

    private static void CapabilitiesRejectMalformedResponse()
    {
        (CliResult result, _) = RunCapabilitiesFixture("{\"success\":true");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("RIMBRIDGE_CAPABILITIES_JSON_INVALID", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void CapabilitiesRejectIncompatibleResponse()
    {
        (CliResult result, _) = RunCapabilitiesFixture(
            """
            {
              "schemaVersion": "rimbridge-tools/v2",
              "success": true,
              "rimBridgeRoute": { "success": true, "result": { "tools": [] } }
            }
            """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("incompatibleSchema", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_CAPABILITIES_SCHEMA_UNSUPPORTED", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void CapabilityDiscoveryDoesNotMutateLifecycle()
    {
        (CliResult result, FakeTransport transport) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": { "success": true, "result": { "tools": [] } }
            }
            """);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(1, transport.Requests.Count);
        DevBridgeProcessRequest request = transport.Requests[0];
        AssertSequence(
            ["--root", request.Arguments[1], "bridge", "tools", "--json"],
            request.Arguments);
        Assert(!request.Arguments.Contains("call", StringComparer.OrdinalIgnoreCase),
            "Capability discovery must not expose a generic bridge call.");
        Assert(!request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase),
            "Capability discovery must not begin a lifecycle session.");
    }
    private static void CapabilityDiscoveryForwardsLease()
    {
        const string leaseId = "lease-11111111111111111111111111111111";
        (CliResult result, FakeTransport transport) = RunCapabilitiesFixture(
            """
            {
              "success": true,
              "rimBridgeRoute": { "success": true, "result": { "tools": [] } }
            }
            """,
            "--lease",
            leaseId);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        DevBridgeProcessRequest request = transport.Requests.Single();
        AssertSequence(
            ["--root", request.Arguments[1], "bridge", "tools", "--lease", leaseId, "--json"],
            request.Arguments);
    }

    private static void CapabilityRetryRecoversTransientReadiness()
    {
        int toolsCalls = 0;
        var transport = new FakeTransport(
            (request, _) =>
            {
                if (request.Arguments.Contains("doctor", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"status":"ready"}""");
                }

                return Interlocked.Increment(ref toolsCalls) == 1
                    ? ProcessResult("""{"success":false,"errorCode":"RIMBRIDGE_NOT_READY","error":"not ready"}""")
                    : ProcessResult("""{"success":true,"rimBridgeRoute":{"success":true,"result":{"tools":[]}}}""");
            });
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var store = new AgentObservabilityStore();

        int exitCode = RunCapabilitiesWith(
            transport,
            stdout,
            stderr,
            store);

        AssertEqual(CliExitCodes.Success, exitCode);
        AssertEqual(3, transport.Requests.Count);
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.RetryStarted),
            "Transient recovery must be visible as retry.started.");
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.RecoveryCompleted),
            "Recovery completion must be visible.");
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.RetryCompleted),
            "Retry completion must be visible.");
        Assert(store.GetAgents().Single().Status == AgentStatus.Completed,
            "A successful retry must not leave a terminal failed agent.");
    }

    private static void CapabilityRetryBoundsPersistentReadinessFailure()
    {
        var transport = new FakeTransport(
            (request, _) => request.Arguments.Contains(
                    "doctor",
                    StringComparer.OrdinalIgnoreCase)
                ? ProcessResult("""{"success":false,"errorCode":"DEVBRIDGE_COORDINATOR_NOT_READY","error":"still unavailable"}""")
                : ProcessResult("""{"success":false,"errorCode":"RIMBRIDGE_NOT_READY","error":"not ready"}"""));
        // Exercise the same bounded path with the persistent doctor failure.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using var store = new AgentObservabilityStore();
        int actualExitCode = RunCapabilitiesWith(transport, stdout, stderr, store);
        using JsonDocument document = JsonDocument.Parse(stdout.ToString());
        AssertEqual("RIMBRIDGE_NOT_READY", document.RootElement.GetProperty("code").GetString());
        AssertEqual(CliExitCodes.InternalError, actualExitCode);
        AssertEqual(2, transport.Requests.Count);
        Assert(!transport.Requests.Skip(1).Any(request =>
                request.Arguments.Contains("bridge", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase)),
            "Persistent recovery must not create a second retry storm.");
        Assert(store.GetEvents().Any(value => value.Type == AgentEventTypes.RecoveryCompleted),
            "Persistent recovery must remain observable.");
    }

    private static void CapabilityRecoveryIsSingleFlight()
    {
        int doctorCalls = 0;
        var transport = new FakeTransport(
            (request, _) =>
            {
                if (!request.Arguments.Contains("doctor", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"success":true}""");
                }

                Interlocked.Increment(ref doctorCalls);
                Thread.Sleep(100);
                return ProcessResult("""{"status":"ready"}""");
            });
        string root = CreateTempDirectory();
        try
        {
            var options = new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = root,
                ShowPlanTimeout = TimeSpan.FromSeconds(1)
            };
            Task<DevBridgeCapabilityRecoveryResult>[] tasks = Enumerable
                .Range(0, 8)
                .Select(_ => Task.Run(
                    () => DevBridgeCapabilityRecovery.RecoverAsync(
                        transport,
                        options,
                        "workflow-single-flight")))
                .ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            AssertEqual(1, doctorCalls);
            Assert(tasks.All(task => task.Result.Succeeded),
                "All concurrent waiters must receive the shared recovery result.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(root);
        }
    }

    private static void NestedCapabilityFailurePreservesDevBridgeOwner()
    {
        var issue = new AgentIssue
        {
            Id = "issue-nested",
            RunId = "run-nested",
            AgentId = "agent-nested",
            ModId = "mod.nested",
            Category = AgentIssueCategory.Error,
            Severity = AgentIssueSeverity.Error,
            Summary = "RimLiaison command failed.",
            EventIds = ["outer"]
        };
        AgentEvent[] events =
        [
            new AgentEvent
            {
                Id = "nested",
                RunId = issue.RunId,
                AgentId = issue.AgentId,
                ModId = issue.ModId,
                Type = AgentEventTypes.ToolFailed,
                Summary = "DevBridge capability discovery failed.",
                Sequence = 1,
                Data = JsonSerializer.SerializeToElement(new
                {
                    toolName = "DevBridge",
                    errorCode = "DEVBRIDGE_COORDINATOR_NOT_READY",
                    underlyingErrorCode = "DEVBRIDGE_COORDINATOR_NOT_READY",
                    outerErrorCode = "RIMLIAISON_COMMAND_FAILED"
                })
            },
            new AgentEvent
            {
                Id = "outer",
                RunId = issue.RunId,
                AgentId = issue.AgentId,
                ModId = issue.ModId,
                Type = AgentEventTypes.CommandFailed,
                Summary = "RimLiaison command failed.",
                Sequence = 2,
                Data = JsonSerializer.SerializeToElement(new
                {
                    errorCode = "RIMLIAISON_COMMAND_FAILED",
                    outerErrorCode = "RIMLIAISON_COMMAND_FAILED"
                })
            }
        ];
        AgentObservabilityIssueSignature signature =
            AgentObservabilityIssueTriageBuilder.Describe(issue, events);
        AgentObservabilityProbableOwner owner =
            AgentObservabilityIssueTriageBuilder.Classify(issue, events, signature);
        AssertEqual("DEVBRIDGE_COORDINATOR_NOT_READY", signature.ErrorCode);
        AssertEqual("DevBridge2", owner.Owner);
        Assert(!owner.Owner.Contains("Frontier", StringComparison.OrdinalIgnoreCase),
            "Nested DevBridge failures must not be assigned to Frontier.");
    }

    private static void LocalCapabilityFailurePreservesRimLiaisonOwner()
    {
        var issue = new AgentIssue
        {
            Id = "issue-local",
            RunId = "run-local",
            AgentId = "agent-local",
            ModId = "mod.local",
            Category = AgentIssueCategory.Error,
            Severity = AgentIssueSeverity.Error,
            Summary = "Invalid local capability query.",
            EventIds = ["local"]
        };
        AgentEvent localEvent = new()
        {
            Id = "local",
            RunId = issue.RunId,
            AgentId = issue.AgentId,
            ModId = issue.ModId,
            Type = AgentEventTypes.CommandFailed,
            Summary = issue.Summary,
            Sequence = 1,
            Data = JsonSerializer.SerializeToElement(new
            {
                toolName = "RimLiaison",
                command = "capabilities",
                errorCode = "RIMLIAISON_ARGUMENT_INVALID"
            })
        };
        AgentObservabilityIssueSignature signature =
            AgentObservabilityIssueTriageBuilder.Describe(issue, [localEvent]);
        AgentObservabilityProbableOwner owner =
            AgentObservabilityIssueTriageBuilder.Classify(issue, [localEvent], signature);
        AssertEqual("RimLiaison", owner.Owner);
        Assert(!owner.Owner.Contains("DevBridge", StringComparison.OrdinalIgnoreCase),
            "Local failures must not be assigned to DevBridge.");
    }

    private static int RunCapabilitiesWith(
        FakeTransport transport,
        StringWriter stdout,
        StringWriter stderr,
        IAgentObservabilityStore store)
    {
        string directory = CreateTempDirectory();
        try
        {
            return WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        [
                            "capabilities",
                            "--json",
                            "--devbridge",
                            "DevBridge.cmd",
                            "--devbridge-root",
                            directory
                        ],
                        stdout,
                        stderr,
                        processTransport: transport,
                        observabilityStore: store)
                    .GetAwaiter()
                    .GetResult());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void UiTargetEnumeration()
    {
        const string leaseId = "lease-11111111111111111111111111111111";
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "targets",
            options: ["--lease", leaseId]);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-ui-targets/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual(2, root.GetProperty("count").GetInt32());
        JsonElement target = root.GetProperty("targets")
            .EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == "window:main");
        AssertEqual("window", target.GetProperty("kind").GetString());
        AssertEqual("Main window", target.GetProperty("label").GetString());
        AssertEqual(2, target.GetProperty("rect").GetProperty("width").GetInt32());
        AssertEqual(2, transport.Requests.Count);
        Assert(transport.Requests[0].Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase),
            "Target enumeration must discover registered tools.");
        Assert(transport.Requests[1].Arguments.Any(argument =>
                argument.Contains("get_screen_targets", StringComparison.OrdinalIgnoreCase)),
            "Target enumeration must call the registered screen-target capability.");
        Assert(transport.Requests.All(request =>
                request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains(leaseId, StringComparer.Ordinal)),
            "UI target discovery must preserve the caller lease for capability discovery and invocation.");
    }

    private static void UiTargetObjectSchemaIsSupported()
    {
        (CliResult result, _) = RunUiFixture(
            "targets",
            targetResponse: RouteResponse(
                """
                {
                  "success": true,
                  "targets": {
                    "windows": [
                      {
                        "windowTargetId": "window:dialog",
                        "kind": "window",
                        "title": "Dialog",
                        "rect": { "x": 1, "y": 2, "width": 3, "height": 4 }
                      }
                    ]
                  }
                }
                """,
                "op-targets-object"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(1, root.GetProperty("count").GetInt32());
        JsonElement target = root.GetProperty("targets").EnumerateArray().Single();
        AssertEqual("window:dialog", target.GetProperty("id").GetString());
        AssertEqual("Dialog", target.GetProperty("label").GetString());
    }

    private static void UiTargetDiscoveryRecoversRequiredLease()
    {
        string directory = CreateTempDirectory();
        try
        {
            int toolsCalls = 0;
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            "{\"success\":true,\"exitCode\":0,\"leaseId\":\"lease-targets\",\"generation\":1}");
                    }

                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult("{\"success\":true,\"exitCode\":0}");
                    }

                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        toolsCalls++;
                        return toolsCalls == 1
                            ? ProcessResult(
                                "{\"success\":false,\"errorCode\":\"RIMBRIDGE_LEASE_REQUIRED\",\"error\":\"lease required\"}")
                            : ProcessResult(UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiTargetsCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected target-discovery request: " +
                        string.Join(" ", request.Arguments));
                });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        [
                            "ui",
                            "targets",
                            "--json",
                            "--devbridge",
                            "DevBridge.cmd",
                            "--devbridge-root",
                            directory
                        ],
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("ok", root.GetProperty("status").GetString());
            AssertEqual(2, root.GetProperty("count").GetInt32());
            AssertEqual(2, toolsCalls);
            Assert(transport.Requests.Any(request =>
                    request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase)),
                "Lease-required target discovery must acquire a lease.");
            Assert(transport.Requests.Any(request =>
                    request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase)),
                "Lease-required target discovery must release its lease.");
            Assert(transport.Requests
                .Where(request => request.Arguments.Contains(
                    "rimworld/get_screen_targets",
                    StringComparer.OrdinalIgnoreCase))
                .All(request => request.Arguments.Contains("lease-targets", StringComparer.Ordinal)),
                "The retried target call must carry the acquired lease.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void UiBridgeCallsCarryWorkflowIdentity()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "current"]);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        DevBridgeProcessRequest[] bridgeCalls = transport.Requests
            .Where(request => request.Arguments.Contains(
                "call",
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        DevBridgeProcessRequest[] routedUiRequests = transport.Requests
            .Where(request => request.Arguments.Contains(
                    "tools",
                    StringComparer.OrdinalIgnoreCase) ||
                request.Arguments.Contains("call", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert(bridgeCalls.Length > 0 &&
            bridgeCalls.All(request =>
                request.EnvironmentVariables is not null &&
                request.EnvironmentVariables.TryGetValue(
                    "DEVBRIDGE_AGENT",
                out string? agent) &&
                !string.IsNullOrWhiteSpace(agent)),
            "All UI bridge calls must carry the canonical workflow owner identity.");
        Assert(bridgeCalls.All(request =>
                request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("lease-1", StringComparer.Ordinal)),
            "Transactional UI bridge calls must carry the acquired lease identity.");
        Assert(routedUiRequests.All(request =>
                request.Arguments.Contains("--lease", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("lease-1", StringComparer.Ordinal)),
            "Transactional UI capability discovery and calls must share the acquired lease identity.");
    }

    private static void UiTargetedScreenshotUsesClipping()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("rimtest-ui-screenshot/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("captured", root.GetProperty("captureStatus").GetString());
        AssertEqual("window:main", root.GetProperty("targetId").GetString());
        AssertEqual("window", root.GetProperty("targetKind").GetString());
        AssertEqual("Main window", root.GetProperty("targetLabel").GetString());
        AssertEqual("/evidence/main.png", root.GetProperty("path").GetString());
        AssertEqual("op-target-shot", root.GetProperty("operationId").GetString());
        AssertEqual(4, transport.Requests.Count);
        DevBridgeProcessRequest screenshotRequest = transport.Requests
            .Single(request => request.Arguments.Contains(
                "rimworld/take_screenshot",
                StringComparer.OrdinalIgnoreCase));
        using JsonDocument arguments = JsonDocument.Parse(screenshotRequest.Arguments[5]);
        JsonElement screenshotArguments = arguments.RootElement;
        AssertEqual("window:main", screenshotArguments.GetProperty("targetId").GetString());
        Assert(screenshotArguments.GetProperty("waitForVisualReady").GetBoolean(),
            "Targeted captures must wait for visual readiness.");
        Assert(!screenshotArguments.GetProperty("doNotResetCamera").GetBoolean(),
            "Targeted captures must preserve camera restoration policy.");
        Assert(screenshotArguments.GetProperty("includeScreenTargets").GetBoolean(),
            "Targeted captures must use RimBridge target clipping.");
        Assert(!screenshotRequest.Arguments.Contains(
            "rimworld/get_screenshot",
            StringComparer.OrdinalIgnoreCase),
            "RimLiaison must not substitute an unrestricted full-screen capture.");
    }

    private static void UiMissingTargetFailsBeforeCapture()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:missing"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.NotFound, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("targetNotFound", root.GetProperty("outcome").GetString());
        AssertEqual("RIMTEST_UI_TARGET_NOT_FOUND", root.GetProperty("code").GetString());
        Assert(!transport.Requests.Any(request => request.Arguments.Contains(
            "rimworld/take_screenshot",
            StringComparer.OrdinalIgnoreCase)),
            "A missing target must fail before the screenshot operation.");
    }

    private static void UiReportsUnavailableBridge()
    {
        (CliResult result, _) = RunUiFixture(
            "targets",
            toolsResponse: """
                { "success": false, "errorCode": "RIMBRIDGE_NOT_READY", "error": "No live route" }
                """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("unavailable", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("bridge tools", StringComparison.OrdinalIgnoreCase),
            "Unavailable UI discovery must return the RimLiaison owner handoff.");
    }

    private static void UiReportsVisualReadinessFailure()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"],
            targetScreenshotResponse: RouteResponse(
                """
                { "success": false, "errorCode": "RIMBRIDGE_VISUAL_NOT_READY", "error": "Renderer is not ready" }
                """,
                "op-not-ready"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("visualReadinessFailure", root.GetProperty("outcome").GetString());
        AssertEqual("RIMBRIDGE_VISUAL_NOT_READY", root.GetProperty("code").GetString());
        AssertEqual("op-not-ready", root.GetProperty("operationId").GetString());
    }

    private static void UiCellCapturePreservesCamera()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--cell-rect", "10,20,3,4"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ok", root.GetProperty("status").GetString());
        AssertEqual("/evidence/cell.png", root.GetProperty("path").GetString());
        Assert(root.GetProperty("cameraRestored").GetBoolean(),
            "Cell capture must preserve RimBridgeServer camera restoration.");
        AssertEqual(10, root.GetProperty("requestedRect").GetProperty("x").GetInt32());
        AssertEqual(4, root.GetProperty("requestedRect").GetProperty("height").GetInt32());
        DevBridgeProcessRequest cellRequest = transport.Requests
            .Single(request => request.Arguments.Contains(
                "rimworld/screenshot_cell_rect",
                StringComparer.OrdinalIgnoreCase));
        using JsonDocument arguments = JsonDocument.Parse(cellRequest.Arguments[5]);
        JsonElement captureArguments = arguments.RootElement;
        AssertEqual(10, captureArguments.GetProperty("x").GetInt32());
        AssertEqual(20, captureArguments.GetProperty("z").GetInt32());
        AssertEqual(3, captureArguments.GetProperty("width").GetInt32());
        AssertEqual(4, captureArguments.GetProperty("height").GetInt32());
        Assert(!captureArguments.GetProperty("doNotResetCamera").GetBoolean(),
            "Cell capture must request camera restoration.");
    }

    private static void UiRequestsDoNotMutateLifecycle()
    {
        (CliResult result, FakeTransport transport) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        string[] lifecycleTerms =
        [
            "begin",
            "start",
            "restart",
            "kill",
            "lease",
            "lifecycle",
            "generation"
        ];
        Assert(!transport.Requests
            .SelectMany(request => request.Arguments)
            .Any(argument => lifecycleTerms.Any(term =>
                argument.Contains(term, StringComparison.OrdinalIgnoreCase))),
            "UI discovery/capture must not acquire leases or mutate lifecycle state.");
    }

    private static void TransactionalUiViewportCapturesAndRestores()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("ok", root.GetProperty("status").GetString());
        JsonElement viewport = root.GetProperty("viewport");
        JsonElement preparation = viewport.GetProperty("preparation");
        JsonElement restoration = viewport.GetProperty("restoration");
        AssertEqual("prepared", preparation.GetProperty("status").GetString());
        AssertEqual("restored", restoration.GetProperty("status").GetString());
        AssertEqual(1024, preparation.GetProperty("effectiveViewport")
            .GetProperty("clientWidth").GetInt32());
        AssertEqual(768, preparation.GetProperty("effectiveViewport")
            .GetProperty("clientHeight").GetInt32());
        Assert(!preparation.GetProperty("persistentPreferenceMutation").GetBoolean(),
            "temporary viewport evidence must prove no persistent preference mutation");
        Assert(restoration.GetProperty("restorationVerified").GetBoolean(),
            "temporary viewport evidence must prove restoration");

        int beginIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase));
        int restoreIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase));
        int endIndex = transport.Requests.FindIndex(request =>
            request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
            request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase));
        Assert(beginIndex >= 0 && restoreIndex > beginIndex && endIndex > restoreIndex,
            "transactional UI validation must restore before releasing its temporary lease");
    }

    private static void TransactionalUiViewportSurfacesRestorationFailure()
    {
        (CliResult result, _) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"],
            restoreResponse: """
                {"success":false,"exitCode":4,"viewport":{"schemaVersion":"devbridge-viewport-environment/v1","success":false,"status":"cleanupFailed","errorCode":"VIEWPORT_RESTORE_FAILED","error":"The original window state could not be verified.","transactionId":"viewport-1","leaseId":"lease-1","persistentPreferenceMutation":false,"restorationVerified":false,"cleanupStatus":"restore-required"}}
                """);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("error", root.GetProperty("status").GetString());
        AssertEqual("RIMTEST_UI_VIEWPORT_RESTORE_FAILED", root.GetProperty("code").GetString());
        AssertEqual("/evidence/main.png", root.GetProperty("screenshotEvidence")
            .GetProperty("path").GetString());
        AssertEqual("restorationFailure", root.GetProperty("viewportRestoration")
            .GetProperty("outcome").GetString());
        Assert(!root.GetProperty("viewportRestoration")
            .GetProperty("restorationVerified").GetBoolean(),
            "restoration failure must remain explicit in machine-readable evidence");
    }

    private static void TransactionalUiViewportRestoresAfterUiFailure()
    {
        (CliResult result, FakeTransport transport) = RunTransactionalUiFixture(
            ["--target", "window:main", "--viewport", "narrow"],
            screenshotResponse: RouteResponse(
                "{\"success\":false,\"errorCode\":\"RIMBRIDGE_UI_ASSERTION_FAILED\",\"error\":\"layout assertion failed\"}",
                "op-ui-failure"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("RIMBRIDGE_UI_ASSERTION_FAILED", root.GetProperty("code").GetString());
        AssertEqual("restored", root.GetProperty("viewportRestoration")
            .GetProperty("status").GetString());
        Assert(transport.Requests.Any(request =>
                request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase)),
            "viewport cleanup must run after a UI operation failure");
    }

    private static void TransactionalUiViewportValidatesExplicitDimensions()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main", "--viewport", "explicit",
                "--viewport-width", "319", "--viewport-height", "720"]);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InvalidInput, result.ExitCode);
        AssertEqual("CLI_INVALID", root.GetProperty("code").GetString());
        Assert(root.GetProperty("errors").EnumerateArray().Single()
            .GetProperty("message").GetString()!.Contains("viewport-width", StringComparison.OrdinalIgnoreCase),
            "explicit viewport bounds must be validated before any DevBridge call");
    }

    private static void UiOutputIsCompact()
    {
        (CliResult result, _) = RunUiFixture(
            "screenshot",
            ["--target", "window:main"]);

        Assert(result.Stdout.Length < 1200,
            "UI screenshot output should remain compact evidence metadata.");
        Assert(!result.Stdout.Contains("cameraBefore", StringComparison.OrdinalIgnoreCase),
            "RimLiaison must not dump camera diagnostics into compact output.");
        Assert(!result.Stdout.Contains("cameraDuringCapture", StringComparison.OrdinalIgnoreCase),
            "RimLiaison must not dump the full bridge payload.");
        Assert(!result.Stdout.Contains("sourcePath", StringComparison.OrdinalIgnoreCase),
            "UI output should expose the selected screenshot path once.");
    }

    private static void CanonicalUiGuidanceIsGenerated()
    {
        string directory = CreateTempDirectory();
        try
        {
            CliResult result = RunInitFixture(directory);
            AssertEqual(CliExitCodes.Success, result.ExitCode);
            string agents = File.ReadAllText(Path.Combine(directory, "AGENTS.md"));
            Assert(agents.Contains(
                "functional tests alone are insufficient",
                StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must require visual inspection for UI work.");
            Assert(agents.Contains("rimliaison ui targets", StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must point agents to target discovery.");
            Assert(agents.Contains("rimliaison ui screenshot", StringComparison.OrdinalIgnoreCase),
                "Canonical AGENTS guidance must point agents to selective screenshots.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DoctorHealthyOutputIsCompact()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("rimtest-doctor/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("ready", root.GetProperty("status").GetString());
        AssertEqual("ok", root.GetProperty("catalog").GetString());
        AssertEqual("ok", root.GetProperty("rimctx").GetString());
        AssertEqual("ok", root.GetProperty("devbridge").GetString());
        AssertEqual("ok", root.GetProperty("rimerror").GetString());
        AssertEqual("configured", root.GetProperty("rimbridge").GetString());
        AssertEqual("rimliaison affected --run --json", root.GetProperty("nextAction").GetString());
        Assert(!result.Stdout.Contains("findings", StringComparison.Ordinal),
            "Doctor must not copy the DevBridge doctor transcript.");
        Assert(string.IsNullOrEmpty(result.Stderr),
            "Healthy doctor output should not write diagnostics.");
    }
    private static void DoctorPreservesStructuredDevBridgeFailure()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            structuredFailure: true);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("OUTPUT_TOO_LARGE", root.GetProperty("code").GetString());
        AssertEqual("ERROR", root.GetProperty("state").GetString());
        AssertEqual(2, root.GetProperty("exitCode").GetInt32());
        Assert(
            root.GetProperty("error").GetString()!.Contains(
                "maximum payload",
                StringComparison.OrdinalIgnoreCase),
            "doctor must preserve the lower-level error message");
        Assert(!result.Stdout.Contains("DEVBRIDGE_RESPONSE_INVALID", StringComparison.Ordinal),
            "a valid structured failure must not be classified as invalid");
        Assert(result.Stderr.Contains("devbridge OUTPUT_TOO_LARGE", StringComparison.Ordinal),
            "doctor diagnostics must identify the originating component and code");
    }

    private static void ModDevelopmentSourceRootIgnoresWhitespaceOverride()
    {
        string directory = CreateTempDirectory();
        string? previousSourceRoot = Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT");
        string? previousBridgeRoot = Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", " ");
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", " ");
            DevBridgeModDevelopmentAdapterOptions options =
                DevBridgeModDevelopmentAdapterOptions.Discover(directory);

            AssertEqual(Path.GetFullPath(directory), options.ScriptRootPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", previousSourceRoot);
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", previousBridgeRoot);
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DevBridgeRootSelectsBatchLauncher()
    {
        string directory = CreateTempDirectory();
        try
        {
            string expected = Path.GetFullPath(Path.Combine(directory, "DevBridge.cmd"));
            DevBridgeAdapterOptions options = DevBridgeAdapterOptions.Discover(
                rootPath: directory);

            AssertEqual(expected, options.CommandPath);
            AssertEqual(Path.GetFullPath(directory), options.RootPath);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DevBridgeBatchLauncherExecutesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = CreateTempDirectory();
        try
        {
            string script = Path.Combine(directory, "DevBridge.cmd");
            File.WriteAllText(script, "@echo off\r\n" +
                "echo {\"success\":true,\"args\":\"%*\"}\r\n");
            DevBridgeProcessResult process =
                new SystemDevBridgeProcessTransport().ExecuteAsync(
                        new DevBridgeProcessRequest(
                            script,
                            directory,
                            ["--root", directory, "doctor", "--json"],
                            TimeSpan.FromSeconds(5),
                            16 * 1024,
                            16 * 1024),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            AssertEqual(0, process.ExitCode);
            Assert(process.StartError is null, "batch launcher must not report a start error");
            Assert(process.Stdout.Contains("--root", StringComparison.Ordinal) &&
                process.Stdout.Contains("doctor", StringComparison.Ordinal),
                "batch launcher must preserve script arguments");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DevBridgeProcessEvidenceIsRetained()
    {
        string directory = CreateTempDirectory();
        try
        {
            string script = Path.Combine(directory, "DevBridge.cmd");
            File.WriteAllText(
                script,
                "@echo off\r\n" +
                "echo {\"success\":false,\"exitCode\":2,\"state\":\"ERROR\",\"errorCode\":\"OUTPUT_TOO_LARGE\",\"error\":\"The coordinator result exceeded the maximum payload length.\"}\r\n" +
                "echo stderr evidence 1>&2\r\n" +
                "exit /b 2\r\n");
            using var store = new AgentObservabilityStore();
            using var run = new AgentObservabilityRun(
                "run-process-evidence",
                store,
                new NoopAgentObservabilityTelemetry());
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.process-evidence",
                "Process Evidence");
            agent.Start();
            using IDisposable activation = agent.Activate();
            DevBridgeProcessResult process =
                new SystemDevBridgeProcessTransport().ExecuteAsync(
                        new DevBridgeProcessRequest(
                            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                            directory,
                            ["/d", "/c", script, "--root", directory, "doctor", "--json"],
                            TimeSpan.FromSeconds(5),
                            16 * 1024,
                            16 * 1024,
                            OperationKey: "devbridge:doctor"),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            AssertEqual("OUTPUT_TOO_LARGE", process.Response?.ErrorCode);
            Assert(process.Evidence?.StdoutEvidenceId is not null,
                "stdout evidence id must be retained");
            Assert(process.Evidence?.StderrEvidenceId is not null,
                "stderr evidence id must be retained");
            AgentIssue issue = store.GetIssues(agentId: agent.AgentId).Single();
            AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([issue.Id]);
            AgentDiagnosticCommandEvidence command = bundle.CommandEvidence.Single(
                value => value.Stdout?.Contains("OUTPUT_TOO_LARGE", StringComparison.Ordinal) == true);
            Assert(command.Stdout?.Contains("OUTPUT_TOO_LARGE", StringComparison.Ordinal) == true,
                "diagnostic bundle must retain structured stdout");
            Assert(command.Stderr?.Contains("stderr evidence", StringComparison.Ordinal) == true,
                "diagnostic bundle must retain stderr");
            AssertEqual(2, command.ExitCode);
            string bundleJson = JsonSerializer.Serialize(
                bundle,
                AgentObservabilityJson.Options);
            Assert(bundleJson.Contains("DevBridge", StringComparison.Ordinal) &&
                bundleJson.Contains("OUTPUT_TOO_LARGE", StringComparison.Ordinal) &&
                bundleJson.Contains("\"exitCode\":2", StringComparison.Ordinal),
                "serialized diagnostic bundle must expose the originating component, code, and exit code");
            Assert(bundleJson.Contains(directory.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal),
                "serialized diagnostic bundle must expose the DevBridge root");
            Assert(bundleJson.Contains("resolvedExecutablePath", StringComparison.Ordinal) &&
                bundleJson.Contains("workingDirectory", StringComparison.Ordinal) &&
                bundleJson.Contains("\"timedOut\":false", StringComparison.Ordinal) &&
                bundleJson.Contains("\"cancelled\":false", StringComparison.Ordinal),
                "serialized diagnostic bundle must expose process metadata and terminal state");
            Assert(!bundle.Completeness.MissingEvidence.Contains("command.output"),
                "persisted process output must satisfy command.output completeness");
            AgentEvent processEvent = bundle.SupportingEvents.Single(
                eventRecord => eventRecord.Type == AgentEventTypes.ToolFailed);
            Assert(AgentObservabilityData.GetString(
                    processEvent.Data,
                    "resolvedExecutablePath") is not null,
                "process evidence must retain the resolved executable path");
            AssertEqual(directory, AgentObservabilityData.GetString(
                processEvent.Data,
                "resolvedToolRoot"));
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }


    private static void DoctorPreservesIdentityMismatchDetails()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            identityMismatch: true);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("READINESS_IDENTITY_MISMATCH", root.GetProperty("code").GetString());
        JsonElement mismatch = root.GetProperty("identityMismatch");
        AssertEqual("runtimeGeneration", mismatch.GetProperty("field").GetString());
        Assert(mismatch.GetProperty("recoverable").GetBoolean(),
            "Doctor must preserve recoverability for generation churn.");
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void DoctorReportsBlockedComponent()
    {
        CliResult result = RunDoctorFixture(contextAvailable: false);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("rimtest-doctor/v1", root.GetProperty("schemaVersion").GetString());
        AssertEqual("blocked", root.GetProperty("status").GetString());
        AssertEqual("rimctx", root.GetProperty("component").GetString());
        AssertEqual("INDEX_MISSING", root.GetProperty("code").GetString());
        Assert(root.GetProperty("workspaceIntegrity").GetProperty("schemaVersion").GetString() ==
            "rimliaison-workspace-integrity/v1", "blocked doctor output must retain workspace health evidence");
        AssertEqual("rimliaison affected --run --json", root.GetProperty("nextAction").GetString());
        Assert(result.Stderr.Contains("rimctx INDEX_MISSING", StringComparison.Ordinal),
            "Blocked doctor diagnostics should identify the component and code.");
    }

    private static void DoctorReadsDevBridgeRimBridgeStatusShape()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            usePascalRimBridgeFields: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("configured", document.RootElement.GetProperty("rimbridge").GetString());
    }

    private static void StackManifestDefaultsAreUsed()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("FixtureMod", document.RootElement.GetProperty("project").GetString());
    }

    private static void ExplicitCliOverridesBeatManifest()
    {
        CliResult result = RunDoctorFixture(
            contextAvailable: true,
            useExplicitOverrides: true);

        AssertEqual(CliExitCodes.Success, result.ExitCode);
        Assert(!result.Stdout.Contains("override", StringComparison.Ordinal),
            "Local alias configuration must not leak into doctor output.");
    }

    private static void MalformedStackSchemaIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor("{\"schemaVersion\":");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        AssertEqual(
            "{\"schemaVersion\":\"rimtest-doctor/v1\",\"status\":\"blocked\",\"component\":\"manifest\",\"code\":\"STACK_MANIFEST_JSON_INVALID\",\"nextAction\":\"rimliaison init --json --manifest-only --force\"}",
            result.Stdout.Trim());
    }

    private static void UnknownStackSchemaIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor(
            "{\"schemaVersion\":\"rimdev-stack/v99\",\"project\":\"Fixture\",\"catalog\":\"catalog.json\",\"rimBridge\":\"via-devbridge\"}");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        AssertEqual(
            "STACK_MANIFEST_SCHEMA_UNSUPPORTED",
            JsonDocument.Parse(result.Stdout).RootElement.GetProperty("code").GetString());
    }

    private static void MissingStackManifestIsBlocked()
    {
        CliResult result = RunManifestOnlyDoctor(manifest: null);

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("STACK_MANIFEST_MISSING", document.RootElement.GetProperty("code").GetString());
        AssertEqual("rimliaison init --json", document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void LocalConfigurationDoesNotLeak()
    {
        CliResult result = RunDoctorFixture(contextAvailable: true);

        Assert(!result.Stdout.Contains("\"fixture\"", StringComparison.Ordinal),
            "Doctor output must not expose the DevBridge alias.");
        Assert(!result.Stdout.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "Doctor output must not expose machine-local paths.");
    }

    private static void InitCreatesEmptyRepositoryHandoff()
    {
        string directory = CreateTempDirectory();
        try
        {
            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            Assert(File.Exists(Path.Combine(directory, ".rimdev", "stack.json")),
                $"init must create the stack manifest. stdout={result.Stdout} stderr={result.Stderr}");
            Assert(File.Exists(Path.Combine(directory, "AGENTS.md")),
                "init must create the canonical AGENTS template.");
            using JsonDocument manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("rimdev-stack/v1", manifest.RootElement.GetProperty("schemaVersion").GetString());
            AssertEqual("TestCatalog/rimtest.catalog.json", manifest.RootElement.GetProperty("catalog").GetString());
            AssertEqual("via-devbridge", manifest.RootElement.GetProperty("rimBridge").GetString());
            Assert(!manifest.RootElement.TryGetProperty("devBridgeProject", out _),
                "init must not guess a DevBridge alias.");
            Assert(!result.Stdout.Contains(directory, StringComparison.OrdinalIgnoreCase),
                "init output must use repository-relative paths.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitFillsMissingManifestFieldSafely()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(
                Path.Combine(directory, "catalog.json"),
                Serialize(CreateCatalog()));
            string manifestPath = Path.Combine(directory, ".rimdev", "stack.json");
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"custom\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            AssertEqual("smoke", document.RootElement.GetProperty("fallbackSuite").GetString());
            AssertEqual("Custom", document.RootElement.GetProperty("project").GetString());
            AssertEqual("custom", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("catalog.json", document.RootElement.GetProperty("catalog").GetString());
            AssertEqual("disabled", document.RootElement.GetProperty("rimBridge").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitMergesExplicitConfigurationSafely()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(
                Path.Combine(directory, "catalog.json"),
                Serialize(CreateCatalog()));
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "target-specific instructions\n");
            string manifestPath = Path.Combine(directory, ".rimdev", "stack.json");
            File.WriteAllText(
                manifestPath,
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"catalog\":\"catalog.json\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(
                directory,
                "--devbridge-project",
                "custom-project",
                "--fallback-suite",
                "smoke");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("target-specific instructions\n", File.ReadAllText(agentsPath));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            AssertEqual("custom-project", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("smoke", document.RootElement.GetProperty("fallbackSuite").GetString());
            AssertEqual("Custom", document.RootElement.GetProperty("project").GetString());
            AssertEqual("catalog.json", document.RootElement.GetProperty("catalog").GetString());
            AssertEqual("disabled", document.RootElement.GetProperty("rimBridge").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitPreservesExistingAgents()
    {
        string directory = CreateTempDirectory();
        try
        {
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "custom instructions\n");
            CliResult result = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("custom instructions\n", File.ReadAllText(agentsPath));
            Assert(result.Stdout.Contains("AGENTS.md", StringComparison.Ordinal),
                "init should report the existing AGENTS file.");
            Assert(result.Stdout.Contains("existing", StringComparison.Ordinal),
                "init should not overwrite an existing AGENTS file.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitPreservesExistingManifest()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rimDevDirectory = Path.Combine(directory, ".rimdev");
            Directory.CreateDirectory(rimDevDirectory);
            string manifestPath = Path.Combine(rimDevDirectory, "stack.json");
            string existing = "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"custom\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"disabled\"}\n";
            File.WriteAllText(manifestPath, existing);

            CliResult result = RunInitFixture(
                directory,
                "--devbridge-project",
                "new-project",
                "--fallback-suite",
                "new-suite");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual(existing, File.ReadAllText(manifestPath));
            Assert(result.Stdout.Contains("stack.json", StringComparison.Ordinal),
                "init should report the existing manifest.");
            Assert(result.Stdout.Contains("existing", StringComparison.Ordinal),
                "init should not overwrite an existing manifest.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitIsIdempotent()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "TestCatalog"));
            File.WriteAllText(
                Path.Combine(directory, "TestCatalog", "rimtest.catalog.json"),
                Serialize(CreateCatalog()));

            CliResult first = RunInitFixture(directory);
            string manifest = File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json"));
            string agents = File.ReadAllText(Path.Combine(directory, "AGENTS.md"));
            CliResult second = RunInitFixture(directory);

            AssertEqual(CliExitCodes.Success, first.ExitCode);
            AssertEqual(CliExitCodes.Success, second.ExitCode);
            AssertEqual(manifest, File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual(agents, File.ReadAllText(Path.Combine(directory, "AGENTS.md")));
            Assert(second.Stdout.Contains("\"status\":\"existing\"", StringComparison.Ordinal),
                "Repeated init should report existing files without rewriting them.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void InitForceBehaviorIsIntentional()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "replace me\n");
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "stack.json"),
                "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Custom\",\"devBridgeProject\":\"old-project\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"disabled\"}");

            CliResult result = RunInitFixture(
                directory,
                "--force",
                "--devbridge-project",
                "new-project",
                "--fallback-suite",
                "settings");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            Assert(!string.Equals(
                    "replace me\n",
                    File.ReadAllText(Path.Combine(directory, "AGENTS.md")),
                    StringComparison.Ordinal),
                "--force must retain its intentional AGENTS overwrite behavior.");
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("new-project", document.RootElement.GetProperty("devBridgeProject").GetString());
            AssertEqual("settings", document.RootElement.GetProperty("fallbackSuite").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ManifestOnlyRepairPreservesAgents()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            string agentsPath = Path.Combine(directory, "AGENTS.md");
            File.WriteAllText(agentsPath, "keep this handoff\n");
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "stack.json"),
                "{\"schemaVersion\":");

            CliResult result = RunInitFixture(
                directory,
                "--manifest-only",
                "--force");

            AssertEqual(CliExitCodes.Success, result.ExitCode);
            AssertEqual("keep this handoff\n", File.ReadAllText(agentsPath));
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(directory, ".rimdev", "stack.json")));
            AssertEqual("rimdev-stack/v1", document.RootElement.GetProperty("schemaVersion").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void DoctorMissingProjectProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}",
            Serialize(CreateCatalog()));

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(
            "STACK_MANIFEST_DEVBRIDGE_PROJECT_MISSING",
            document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --devbridge-project <project>",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorMissingFallbackProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"rimBridge\":\"via-devbridge\"}",
            Serialize(CreateCatalog()));

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual(
            "STACK_MANIFEST_FALLBACK_SUITE_MISSING",
            document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --fallback-suite smoke",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorMissingCatalogProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctor(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("CATALOG_NOT_FOUND", document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison init --json --manifest-only --force --catalog catalog.json",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void DoctorInvalidCatalogProvidesHandoff()
    {
        CliResult result = RunManifestOnlyDoctorWithCatalog(
            "{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"Fixture\",\"devBridgeProject\":\"fixture\",\"catalog\":\"catalog.json\",\"fallbackSuite\":\"smoke\",\"rimBridge\":\"via-devbridge\"}",
            "{\"schemaVersion\":");

        AssertEqual(CliExitCodes.ConservativeSelection, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        AssertEqual("CATALOG_JSON_INVALID", document.RootElement.GetProperty("code").GetString());
        AssertEqual(
            "rimliaison validate --json --catalog catalog.json",
            document.RootElement.GetProperty("nextAction").GetString());
    }

    private static void AffectedDiscoversGitChangesWithoutPaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                true,
                ["Source/Staged.cs", "Source/New.cs"]));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--base", "origin/main", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(1, git.Calls.Count);
            AssertEqual("origin/main", git.Calls[0].Base);
            AssertSequence(
                ["Source/Staged.cs", "Source/New.cs"],
                impactAdapter.ChangedPaths);
            Assert(stdout.ToString().Contains(
                "\"status\":\"ok\"",
                StringComparison.Ordinal),
                "Automatic Git changes should feed RimContext selection.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void GitDiscoveryIncludesStagedAndUntrackedFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            Directory.CreateDirectory(Path.Combine(directory, "bin"));
            RunGit(directory, "init", "--quiet");
            RunGit(directory, "config", "user.email", "RimLiaison@example.invalid");
            RunGit(directory, "config", "user.name", "RimLiaison");
            string tracked = Path.Combine(directory, "Source", "Tracked.cs");
            File.WriteAllText(tracked, "class Tracked {}\n");
            RunGit(directory, "add", "Source/Tracked.cs");
            RunGit(directory, "commit", "--quiet", "-m", "initial");
            File.WriteAllText(tracked, "class Tracked { int Value; }\n");
            File.WriteAllText(Path.Combine(directory, "Source", "Staged.cs"), "class Staged {}\n");
            RunGit(directory, "add", "Source/Staged.cs");
            File.WriteAllText(Path.Combine(directory, "Source", "Untracked.cs"), "class Untracked {}\n");
            File.WriteAllText(Path.Combine(directory, "bin", "Generated.cs"), "generated\n");

            GitChangeDiscoveryResult result = new SystemGitChangeProvider()
                .DiscoverAsync(directory)
                .GetAwaiter()
                .GetResult();

            Assert(result.Resolved, result.Error ?? "Git discovery should resolve.");
            Assert(result.Paths.Contains("Source/Tracked.cs"), "Tracked modification was not discovered.");
            Assert(result.Paths.Contains("Source/Staged.cs"), "Staged file was not discovered.");
            Assert(result.Paths.Contains("Source/Untracked.cs"), "Untracked file was not discovered.");
            Assert(!result.Paths.Any(path => path.StartsWith("bin/", StringComparison.Ordinal)),
                "Generated build directories must be excluded.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void GitDiscoveryPreservesDeletedAndRenamedPaths()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            RunGit(directory, "init", "--quiet");
            RunGit(directory, "config", "user.email", "RimLiaison@example.invalid");
            RunGit(directory, "config", "user.name", "RimLiaison");
            File.WriteAllText(Path.Combine(directory, "Source", "Old.cs"), "old\n");
            File.WriteAllText(Path.Combine(directory, "Source", "Deleted.cs"), "deleted\n");
            RunGit(directory, "add", "Source/Old.cs", "Source/Deleted.cs");
            RunGit(directory, "commit", "--quiet", "-m", "initial");
            RunGit(directory, "mv", "Source/Old.cs", "Source/New.cs");
            RunGit(directory, "rm", "Source/Deleted.cs");

            GitChangeDiscoveryResult result = new SystemGitChangeProvider()
                .DiscoverAsync(directory)
                .GetAwaiter()
                .GetResult();

            Assert(result.Resolved, result.Error ?? "Git discovery should resolve.");
            Assert(result.Paths.Contains("Source/Old.cs"),
                "The rename source must remain in the changed path set.");
            Assert(result.Paths.Contains("Source/New.cs"),
                "The rename destination must remain in the changed path set.");
            Assert(result.Paths.Contains("Source/Deleted.cs"),
                "Deleted paths must remain in the changed path set.");
            GitChangedPath rename = result.Changes.Single(change => change.IsRenamed);
            AssertEqual("Source/New.cs", rename.Path);
            AssertEqual("Source/Old.cs", rename.OriginalPath);
            Assert(result.Changes.Any(change =>
                    change.IsDeleted && change.Path == "Source/Deleted.cs"),
                "Git discovery must retain deletion status.");

            GitChangeDiscoveryResult baseResult = new SystemGitChangeProvider()
                .DiscoverAsync(directory, "HEAD")
                .GetAwaiter()
                .GetResult();
            Assert(baseResult.Resolved, baseResult.Error ?? "Git base discovery should resolve.");
            Assert(baseResult.Paths.Contains("Source/Old.cs"),
                "Base diff discovery must retain the rename source.");
            Assert(baseResult.Paths.Contains("Source/New.cs"),
                "Base diff discovery must retain the rename destination.");
            Assert(baseResult.Paths.Contains("Source/Deleted.cs"),
                "Base diff discovery must retain deleted paths.");
            Assert(baseResult.Changes.Any(change =>
                    change.IsRenamed &&
                    change.Path == "Source/New.cs" &&
                    change.OriginalPath == "Source/Old.cs"),
                "Base diff discovery must preserve rename source/destination.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void CleanAffectedRunIsExplicitAndDoesNotLaunch()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var adapter = new FakeRecipeAdapter();
            var developmentAdapter = new FakeModDevelopmentAdapter();
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(true, []));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--run", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    adapter,
                    gitChangeProvider: git,
                    developmentAdapter: developmentAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual("ok", root.GetProperty("status").GetString());
            AssertEqual(0, root.GetProperty("tests").GetArrayLength());
            AssertEqual(0, adapter.RunCalls.Count);
            AssertEqual(0, developmentAdapter.Calls.Count);
            Assert(string.IsNullOrEmpty(stderr.ToString()),
                "A clean affected run should not write diagnostics.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AffectedSourceRunPerformsFreshnessTransaction()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual(1, root.GetProperty("passed").GetInt32());
        AssertEqual("deployed", freshness.GetProperty("deploymentDecision").GetString());
        AssertEqual("FRESH", freshness.GetProperty("evaluationStatus").GetString());
        AssertEqual(
            new string('b', 64),
            freshness.GetProperty("builtArtifactSha256").GetString());
        AssertEqual(
            new string('b', 64),
            freshness.GetProperty("deployedArtifactSha256").GetString());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A deployed source-change pass must carry a freshness proof.");
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        AssertEqual(1, result.DevelopmentCalls.Count);
        AssertEqual(1, result.RecipeCalls.Count);
        Assert(RimTestOutputBudgets.Utf8Bytes(result.Stdout) <=
            RimTestOutputBudgets.AffectedSuitePassMaxBytes,
            "Freshness success output must remain bounded.");
        Assert(!string.IsNullOrWhiteSpace(root.GetProperty("workflowId").GetString()),
            "Affected runs must create a workflow correlation id.");
        AssertEqual(
            root.GetProperty("workflowId").GetString(),
            result.DevelopmentCalls[0].WorkflowId);
    }

    private static void FailFastAffectedRunStillProvesFreshness()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            failFast: true);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        Assert(root.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(),
            "Fail-fast must not bypass the artifact freshness transaction.");
        JsonElement failFast = root.GetProperty("failFast");
        AssertEqual(0, failFast.GetProperty("notLaunched").GetInt32());
        Assert(failFast.GetProperty("validationCompleted").GetBoolean(),
            "A passing affected fail-fast run must prove complete validation.");
        AssertEqual(1, result.DevelopmentCalls.Count);
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void AffectedCompanionRecipeMayUseLaterGeneration()
    {
        CatalogDocument catalog = new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "artifact-smoke",
                    Recipe = "artifact-fixture",
                    ArtifactFreshnessAnchor = true,
                    Covers =
                    [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                },
                new CatalogTest
                {
                    Id = "companion-smoke",
                    Recipe = "companion-fixture",
                    Covers =
                    [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                }
            ],
            Suites = []
        };
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed",
                generation: 7),
            recipeRunFactory: (recipeId, _) => PassRunWithGeneration(
                recipeId,
                recipeId == "artifact-fixture" ? 7 : 8),
            scenarioCatalog: catalog);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual(2, root.GetProperty("passed").GetInt32());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A later-generation companion recipe must not invalidate the artifact anchor proof.");
        AssertEqual("artifact-smoke", freshness.GetProperty("artifactTestId").GetString());
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        AssertEqual(2, result.RecipeCalls.Count);
    }

    private static void AffectedIdenticalArtifactUsesNoDeployProof()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "unchanged"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement freshness = document.RootElement.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual("pass", document.RootElement.GetProperty("status").GetString());
        AssertEqual("unchanged", freshness.GetProperty("deploymentDecision").GetString());
        AssertEqual(
            "identical-deployment-hash-plus-owned-generation-state",
            freshness.GetProperty("proof").GetString());
        Assert(freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "An identical artifact fast path still needs owned generation evidence.");
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void TrackedBuildOwnedArtifactMutationContinues()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.BuildOwnedArtifact);

        Assert(result.Success, "A descriptor-derived tracked artifact with matching owner bytes should continue.");
        AssertEqual(true, result.Freshness.SourceInputsStable);
        AssertEqual(1, result.Freshness.BuildOwnedOutputChanges!.Count);
        AssertEqual(
            "1.6/Assemblies/Fixture.dll",
            result.Freshness.BuildOwnedOutputChanges[0].Path);
        AssertEqual(
            result.Freshness.BuiltArtifactSha256,
            result.Freshness.BuildOwnedOutputChanges[0].Sha256);
    }

    private static void SourceMutationDuringArtifactTransactionIsRejected()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.Source);

        Assert(!result.Success, "A source mutation after the protected transaction begins must fail.");
        AssertEqual("RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION", result.Status.ErrorCode);
    }

    private static void UnrelatedTrackedMutationDuringArtifactTransactionIsRejected()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.UnrelatedTracked);

        Assert(!result.Success, "An unrelated tracked mutation must fail the transaction.");
        AssertEqual("RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION", result.Status.ErrorCode);
    }

    private static void ArtifactMutationWithoutBuildProvenanceIsRejected()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.ArtifactWithoutProvenance);

        Assert(!result.Success, "An output-looking mutation without owner evidence must fail.");
        AssertEqual("RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION", result.Status.ErrorCode);
    }

    private static void ArtifactMutationWithUnexpectedBytesIsRejected()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.ArtifactUnexpectedBytes);

        Assert(!result.Success, "An expected artifact path with bytes that differ from the owner hash must fail.");
        AssertEqual("RIMTEST_WORKTREE_CHANGED_DURING_TRANSACTION", result.Status.ErrorCode);
    }

    private static void MultipleBuildOwnedArtifactMutationsAreClassifiedPerOutput()
    {
        ArtifactFreshnessTransactionResult result = RunWorktreeMutationScenario(
            WorktreeMutationScenario.MultipleBuildOwnedArtifacts);

        Assert(result.Success, "Every independently proven build output should be accepted.");
        AssertEqual(2, result.Freshness.BuildOwnedOutputChanges!.Count);
        AssertSequence(
            ["1.6/Assemblies/Fixture.dll", "1.6/Assemblies/Fixture.Support.dll"],
            result.Freshness.BuildOwnedOutputChanges.Select(change => change.Path).ToArray());
        AssertEqual(
            2,
            result.Freshness.BuildOwnedOutputChanges
                .Select(change => change.Sha256)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    private static void AffectedBuildFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "DEVELOPMENT_BUILD_FAILED"));

        AssertArtifactTransactionFailure(result, "DEVELOPMENT_BUILD_FAILED");
    }

    private static void AffectedBuildPrerequisiteBlocksAllSelectedTests()
    {
        CatalogDocument scenarioCatalog = new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "assembler-smoke",
                    Recipe = "assembler-fixture",
                    ArtifactFreshnessAnchor = true,
                    Covers = [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                },
                new CatalogTest
                {
                    Id = "assembler-companion",
                    Recipe = "assembler-fixture",
                    ArtifactFreshnessAnchor = true,
                    Covers = [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                }
            ],
            Suites = []
        };
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "RIMWORLD_EXECUTABLE_MISSING") with
            {
                Project = "Frontier"
            },
            scenarioCatalog: scenarioCatalog);

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual(2, root.GetProperty("selectedTestCount").GetInt32());
        AssertEqual(0, root.GetProperty("executedTestCount").GetInt32());
        AssertEqual(2, root.GetProperty("blockedTestCount").GetInt32());
        AssertEqual(0, root.GetProperty("failedTestCount").GetInt32());
        Assert(!root.TryGetProperty("failures", out _),
            "A shared source prerequisite must not create test failures.");
        AssertEqual(
            "Frontier build blocked: required RimWorld build environment unavailable",
            root.GetProperty("orchestration").GetProperty("failure")
                .GetProperty("summary").GetString());
        AssertEqual("FAIL",
            root.GetProperty("orchestration").GetProperty("sourceBuild").GetString());
        AssertEqual("BLOCKED",
            root.GetProperty("orchestration").GetProperty("runtimeValidation").GetString());
        AssertEqual(0, result.RecipeCalls.Count);
    }

    private static void AffectedDeploymentFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "DEVELOPMENT_DEPLOYMENT_FAILED"));

        AssertArtifactTransactionFailure(result, "DEVELOPMENT_DEPLOYMENT_FAILED");
    }

    private static void AffectedReadinessFailureBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (_, workflowId) => FailedDevelopmentResult(
                workflowId,
                "READINESS_TIMEOUT"));

        AssertArtifactTransactionFailure(result, "READINESS_TIMEOUT");
    }

    private static void AffectedRunRecoversReadinessOnce()
    {
        string directory = CreateTempDirectory();
        try
        {
            string fingerprint = Convert.ToHexString(
                SHA256.HashData([])).ToLowerInvariant();
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) => calls++ == 0
                    ? FailedDevelopmentResult(workflowId, "PROCESS_EXITED")
                    : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged", generation: 8)
            };
            var readiness = new FakeFreshGenerationAdapter(8);
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, readinessAdapter: readiness)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-readiness-recovery",
                            TestRecipe: "recipe-a"))
                    .GetAwaiter()
                    .GetResult();

            Assert(result.Success, "A stopped runtime should recover before the owner transaction is retried.");
            AssertEqual(2, development.Calls.Count);
            AssertEqual(1, readiness.Calls.Count);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
            AssertEqual("FRESH", result.Freshness.EvaluationStatus);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void IdentityGenerationMismatchRecovers()
    {
        string directory = CreateTempDirectory();
        try
        {
            DevBridgeIdentityMismatch mismatch = IdentityMismatch(
                directory,
                directory,
                "runtimeGeneration",
                DevBridgeIdentityMismatchClassifications.RuntimeGeneration,
                recoverable: true);
            ArtifactFreshnessTransactionResult result =
                RunIdentityTransaction(mismatch);

            Assert(result.Success, "A new runtime generation should recover.");
            AssertEqual(2, result.RecoveryEvents!.Count);
            AssertEqual("recovered", result.RecoveryEvents[1].State);
            AssertEqual(1, result.Status.RecoveryAttempts);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void IdentityProcessMismatchRecovers()
    {
        string directory = CreateTempDirectory();
        try
        {
            DevBridgeIdentityMismatch mismatch = IdentityMismatch(
                directory,
                directory,
                "rimWorldProcessIdentity",
                DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity,
                recoverable: true);
            ArtifactFreshnessTransactionResult result =
                RunIdentityTransaction(mismatch);

            Assert(result.Success, "A new RimWorld process should recover.");
            AssertEqual(8, result.Freshness.Generation);
            Assert(result.Freshness.LoadedArtifactFreshnessProven,
                "Recovery must retain only final-generation freshness.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void IdentityRootMismatchRefusesRecovery()
    {
        string directory = CreateTempDirectory();
        try
        {
            DevBridgeIdentityMismatch mismatch = IdentityMismatch(
                directory,
                Path.Combine(directory, "other-owner"),
                "installationRoot",
                DevBridgeIdentityMismatchClassifications.InstallationRootOwner,
                recoverable: false);
            ArtifactFreshnessTransactionResult result =
                RunIdentityTransaction(mismatch);

            Assert(!result.Success, "A different DevBridge root must remain a hard failure.");
            AssertEqual(0, result.RecoveryEvents?.Count ?? 0);
            AssertEqual(PrerequisiteRecoveryState.RecoveryFailed, result.Status.RecoveryState);
            AssertEqual(false, result.Status.IdentityMismatch!.Recoverable);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void IdentityRecoveryExhaustionIsStructured()
    {
        string directory = CreateTempDirectory();
        try
        {
            DevBridgeIdentityMismatch mismatch = IdentityMismatch(
                directory,
                directory,
                "runtimeGeneration",
                DevBridgeIdentityMismatchClassifications.RuntimeGeneration,
                recoverable: true);
            ArtifactFreshnessTransactionResult result =
                RunIdentityTransaction(mismatch, persistent: true);

            Assert(!result.Success, "Persistent identity churn must be bounded.");
            AssertEqual(3, result.Status.RecoveryAttempts);
            AssertEqual(
                PrerequisiteRecoveryState.TransitionRecoveryExhausted,
                result.Status.RecoveryState);
            Assert(!result.Freshness.LoadedArtifactFreshnessProven,
                "Exhaustion cannot expose a freshness proof.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void CanonicalAffectedIdentityExhaustionIsJson()
    {
        int calls = 0;
        DevBridgeIdentityMismatch mismatch = IdentityMismatch(
            "C:/DevBridge",
            "C:/DevBridge",
            "runtimeGeneration",
            DevBridgeIdentityMismatchClassifications.RuntimeGeneration,
            recoverable: true);
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) =>
            {
                calls++;
                DevBridgeModDevelopmentResult failed =
                    SuccessfulDevelopmentResult(
                        sourceFingerprint,
                        workflowId,
                        "unchanged",
                        generation: 7);
                return failed with
                {
                    Status = new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "READINESS_IDENTITY_MISMATCH",
                        "persistent identity churn",
                        IdentityMismatch: mismatch),
                    Success = false
                };
            },
            scenarioCatalog: new CatalogDocument
            {
                SchemaVersion = CatalogSchema.Current,
                Tests =
                [
                    new CatalogTest
                    {
                        Id = "assembler-smoke",
                        Recipe = "assembler-fixture",
                        ArtifactFreshnessAnchor = true,
                        Covers =
                        [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                    }
                ],
                Suites = []
            },
            freshGenerationRecoveryAdapter: new FakeFreshGenerationAdapter(8, 9, 10));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual(
            "INFRASTRUCTURE_FAILURE",
            root.GetProperty("orchestration").GetProperty("overall").GetString());
        AssertEqual(
            "READINESS_IDENTITY_MISMATCH",
            root.GetProperty("orchestration")
                .GetProperty("failure")
                .GetProperty("errorCode")
                .GetString());
        Assert(
            root.GetProperty("orchestration")
                .GetProperty("failure")
                .GetProperty("identityMismatch")
                .GetProperty("recoverable")
                .GetBoolean(),
            "Terminal identity mismatch output must retain structured details.");
        AssertEqual(3, result.FreshGenerationCalls);
    }

    private static void CanonicalAffectedIdentityRecoveryContinues()
    {
        int calls = 0;
        DevBridgeIdentityMismatch mismatch = IdentityMismatch(
            "C:/DevBridge",
            "C:/DevBridge",
            "rimWorldProcessIdentity",
            DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity,
            recoverable: true);
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) =>
            {
                calls++;
                if (calls == 1)
                {
                    DevBridgeModDevelopmentResult failed =
                        SuccessfulDevelopmentResult(
                            sourceFingerprint,
                            workflowId,
                            "unchanged",
                            generation: 7);
                    return failed with
                    {
                        Status = new DevBridgeAdapterStatus(
                            DevBridgeOutcomeKind.InfrastructureFailure,
                            "READINESS_IDENTITY_MISMATCH",
                            "stale RimWorld process identity",
                            IdentityMismatch: mismatch),
                        Success = false
                    };
                }

                return SuccessfulDevelopmentResult(
                    sourceFingerprint,
                    workflowId,
                    "unchanged",
                    generation: 8);
            },
            recipeRun: PassRunWithGeneration("assembler-fixture", 8),
            scenarioCatalog: new CatalogDocument
            {
                SchemaVersion = CatalogSchema.Current,
                Tests =
                [
                    new CatalogTest
                    {
                        Id = "assembler-smoke",
                        Recipe = "assembler-fixture",
                        ArtifactFreshnessAnchor = true,
                        Covers =
                        [new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" }]
                    }
                ],
                Suites = []
            },
            freshGenerationRecoveryAdapter: new FakeFreshGenerationAdapter(8));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("pass", root.GetProperty("status").GetString());
        AssertEqual("PASS", root.GetProperty("orchestration").GetProperty("overall").GetString());
        AssertEqual("RECOVERED",
            root.GetProperty("orchestration").GetProperty("infrastructure").GetString());
        AssertEqual(2, result.DevelopmentCalls.Count);
        AssertEqual(1, result.FreshGenerationCalls);
        AssertEqual(1, result.RecipeCalls.Count);
        AssertEqual(8, root.GetProperty("artifactFreshness").GetProperty("generation").GetInt32());
        AssertEqual(
            "recovered",
            root.GetProperty("prerequisiteRecovery")[
                root.GetProperty("prerequisiteRecovery").GetArrayLength() - 1]
                .GetProperty("state")
                .GetString());
    }

    private static void IdentityParserClassifiesFields()
    {
        const string root = "C:/DevBridge";
        string[] fields =
        [
            "installationRoot",
            "coordinatorIdentity",
            "runtimeGeneration",
            "rimWorldProcessIdentity",
            "descriptorProfileRegistration",
            "protocolSchema"
        ];
        string[] classifications =
        [
            DevBridgeIdentityMismatchClassifications.InstallationRootOwner,
            DevBridgeIdentityMismatchClassifications.CoordinatorIdentity,
            DevBridgeIdentityMismatchClassifications.RuntimeGeneration,
            DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity,
            DevBridgeIdentityMismatchClassifications.StaleDescriptorProfileRegistration,
            DevBridgeIdentityMismatchClassifications.ProtocolSchema
        ];

        for (int index = 0; index < fields.Length; index++)
        {
            using JsonDocument document = JsonDocument.Parse(
                JsonSerializer.Serialize(new
                {
                    identityMismatch = new
                    {
                        field = fields[index],
                        expected = "old",
                        actual = "new",
                        recoverable = true,
                        actualRoot = root
                    }
                }));
            DevBridgeIdentityMismatch mismatch =
                DevBridgeIdentityMismatchParser.Parse(
                    document.RootElement,
                    root,
                    "READINESS_IDENTITY_MISMATCH")!;

            AssertEqual(classifications[index], mismatch.Classification);
            AssertEqual(
                index is 0 or 5 ? false : true,
                mismatch.Recoverable);
        }
    }

    private static ArtifactFreshnessTransactionResult RunIdentityTransaction(
        DevBridgeIdentityMismatch mismatch,
        bool persistent = false)
    {
        string directory = CreateTempDirectory();
        try
        {
            string fingerprint = Convert.ToHexString(
                SHA256.HashData([])).ToLowerInvariant();
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) =>
                {
                    calls++;
                    if (persistent || calls == 1)
                    {
                        DevBridgeModDevelopmentResult failed =
                            SuccessfulDevelopmentResult(
                                sourceFingerprint,
                                workflowId,
                                "unchanged",
                                generation: 7);
                        return failed with
                        {
                            Status = new DevBridgeAdapterStatus(
                                DevBridgeOutcomeKind.InfrastructureFailure,
                                "READINESS_IDENTITY_MISMATCH",
                                "simulated identity mismatch",
                                IdentityMismatch: mismatch),
                            Success = false
                        };
                    }

                    return SuccessfulDevelopmentResult(
                        sourceFingerprint,
                        workflowId,
                        "unchanged",
                        generation: 8);
                }
            };
            var readiness = new FakeFreshGenerationAdapter(8, 9, 10);
            return new ArtifactFreshnessTransaction(
                    development,
                    readinessAdapter: readiness)
                .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                    "fixture",
                    directory,
                    [],
                    fingerprint,
                    "wf-identity",
                    TestRecipe: "recipe-identity"))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static DevBridgeIdentityMismatch IdentityMismatch(
        string expectedRoot,
        string actualRoot,
        string field,
        string classification,
        bool recoverable) =>
        new(
            field,
            expectedRoot,
            actualRoot,
            classification,
            recoverable,
            expectedRoot,
            actualRoot,
            "configured --devbridge-root");

    private static void AffectedGenerationMismatchBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            PassRunWithGeneration("assembler-fixture", 8));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual(1, root.GetProperty("blockedTestCount").GetInt32());
        AssertEqual("RIMTEST_ARTIFACT_GENERATION_MISMATCH",
            root.GetProperty("blockedTests")[0].GetProperty("errorCode").GetString());
        AssertEqual("RIMTEST_ARTIFACT_GENERATION_MISMATCH", freshness.GetProperty("errorCode").GetString());
        Assert(!freshness.GetProperty("loadedArtifactFreshnessProven").GetBoolean(),
            "A mismatched generation must invalidate the freshness proof.");
        AssertEqual(1, result.RecipeCalls.Count);
    }

    private static void AffectedUnknownFreshnessBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed",
                loadedArtifactFreshnessProven: false,
                errorCode: "DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN"));

        AssertArtifactTransactionFailure(
            result,
            "DEVELOPMENT_ARTIFACT_FRESHNESS_UNKNOWN");
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        Assert(!document.RootElement.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(),
            "Unknown owner freshness must remain an explicit failure.");
    }

    private static void AffectedIncompleteFreshnessMetadataBlocksPass()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) =>
            {
                DevBridgeModDevelopmentResult complete = SuccessfulDevelopmentResult(
                    sourceFingerprint,
                    workflowId,
                    "deployed");
                return complete with
                {
                    Freshness = complete.Freshness! with
                    {
                        BuiltArtifactSha256 = null
                    }
                };
            });

        AssertArtifactTransactionFailure(result, "RIMTEST_ARTIFACT_FRESHNESS_UNKNOWN");
    }

    private static void AffectedPropagatesTransactionIdentities()
    {
        AffectedScenarioResult result = RunAffectedSourceScenario(
            (sourceFingerprint, workflowId) => SuccessfulDevelopmentResult(
                sourceFingerprint,
                workflowId,
                "deployed"),
            PassRunWithIdentity("assembler-fixture", 7, "op-assembler-1"));

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement freshness = root.GetProperty("artifactFreshness");
        AssertEqual(CliExitCodes.Success, result.ExitCode);
        AssertEqual(
            root.GetProperty("workflowId").GetString(),
            freshness.GetProperty("workflowId").GetString());
        AssertEqual("run-assembler-fixture", freshness.GetProperty("runId").GetString());
        AssertEqual(
            "op-assembler-1",
            freshness.GetProperty("operationIds")[0].GetString());
        AssertEqual(7, freshness.GetProperty("generation").GetInt32());
        Assert(!string.IsNullOrWhiteSpace(freshness.GetProperty("transactionId").GetString()),
            "The DevBridge transaction identity must reach the result.");
        Assert(!string.IsNullOrWhiteSpace(freshness.GetProperty("leaseId").GetString()),
            "The DevBridge lease identity must reach the result.");
    }

    private static void ModDevelopmentAdapterParsesBoundedFreshnessResponse()
    {
        const string workflowId = "wf-mod-1";
        const string sourceFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string response = JsonSerializer.Serialize(new
        {
            schemaVersion = DevBridgeModDevelopmentSchemas.Current,
            project = "fixture",
            success = true,
            transactionId = "tx-1",
            workflowId,
            generation = 7,
            leaseId = "lease-00000000000000000000000000000001",
            artifactFreshness = new
            {
                sourceFingerprint,
                builtArtifactSha256 = new string('b', 64),
                deployedArtifactSha256 = new string('b', 64),
                deploymentDecision = "deployed",
                generationBefore = 6,
                generationAfter = 7,
                generation = 7,
                transactionId = "tx-1",
                workflowId,
                leaseId = "lease-00000000000000000000000000000001",
                loadedArtifactFreshnessProven = true,
                proof = "deployment-hash-plus-new-owned-generation"
            }
        });
        var transport = new FakeTransport((_, _) => ProcessResult(response));
        var adapter = new DevBridgeModDevelopmentAdapter(
            transport,
            new DevBridgeModDevelopmentAdapterOptions
            {
                RootPath = "DevBridgeRoot",
                ScriptRootPath = "SourceDevBridgeRoot",
                DescriptorPath = "DevBridgeRoot/fixture.json",
                DeploymentRoot = "DeploymentRoot",
                PowerShellPath = "pwsh",
                Timeout = TimeSpan.FromSeconds(1),
                MaxStdoutBytes = 4096,
                MaxStderrBytes = 1024
            });

        DevBridgeModDevelopmentResult result = adapter.RunAsync(
                "fixture",
                "RepositoryRoot",
                sourceFingerprint,
                workflowId)
            .GetAwaiter()
            .GetResult();

        Assert(result.Status.IsSuccess, "A valid owner response should parse as success.");
        AssertEqual(7, result.Freshness!.Generation);
        Assert(result.Freshness.LoadedArtifactFreshnessProven,
            "The adapter must preserve the owner freshness proof.");
        AssertEqual(1, transport.Requests.Count);
        Assert(transport.Requests[0].Arguments.Contains("-SkipRecipe"),
            "RimLiaison must ask the owner for the transaction without running its broad recipe.");
        Assert(transport.Requests[0].Arguments.Contains("-SourceFingerprint") &&
            transport.Requests[0].Arguments.Contains(sourceFingerprint),
            "The source identity must cross the owner boundary.");
        Assert(transport.Requests[0].Arguments.Contains("-WorkflowId") &&
            transport.Requests[0].Arguments.Contains(workflowId),
            "The workflow identity must cross the owner boundary.");
        Assert(
            transport.Requests[0].Arguments.Count(argument =>
                string.Equals(argument, "-DevelopmentRoot", StringComparison.Ordinal)) == 1,
            "The owner must receive exactly one primary development root.");
        Assert(
            transport.Requests[0].Arguments.Count(argument =>
                string.Equals(argument, "-AdditionalDevelopmentRoot", StringComparison.Ordinal)) == 1,
            "The owner must receive the coordinator root as a distinct additional development root.");
        string[] requestArguments = transport.Requests[0].Arguments.ToArray();
        int developmentRootIndex = Array.IndexOf(requestArguments, "-DevelopmentRoot");
        int additionalRootIndex = Array.IndexOf(requestArguments, "-AdditionalDevelopmentRoot");
        AssertEqual(
            Path.GetFullPath("RepositoryRoot"),
            transport.Requests[0].Arguments[developmentRootIndex + 1]);
        AssertEqual("DevBridgeRoot", transport.Requests[0].Arguments[additionalRootIndex + 1]);
        AssertEqual(
            Path.Combine(
                "SourceDevBridgeRoot",
                "scripts",
                "mod-test.ps1"),
            transport.Requests[0].Arguments[Array.IndexOf(
                transport.Requests[0].Arguments.ToArray(),
                "-File") + 1]);
        AssertEqual(4096, transport.Requests[0].MaxStdoutBytes);
    }
    private static void ModDevelopmentAdapterUsesPackagedTransactionConsumer()
    {
        var transport = new FakeTransport((_, _) => ProcessResult(
            """{"schemaVersion":"devbridge-mod-development/v1","project":"fixture","success":true,"transactionId":"tx-package","workflowId":"wf-package"}"""));
        const string packagedConsumer = "PromotedPackage/transaction-components/mod-test.ps1";
        var adapter = new DevBridgeModDevelopmentAdapter(
            transport,
            new DevBridgeModDevelopmentAdapterOptions
            {
                RootPath = "DevBridgeRoot",
                ScriptRootPath = "SourceDevBridgeRoot",
                TransactionConsumerPath = packagedConsumer,
                DescriptorPath = "DevBridgeRoot/fixture.json",
                DeploymentRoot = "DeploymentRoot",
                PowerShellPath = "pwsh",
                Timeout = TimeSpan.FromSeconds(1)
            });

        DevBridgeModDevelopmentResult result = adapter.RunAsync(
                "fixture",
                "RepositoryRoot",
                new string('a', 64),
                "wf-package")
            .GetAwaiter()
            .GetResult();
        Assert(result.Status.IsSuccess, "The packaged consumer request should succeed.");
        string[] arguments = transport.Requests[0].Arguments.ToArray();
        AssertEqual(packagedConsumer, arguments[Array.IndexOf(arguments, "-File") + 1]);
    }
    private static void InternalTransactionServiceAvoidsPowerShellBoundary()
    {
        string root = CreateTempDirectory();
        string deployment = CreateTempDirectory();
        string rimWorld = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".rimdev", "recipes"));
            File.WriteAllText(
                Path.Combine(root, ".rimdev", "recipes", "fixture.json"),
                """{"schemaVersion":"devbridge-test-recipe/v1","id":"fixture","description":"fixture","projects":["fixture"],"inputs":{"quicktest":true},"requiresReady":true,"success":{"quicktestReady":true}}""");
            string descriptorPath = Path.Combine(root, "fixture.json");
            File.WriteAllText(
                descriptorPath,
                """
                {
                  "schemaVersion":"devbridge-mod-development/v1",
                  "project":"fixture",
                  "sourceProject":"Source/Fixture.csproj",
                  "configuration":"Release",
                  "expectedAssembly":"Fixture.dll",
                  "deploymentTarget":"1.6/Assemblies/Fixture.dll",
                  "testRecipe":"fixture",
                  "runtimePackage":{"sourceRoot":".","include":["About/**"]}
                }
                """);
            Directory.CreateDirectory(Path.Combine(root, "About"));
            File.WriteAllText(Path.Combine(root, "About", "About.xml"), "<ModMetaData><packageId>fixture</packageId></ModMetaData>");
            File.WriteAllText(Path.Combine(root, "Source.csproj"), "<Project />");
            string coordinatorCommand = Path.Combine(root, "DevBridge.cmd");
            File.WriteAllText(coordinatorCommand, string.Empty);
            string? stagedOutput = null;
            var transport = new FakeTransport((request, _) =>
            {
                if (string.Equals(request.FileName, "dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    stagedOutput = request.Arguments[Array.IndexOf(request.Arguments.ToArray(), "--output") + 1];
                    Directory.CreateDirectory(stagedOutput);
                    File.WriteAllText(Path.Combine(stagedOutput, "Fixture.dll"), "fixture");
                    return ProcessResult("build succeeded");
                }

                if (request.Arguments.Contains("recipe", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("show", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"schemaVersion":"devbridge-test-recipe-show/v1","recipe":{"schemaVersion":"devbridge-test-recipe/v1","id":"fixture","projects":["fixture"]},"errorCode":null,"error":null}""");
                }
                if (request.Arguments.Contains("recipe", StringComparer.OrdinalIgnoreCase) &&
                    request.Arguments.Contains("plan", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"schemaVersion":"devbridge-test-recipe-plan/v1","recipe":"fixture","alreadySatisfied":false,"estimatedRimWorldLaunches":1,"nextAction":"run","blockedBy":[],"steps":[]}""");
                }
                if (request.Arguments.Contains("status", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult($$"""{"success":true,"generation":7,"rimworldRoot":"{{rimWorld.Replace("\\", "\\\\")}}"}""");
                }
                if (request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"success":true,"exitCode":0,"generation":7,"leaseId":"lease-00000000000000000000000000000001"}""");
                }
                if (request.Arguments.Contains("wait-ready", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"success":true,"state":"READY","generation":8}""");
                }
                if (request.Arguments.Contains("stop", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"success":true,"gameState":"STOPPED"}""");
                }
                if (request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
                {
                    return ProcessResult("""{"success":true,"exitCode":0,"generation":8,"leaseId":"lease-00000000000000000000000000000001"}""");
                }
                return ProcessResult("""{"success":true}""");
            });
            var service = new InternalDevelopmentTransactionService(
                transport,
                new DevBridgeModDevelopmentAdapterOptions
                {
                    RootPath = root,
                    DescriptorPath = descriptorPath,
                    DeploymentRoot = deployment,
                    Timeout = TimeSpan.FromSeconds(5),
                    MaxStdoutBytes = 4096,
                    MaxStderrBytes = 1024
                });

            DevBridgeModDevelopmentResult result = service.RunAsync(
                    "fixture",
                    root,
                    new string('a', 64),
                    "wf-internal")
                .GetAwaiter()
                .GetResult();

            Assert(result.Status.IsSuccess, result.Status.Error ?? "The internal transaction should pass.");
            Assert(result.Freshness?.LoadedArtifactFreshnessProven == true,
                "The internal transaction must prove freshness.");
            AssertEqual("fixture", result.Freshness!.RecipeId);
            AssertEqual("fixture", result.Freshness.RecipeOwner);
            AssertEqual("PROJECT_OWNED", result.Freshness.RecipeSource);
            Assert(result.Freshness.RecipeSha256 is { Length: 64 },
                "The internal transaction must export the resolved recipe hash.");
            AssertEqual("PROJECT_BUILD", result.Build?.BuildOwnerType);
            AssertEqual("fixture", result.Build?.BuildOwnerProject);
            AssertEqual("Source/Fixture.csproj", result.Build?.BuildTarget);
            AssertEqual("dotnet build", result.Build?.BuildCommandIdentity);
            AssertEqual("build:fixture", result.Build?.BuildEvidenceId);
            Assert(stagedOutput is not null, "The internal transaction must invoke dotnet directly.");
            Assert(transport.Requests.All(request =>
                !string.Equals(request.FileName, "pwsh", StringComparison.OrdinalIgnoreCase) &&
                !request.Arguments.Any(argument => argument.EndsWith("mod-test.ps1", StringComparison.OrdinalIgnoreCase))),
                "The internal transaction must not invoke the PowerShell consumer.");
            var recipeRequest = transport.Requests.First(request =>
                request.Arguments.Contains("recipe", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("show", StringComparer.OrdinalIgnoreCase));
            Assert(recipeRequest.Arguments.Contains("--recipe-file", StringComparer.OrdinalIgnoreCase) &&
                recipeRequest.Arguments.Contains(Path.Combine(root, ".rimdev", "recipes", "fixture.json"), StringComparer.OrdinalIgnoreCase),
                "the internal transaction must pass the semantic resolved recipe file to DevBridge");
            Assert(transport.Requests.Any(request => request.Arguments.Contains("ensure-ready", StringComparer.OrdinalIgnoreCase)),
                "The internal transaction must own readiness.");
            Assert(transport.Requests.Any(request => request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase)),
                "The internal transaction must own lease acquisition.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(root);
            DeleteDirectoryIncludingReadOnlyFiles(deployment);
            DeleteDirectoryIncludingReadOnlyFiles(rimWorld);
        }
    }


    private static void ModDevelopmentOwnerManifestUsesRuntimeDeploymentRoot()
    {
        string repository = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(repository, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(repository, "Source"));
            Directory.CreateDirectory(Path.Combine(repository, "About"));
            Directory.CreateDirectory(Path.Combine(repository, "1.6", "Assemblies"));
            File.WriteAllText(
                Path.Combine(repository, "Source", "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(
                Path.Combine(repository, "About", "About.xml"),
                "<ModMetaData><packageId>lan.fixture</packageId></ModMetaData>");
            File.WriteAllText(
                Path.Combine(repository, ".rimdev", "stack.json"),
                """
                {
                  "schemaVersion": "rimdev-stack/v1",
                  "project": "Fixture",
                  "devBridgeProject": "fixture",
                  "catalog": "TestCatalog/catalog.json",
                  "rimBridge": "via-devbridge",
                  "workload": "production",
                  "projectType": "rimworld-content-mod",
                  "packageId": "lan.fixture",
                  "sourceProject": "Source/Fixture.csproj",
                  "configuration": "Release",
                  "expectedAssembly": "Fixture.dll",
                  "deploymentTarget": "1.6/Assemblies/Fixture.dll",
                  "testRecipe": "fixture-development",
                  "runtimePackage": {
                    "sourceRoot": ".",
                    "include": ["About/**", "1.*/**"],
                    "exclude": [".rimdev/**", "Source/**", "bin/**", "obj/**"]
                  }
                }
                """);
            Directory.CreateDirectory(Path.Combine(repository, ".rimdev", "recipes"));
            File.WriteAllText(
                Path.Combine(repository, ".rimdev", "recipes", "fixture-development.json"),
                """{"schemaVersion":"devbridge-test-recipe/v1","id":"fixture-development","description":"fixture","projects":["fixture"],"inputs":{"quicktest":true},"requiresReady":true,"success":{"quicktestReady":true}}""");

            var transport = new FakeTransport((_, _) => ProcessResult(
                JsonSerializer.Serialize(new
                {
                    schemaVersion = DevBridgeModDevelopmentSchemas.Current,
                    project = "fixture",
                    success = true,
                    transactionId = "tx-owner-root",
                    workflowId = "wf-owner-root"
                })));
            DevBridgeModDevelopmentAdapterOptions options =
                DevBridgeModDevelopmentAdapterOptions.Discover("RuntimeRoot") with
                {
                    ScriptRootPath = "SourceRoot",
                    DeploymentRoot = Path.GetFullPath("RuntimeTarget")
                };
            DevBridgeModDevelopmentResult result = new DevBridgeModDevelopmentAdapter(
                    transport,
                    options)
                .RunAsync(
                    "fixture",
                    repository,
                    new string('a', 64),
                    "wf-owner-root")
                .GetAwaiter()
                .GetResult();

            Assert(
                result.Status.IsSuccess,
                $"The project-owned contract should reach the owner: {result.Status.ErrorCode} {result.Status.Error}");
            string[] arguments = transport.Requests[0].Arguments.ToArray();
            int deploymentRootIndex = Array.IndexOf(arguments, "-DeploymentRoot");
            AssertEqual(options.DeploymentRoot, arguments[deploymentRootIndex + 1]);
            AssertEqual(
                Path.Combine("SourceRoot", "scripts", "mod-test.ps1"),
                arguments[Array.IndexOf(arguments, "-File") + 1]);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
        }
    }

    private static void ModDevelopmentAdapterUsesSourceScriptRoot()
    {
        DevBridgeModDevelopmentAdapterOptions options =
            DevBridgeModDevelopmentAdapterOptions.Discover(
                "RuntimeRoot") with
            {
                ScriptRootPath = "SourceRoot"
            };
        AssertEqual(Path.GetFullPath("RuntimeRoot"), options.RootPath);
        AssertEqual("SourceRoot", options.ScriptRootPath);
    }

    private static void ModDevelopmentResponseContractMatrix()
    {
        const string project = "fixture";
        const string workflowId = "wf-response-matrix";
        string fingerprint = new string('a', 64);
        int caseCount = 0;

        void RunCase(
            string name,
            Func<DevBridgeProcessResult> resultFactory,
            string expectedCode,
            DevBridgeOutcomeKind expectedOutcome)
        {
            caseCount++;
            DevBridgeModDevelopmentAdapter adapter =
                new(
                    new FakeTransport((_, _) => resultFactory()),
                    new DevBridgeModDevelopmentAdapterOptions
                    {
                        RootPath = "RuntimeRoot",
                        DescriptorPath = "RuntimeRoot/fixture.json",
                        EnableDescriptorRecovery = false,
                        Timeout = TimeSpan.FromSeconds(1)
                    });
            DevBridgeModDevelopmentResult result = adapter.RunAsync(
                    project,
                    "RepositoryRoot",
                    fingerprint,
                    workflowId).GetAwaiter().GetResult();
            Assert(
                expectedOutcome == result.Status.Outcome,
                name + " outcome expected " + expectedOutcome + ", received " + result.Status.Outcome);
            Assert(
                string.Equals(expectedCode, result.Status.ErrorCode, StringComparison.Ordinal),
                name + " code expected " + expectedCode + ", received " + result.Status.ErrorCode);
        }

        string Valid(string success, string? extra = null) =>
            $$"""{"schemaVersion":"devbridge-mod-development/v1","project":"fixture","workflowId":"{{workflowId}}","success":{{success}}{{extra}}}""";

        RunCase(
            "valid success",
            () => ProcessResult(Valid("true")),
            null!,
            DevBridgeOutcomeKind.Success);
        RunCase(
            "valid failure",
            () => ProcessResult(Valid("false", ",\"errorCode\":\"DEVELOPMENT_BUILD_FAILED\"")),
            "DEVELOPMENT_BUILD_FAILED",
            DevBridgeOutcomeKind.InfrastructureFailure);
        RunCase(
            "missing stdout",
            () => ProcessResult(string.Empty),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "blank stdout",
            () => ProcessResult(" \r\n\t"),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "invalid JSON",
            () => ProcessResult("not-json"),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "trailing JSON",
            () => ProcessResult(Valid("true") + Valid("true")),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "leading non-JSON",
            () => ProcessResult("log\r\n" + Valid("true")),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "missing schema",
            () => ProcessResult("""{"project":"fixture","success":true}"""),
            "DEVBRIDGE_MOD_TRANSACTION_SCHEMA_UNSUPPORTED",
            DevBridgeOutcomeKind.IncompatibleSchema);
        RunCase(
            "unsupported schema",
            () => ProcessResult("""{"schemaVersion":"devbridge-mod-development/v0","success":true}"""),
            "DEVBRIDGE_MOD_TRANSACTION_SCHEMA_UNSUPPORTED",
            DevBridgeOutcomeKind.IncompatibleSchema);
        RunCase(
            "missing success",
            () => ProcessResult("""{"schemaVersion":"devbridge-mod-development/v1"}"""),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "wrong success type",
            () => ProcessResult("""{"schemaVersion":"devbridge-mod-development/v1","success":"true"}"""),
            "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "project mismatch",
            () => ProcessResult(Valid("true").Replace("\"project\":\"fixture\"", "\"project\":\"other\"", StringComparison.Ordinal)),
            "DEVBRIDGE_MOD_TRANSACTION_PROJECT_MISMATCH",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "workflow mismatch",
            () => ProcessResult(Valid("true").Replace(workflowId, "wf-other", StringComparison.Ordinal)),
            "DEVBRIDGE_WORKFLOW_ID_MISMATCH",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "success nonzero exit",
            () => ProcessResult(Valid("true"), 1),
            "DEVBRIDGE_MOD_TRANSACTION_RESULT_CONFLICT",
            DevBridgeOutcomeKind.InfrastructureFailure);
        RunCase(
            "success start error",
            () => new DevBridgeProcessResult(0, Valid("true"), string.Empty, StartError: "start failed"),
            "DEVBRIDGE_MOD_TRANSACTION_RESULT_CONFLICT",
            DevBridgeOutcomeKind.InfrastructureFailure);
        RunCase(
            "failure default code",
            () => ProcessResult(Valid("false")),
            "DEVELOPMENT_TRANSACTION_FAILED",
            DevBridgeOutcomeKind.InfrastructureFailure);
        RunCase(
            "stdout truncated",
            () => new DevBridgeProcessResult(0, Valid("true"), string.Empty, StdoutTruncated: true),
            "DEVBRIDGE_MOD_TRANSACTION_OUTPUT_TRUNCATED",
            DevBridgeOutcomeKind.MalformedResponse);
        RunCase(
            "cancelled",
            () => new DevBridgeProcessResult(1, string.Empty, string.Empty, Cancelled: true),
            "RIMTEST_CANCELLED",
            DevBridgeOutcomeKind.Cancelled);
        RunCase(
            "timed out",
            () => new DevBridgeProcessResult(1, string.Empty, string.Empty, TimedOut: true),
            "DEVBRIDGE_MOD_TRANSACTION_TIMEOUT",
            DevBridgeOutcomeKind.Timeout);
        RunCase(
            "start failure",
            () => new DevBridgeProcessResult(null, string.Empty, "missing script", StartError: "missing script"),
            "DEVBRIDGE_MOD_TRANSACTION_START_FAILED",
            DevBridgeOutcomeKind.InfrastructureFailure);

        AssertEqual(20, caseCount);
    }

    private static void ModDevelopmentAdapterBindsDescriptorOutputProvenance()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            const string workflowId = "wf-output-provenance";
            const string transactionId = "tx-output-provenance";
            string sourceFingerprint = new string('a', 64);
            string artifactHash = new string('b', 64);
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            File.WriteAllText(
                descriptorPath,
                """
                {
                  "schemaVersion": "devbridge-mod-development/v1",
                  "project": "fixture",
                  "sourceProject": "Source/Fixture.csproj",
                  "configuration": "Release",
                  "expectedAssembly": "Fixture.dll",
                  "deploymentTarget": "1.6/Assemblies/Fixture.dll",
                  "testRecipe": "fixture-development"
                }
                """);
            string response = JsonSerializer.Serialize(new
            {
                schemaVersion = DevBridgeModDevelopmentSchemas.Current,
                project = "fixture",
                success = true,
                transactionId,
                workflowId,
                generation = 7,
                leaseId = "lease-00000000000000000000000000000001",
                artifactFreshness = new
                {
                    sourceFingerprint,
                    builtArtifactSha256 = artifactHash,
                    deployedArtifactSha256 = artifactHash,
                    deploymentDecision = "deployed",
                    generationBefore = 6,
                    generationAfter = 7,
                    generation = 7,
                    transactionId,
                    workflowId,
                    leaseId = "lease-00000000000000000000000000000001",
                    loadedArtifactFreshnessProven = true,
                    proof = "deployment-hash-plus-new-owned-generation"
                }
            });
            var adapter = new DevBridgeModDevelopmentAdapter(
                new FakeTransport((_, _) => ProcessResult(response)),
                DescriptorOptions(coordinator) with
                {
                    DescriptorPath = descriptorPath,
                    DeploymentRoot = repository
                });

            DevBridgeModDevelopmentResult result = adapter.RunAsync(
                    "fixture",
                    repository,
                    sourceFingerprint,
                    workflowId)
                .GetAwaiter()
                .GetResult();

            Assert(result.Status.IsSuccess, "The owner response should remain successful.");
            AssertEqual(1, result.BuildOutputs!.Count);
            AssertEqual("1.6/Assemblies/Fixture.dll", result.BuildOutputs[0].RepositoryPath);
            AssertEqual(artifactHash, result.BuildOutputs[0].Sha256);
            AssertEqual(transactionId, result.BuildOutputs[0].TransactionId);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void SharedRuntimeTransitionsRecoverOnFreshGeneration()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            string[] transitionCodes =
            [
                "RIMBRIDGE_ENDPOINT_STALE",
                "RIMBRIDGE_PROCESS_IDENTITY_MISMATCH",
                "RIMBRIDGE_COMPANION_UNAVAILABLE",
                "ENDPOINT_UNAVAILABLE",
                "RIMBRIDGE_PROTOCOL_ERROR",
                "DEVBRIDGE_NO_STRUCTURED_RESPONSE",
                "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_MISSING",
                "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID"
            ];

            foreach (string transitionCode in transitionCodes)
            {
                int calls = 0;
                var development = new FakeModDevelopmentAdapter
                {
                    Factory = (sourceFingerprint, workflowId) => calls++ == 0
                        ? FailedTransitionResult(
                            sourceFingerprint,
                            workflowId,
                            transitionCode,
                            generation: 7)
                        : SuccessfulDevelopmentResult(
                            sourceFingerprint,
                            workflowId,
                            "unchanged",
                            generation: 8)
                };
                var readiness = new FakeFreshGenerationAdapter(8);
                ArtifactFreshnessTransactionResult result =
                    new ArtifactFreshnessTransaction(development, readinessAdapter: readiness)
                        .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-transition-" + transitionCode,
                            TestRecipe: "recipe-transition"))
                        .GetAwaiter()
                        .GetResult();

                Assert(result.Success, transitionCode + " should recover once.");
                AssertEqual(2, calls);
                AssertEqual(1, readiness.Calls.Count);
                AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
                AssertEqual(2, result.RecoveryEvents!.Count);
                AssertEqual("recovering", result.RecoveryEvents[0].State);
                AssertEqual("recovered", result.RecoveryEvents[1].State);
                AssertEqual(8, result.RecoveryEvents[1].Generation);
                AssertEqual("wf-transition-" + transitionCode, result.RecoveryEvents[1].WorkflowId);
                AssertEqual(8, result.Freshness.Generation);
                Assert(result.Freshness.LoadedArtifactFreshnessProven,
                    "A recovered transition must finish with independent freshness proof.");
            }
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void RepeatedSharedTransitionProtocolFailureIsExhausted()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) =>
                {
                    calls++;
                    return FailedTransitionResult(
                        sourceFingerprint,
                        workflowId,
                        "DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID",
                        generation: calls == 1 ? 7 : 8);
                }
            };
            var readiness = new FakeFreshGenerationAdapter(8);
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, readinessAdapter: readiness)
                    .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                        "fixture",
                        directory,
                        [],
                        fingerprint,
                        "wf-transition-exhausted",
                        TestRecipe: "recipe-transition"))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A persistent protocol failure must remain terminal.");
            AssertEqual(2, calls);
            AssertEqual(1, readiness.Calls.Count);
            AssertEqual("DEVBRIDGE_MOD_TRANSACTION_RESPONSE_INVALID", result.Status.ErrorCode);
            AssertEqual(PrerequisiteRecoveryState.TransitionRecoveryExhausted, result.Status.RecoveryState);
            AssertEqual("shared-runtime-transition-recovery-exhausted", result.Status.RecoveryAction);
            AssertEqual("transitionRecoveryExhausted", result.RecoveryEvents!.Last().State);
            Assert(!result.Freshness.LoadedArtifactFreshnessProven,
                "An exhausted transition must not expose a freshness proof.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void StaleGenerationProofIsRejectedAfterTransitionRecovery()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) => calls++ == 0
                    ? FailedTransitionResult(sourceFingerprint, workflowId, "RIMBRIDGE_PROCESS_MISMATCH", 7)
                    : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged", generation: 7)
            };
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(
                        development,
                        readinessAdapter: new FakeFreshGenerationAdapter(8))
                    .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                        "fixture",
                        directory,
                        [],
                        fingerprint,
                        "wf-stale-proof",
                        TestRecipe: "recipe-transition"))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "The old generation proof must be rejected.");
            AssertEqual("RIMTEST_ARTIFACT_GENERATION_MISMATCH", result.Status.ErrorCode);
            AssertEqual(null, result.Freshness.Generation);
            Assert(!result.Freshness.LoadedArtifactFreshnessProven,
                "A rejected stale-generation result cannot prove freshness.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void SourceFailureDoesNotEnterTransitionRecovery()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            var development = new FakeModDevelopmentAdapter
            {
                Result = FailedDevelopmentResult(null, "DEVELOPMENT_BUILD_FAILED")
            };
            var readiness = new FakeFreshGenerationAdapter(8);
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, readinessAdapter: readiness)
                    .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                        "fixture",
                        directory,
                        [],
                        fingerprint,
                        "wf-build-failure",
                        TestRecipe: "recipe-transition"))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A source failure must remain terminal.");
            AssertEqual("DEVELOPMENT_BUILD_FAILED", result.Status.ErrorCode);
            AssertEqual(1, development.Calls.Count);
            AssertEqual(0, readiness.Calls.Count);
            Assert(result.RecoveryEvents is null,
                "A source failure must not produce transition recovery events.");
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void ModDevelopmentBuildFailureExportsCompilerDiagnostics()
    {
        const string workflowId = "wf-build-export";
        const string transactionId = "tx-build-export";
        const string sourceFingerprint = "source-fingerprint-build-export";
        string response = JsonSerializer.Serialize(new
        {
            schemaVersion = DevBridgeModDevelopmentSchemas.Current,
            project = "fixture",
            success = false,
            transactionId,
            workflowId,
            generation = 7,
            failure = new
            {
                errorCode = "DEVELOPMENT_BUILD_FAILED",
                command = "dotnet build Source/Fixture.csproj --configuration Debug",
                output = "Source/Fixture.cs(12,7): error CS0246: The type or namespace name 'MissingType' could not be found. apiKey=secret-value"
            },
            build = new
            {
                command = "dotnet build Source/Fixture.csproj --configuration Debug",
                exitCode = 1,
                output = "Source/Fixture.cs(12,7): error CS0246: The type or namespace name 'MissingType' could not be found.",
                causalDiagnostic = "Source/Fixture.cs(12,7): error CS0246: The type or namespace name 'MissingType' could not be found.",
                diagnosticSignature = "CS0246",
                sourceProject = "Source/Fixture.csproj",
                stagingPath = "staging/Fixture",
                timedOut = false,
                builtSha256 = "built-sha-1",
                configuration = "Debug"
            }
        });
        var transport = new FakeTransport((_, _) => ProcessResult(response, exitCode: 1));
        var adapter = new DevBridgeModDevelopmentAdapter(
            transport,
            new DevBridgeModDevelopmentAdapterOptions
            {
                RootPath = "DevBridgeRoot",
                DescriptorPath = "DevBridgeRoot/fixture.json",
                DeploymentRoot = "DeploymentRoot",
                PowerShellPath = "pwsh",
                Timeout = TimeSpan.FromSeconds(1),
                MaxStdoutBytes = 16 * 1024,
                MaxStderrBytes = 1024,
                EnableDescriptorRecovery = false
            });
        using var store = new AgentObservabilityStore();
        using var run = new AgentObservabilityRun(
            "run-build-export",
            store,
            new NoopAgentObservabilityTelemetry());
        using AgentObservabilitySession agent = run.CreateAgent(
            "mod.build-export",
            "Build Export");
        agent.Start();
        using IDisposable activation = agent.Activate();

        DevBridgeModDevelopmentResult result = adapter.RunAsync(
                "fixture",
                "RepositoryRoot",
                sourceFingerprint,
                workflowId)
            .GetAwaiter()
            .GetResult();

        AssertEqual(DevBridgeOutcomeKind.InfrastructureFailure, result.Status.Outcome);
        AssertEqual("DEVELOPMENT_BUILD_FAILED", result.Status.ErrorCode);
        Assert(
            result.Build?.Output?.Contains("CS0246", StringComparison.Ordinal) == true,
            "the DevBridge parser must preserve compiler output");
        AssertEqual("Source/Fixture.csproj", result.Build!.SourceProject);
        AssertEqual(1, result.Build.ExitCode);
        AgentIssue issue = store.GetIssues(agentId: agent.AgentId)
            .Single(value => value.Category == AgentIssueCategory.Error);
        AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([issue.Id]);

        Assert(
            bundle.SelectedIssueIds.SequenceEqual([issue.Id]),
            "the selected issue identity must remain explicit");
        Assert(bundle.BuildEvidence.Any(value =>
            value.SourceProject == "Source/Fixture.csproj" &&
            value.ExitCode == 1 &&
            value.ErrorCode == "DEVELOPMENT_BUILD_FAILED" &&
            value.DiagnosticOutput?.Contains("CS0246", StringComparison.Ordinal) == true),
            "structured build evidence must retain the compiler diagnostic");
        Assert(bundle.CommandEvidence.Any(value =>
            value.Command?.Contains("dotnet build Source/Fixture.csproj", StringComparison.Ordinal) == true &&
            value.ExitCode == 1),
            "structured command evidence must retain the failed build command");
        Assert(bundle.Completeness.IsComplete,
            "the bundle must carry the compiler reason and build command without a second lookup");
        string bundleJson = JsonSerializer.Serialize(bundle, AgentObservabilityJson.Options);
        Assert(
            !bundleJson.Contains("secret-value", StringComparison.Ordinal),
            "exported build evidence must redact credentials");
    }

    private static void PinnedDevBridgeBuildDiagnosticsCrossWireBoundary()
    {
        string? fixturePath = Environment.GetEnvironmentVariable(
            "RIMLIAISON_DEVBRIDGE_DIAGNOSTIC_FIXTURE");
        // The cross-stack validator supplies the fixture generated by the pinned
        // DevBridge process test. Keep the ordinary offline suite independent of
        // that external repository while still exercising this test when selected.
        if (string.IsNullOrWhiteSpace(fixturePath))
        {
            return;
        }
        string response = File.ReadAllText(fixturePath!);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        AssertEqual(
            DevBridgeModDevelopmentSchemas.Current,
            root.GetProperty("schemaVersion").GetString());
        JsonElement wireBuild = root.GetProperty("build");
        JsonElement wireFailure = root.GetProperty("failure");
        AssertEqual("DEVELOPMENT_BUILD_FAILED", wireFailure.GetProperty("errorCode").GetString());
        Assert(wireBuild.TryGetProperty("command", out _) &&
            wireBuild.TryGetProperty("sourceProject", out _) &&
            wireBuild.TryGetProperty("outputTruncated", out _),
            "the pinned DevBridge wire response must expose structured build fields");
        string workflowId = root.GetProperty("workflowId").GetString()!;
        string transactionId = root.GetProperty("transactionId").GetString()!;
        string sourceFingerprint = root.GetProperty("sourceFingerprint").GetString()!;

        string temporaryRoot = CreateTempDirectory();
        string previousFixture = Environment.GetEnvironmentVariable(
            "RIMLIAISON_CROSS_STACK_FIXTURE") ?? string.Empty;
        try
        {
            Directory.CreateDirectory(Path.Combine(temporaryRoot, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(temporaryRoot, "Source"));
            Directory.CreateDirectory(Path.Combine(temporaryRoot, "About"));
            File.WriteAllText(
                Path.Combine(temporaryRoot, "Source", "Frontier.csproj"),
                "<Project><PropertyGroup><AssemblyName>Frontier</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(
                Path.Combine(temporaryRoot, "About", "About.xml"),
                "<ModMetaData><packageId>lan.frontier</packageId></ModMetaData>");
            File.WriteAllText(
                Path.Combine(temporaryRoot, ".rimdev", "stack.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimdev-stack/v1",
                    project = "Frontier",
                    devBridgeProject = "frontier",
                    catalog = "catalog.json",
                    rimBridge = "via-devbridge",
                    workload = "production",
                    projectType = "rimworld-content-mod",
                    packageId = "lan.frontier",
                    sourceProject = "Source/Frontier.csproj",
                    configuration = "Release",
                    expectedAssembly = "Frontier.dll",
                    deploymentTarget = "1.6/Assemblies/Frontier.dll",
                    testRecipe = "mod-development-smoke",
                    runtimePackage = new
                    {
                        sourceRoot = ".",
                        include = new[] { "About/**", "1.*/**" },
                        exclude = new[] { ".rimdev/**", "Source/**", "bin/**", "obj/**" }
                    }
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            string descriptorPath = Path.Combine(temporaryRoot, "descriptor.json");
            Directory.CreateDirectory(Path.Combine(temporaryRoot, ".rimdev", "recipes"));
            File.WriteAllText(
                Path.Combine(temporaryRoot, ".rimdev", "recipes", "mod-development-smoke.json"),
                """{"schemaVersion":"devbridge-test-recipe/v1","id":"mod-development-smoke","description":"fixture","projects":["frontier"],"inputs":{"quicktest":true},"requiresReady":true,"success":{"quicktestReady":true}}""");
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = DevBridgeModDevelopmentSchemas.Current,
                    project = "frontier",
                    sourceProject = "Source/Frontier.csproj",
                    configuration = "Release",
                    expectedAssembly = "Frontier.dll",
                    deploymentTarget = "1.6/Assemblies/Frontier.dll",
                    testRecipe = "mod-development-smoke",
                    runtimePackage = new
                    {
                        sourceRoot = ".",
                        include = new[] { "About/**" },
                        exclude = new[] { ".rimdev/**", "Source/**" }
                    }
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            string scriptsRoot = Path.Combine(temporaryRoot, "scripts");
            Directory.CreateDirectory(scriptsRoot);
            File.WriteAllText(
                Path.Combine(scriptsRoot, "mod-test.ps1"),
                "Get-Content -LiteralPath $env:RIMLIAISON_CROSS_STACK_FIXTURE -Raw\nexit 1\n");
            Environment.SetEnvironmentVariable("RIMLIAISON_CROSS_STACK_FIXTURE", fixturePath);

            using var store = new AgentObservabilityStore();
            using var run = new AgentObservabilityRun("run-cross-stack-wire", store);
            using AgentObservabilitySession agent = run.CreateAgent(
                "mod.frontier",
                "Frontier");
            agent.Start();
            using IDisposable activation = agent.Activate();

            var adapter = new DevBridgeModDevelopmentAdapter(
                new SystemDevBridgeProcessTransport(),
                new DevBridgeModDevelopmentAdapterOptions
                {
                    RootPath = temporaryRoot,
                    DeploymentRoot = Path.Combine(
                        Path.GetPathRoot(temporaryRoot) ?? "C:\\",
                        "rimliaison-wire-runtime-" + Guid.NewGuid().ToString("N")),
                    PowerShellPath = "pwsh",
                    EnableDescriptorRecovery = false,
                    PreserveDescriptorBackup = false,
                    Timeout = TimeSpan.FromSeconds(30)
                });
            DevBridgeModDevelopmentResult result = adapter.RunAsync(
                    "frontier",
                    temporaryRoot,
                    sourceFingerprint,
                    workflowId)
                .GetAwaiter()
                .GetResult();

            AssertEqual(DevBridgeOutcomeKind.InfrastructureFailure, result.Status.Outcome);
            AssertEqual("DEVELOPMENT_BUILD_FAILED", result.Status.ErrorCode);
            AssertEqual(transactionId, result.TransactionId);
            AssertEqual(workflowId, result.WorkflowId);
            AssertEqual("DEVELOPMENT_BUILD_FAILED", result.Build!.ErrorCode);
            AssertEqual(1, result.Build.ExitCode);
            AssertEqual(false, result.Build.TimedOut);
            AssertEqual(
                wireBuild.GetProperty("outputTruncated").GetBoolean(),
                result.Build.OutputTruncated);
            Assert(result.Build.Output?.Contains("CS", StringComparison.OrdinalIgnoreCase) == true,
                "the parser must preserve a compiler diagnostic from the DevBridge fixture");
            Assert(!string.IsNullOrWhiteSpace(result.Build.Command),
                "the parser must preserve the exact bounded build command");
            Assert(!string.IsNullOrWhiteSpace(result.Build.SourceProject),
                "the parser must preserve the source project");
            Assert(!string.IsNullOrWhiteSpace(result.Build.StagingPath),
                "the parser must preserve the staging path");

            agent.Record(
                DevelopmentStage.Testing,
                AgentEventTypes.CommandFailed,
                "RimLiaison top-level command failed.",
                new
                {
                    operationKey = "cli",
                    workflowId,
                    transactionId,
                    errorCode = "RIMLIAISON_COMMAND_FAILED",
                    exitCode = 1,
                    outcome = "failure"
                });
            agent.Fail("RimLiaison command failed.", "RIMLIAISON_COMMAND_FAILED");

            IReadOnlyList<AgentEvent> events = store.GetEvents(agentId: agent.AgentId);
            IReadOnlyList<AgentIssue> issues = store.GetIssues(agentId: agent.AgentId);
            string? CodeFor(AgentEvent value) => AgentObservabilityData.GetString(value.Data, "errorCode");
            AgentIssue primary = issues.Single(issue =>
                issue.Category == AgentIssueCategory.Error &&
                string.Equals(issue.OperationKey, "build:frontier", StringComparison.Ordinal));
            AgentDiagnosticBundle bundle = store.CreateDiagnosticBundle([primary.Id]);
            string[] codes = bundle.SupportingEvents
                .Select(CodeFor)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert(codes.Contains("DEVELOPMENT_BUILD_FAILED", StringComparer.Ordinal),
                "the build failure must remain the primary causal event");
            Assert(codes.Contains("DEVBRIDGE_COMMAND_FAILED", StringComparer.Ordinal) ||
                codes.Contains("DEVELOPMENT_BUILD_FAILED", StringComparer.Ordinal),
                "the DevBridge command wrapper must retain a correlated failure code");
            Assert(codes.Contains("DEVBRIDGE_BUILD_FAILED", StringComparer.Ordinal),
                "the RimLiaison build operation failure must be correlated");
            Assert(codes.Contains("RIMLIAISON_COMMAND_FAILED", StringComparer.Ordinal),
                "the top-level command failure must be correlated");
            Assert(bundle.CorrelatedIssues.Count > 0,
                "the causal bundle must distinguish correlated wrapper failures");
            Assert(bundle.BuildEvidence.Any(value =>
                    value.ErrorCode == "DEVELOPMENT_BUILD_FAILED" &&
                    value.DiagnosticOutput?.Contains("CS", StringComparison.OrdinalIgnoreCase) == true &&
                    value.TransactionId == transactionId &&
                    value.WorkflowId == workflowId),
                "the v2 export must retain build output and transaction/workflow identity");
            Assert(bundle.BuildEvidence.Any(value => value.OutputTruncated ==
                    wireBuild.GetProperty("outputTruncated").GetBoolean()),
                "the v2 export must carry the DevBridge truncation indicator");
            if (!bundle.Completeness.IsComplete)
            {
                Assert(
                    bundle.Completeness.MissingEvidence.Contains("build.causalDiagnostic", StringComparer.Ordinal) &&
                    bundle.Completeness.MissingEvidence.Contains("build.rawOutput", StringComparer.Ordinal),
                    "incomplete pinned diagnostics must identify the missing causal and raw output evidence");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_CROSS_STACK_FIXTURE",
                string.IsNullOrEmpty(previousFixture) ? null : previousFixture);
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void ValidDevelopmentDescriptorIsPreserved()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            string original = """
                {
                  "schemaVersion": "devbridge-mod-development/v1",
                  "project": "fixture",
                  "sourceProject": "Source/Fixture.csproj",
                  "configuration": "Debug",
                  "expectedAssembly": "Fixture.dll",
                  "deploymentTarget": "custom/Assemblies/Fixture.dll",
                  "testRecipe": "custom-development"
                }
                """;
            File.WriteAllText(descriptorPath, original);

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "A valid descriptor should remain usable.");
            AssertEqual(PrerequisiteRecoveryState.Ready, result.State);
            AssertEqual(original, File.ReadAllText(descriptorPath));
            AssertEqual(0, result.Attempts);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void MissingDevelopmentDescriptorIsDerived()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "Canonical project metadata should derive the missing descriptor.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            AssertEqual(1, result.Attempts);
            Assert(File.Exists(descriptorPath), "The recovered descriptor should be atomically materialized.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("Source/Fixture.csproj", document.RootElement.GetProperty("sourceProject").GetString());
            AssertEqual("Fixture.dll", document.RootElement.GetProperty("expectedAssembly").GetString());
            AssertEqual("1.6/Assemblies/Fixture.dll", document.RootElement.GetProperty("deploymentTarget").GetString());
            AssertEqual("fixture-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void MalformedDevelopmentDescriptorIsRepaired()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            File.WriteAllText(descriptorPath, "{ malformed descriptor");

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator));

            Assert(result.CanProceed, "Malformed descriptor JSON should be safely reconstructed.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            string malformedBackup = result.BackupPath ?? string.Empty;
            Assert(!string.IsNullOrWhiteSpace(malformedBackup) &&
                File.Exists(malformedBackup),
                "The malformed input should remain recoverable through its backup.");
            Assert(malformedBackup.StartsWith(
                    Path.Combine(coordinator, "artifacts", "descriptor-recovery") +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "descriptor recovery state must stay in the generated artifacts area");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("fixture", document.RootElement.GetProperty("project").GetString());
            AssertEqual("fixture-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void StaleDevelopmentDescriptorIsReconciledSafely()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "fixture.json");
            File.WriteAllText(
                descriptorPath,
                """
                {
                  "schemaVersion": "devbridge-mod-development/v1",
                  "project": "fixture",
                  "sourceProject": "Source/Deleted.csproj",
                  "configuration": "Debug",
                  "expectedAssembly": "Legacy.dll",
                  "deploymentTarget": "custom/Assemblies/Legacy.dll",
                  "testRecipe": "custom-development"
                }
                """);

            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "fixture",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator) with
                    {
                        TestRecipe = null,
                        DeploymentTarget = null
                    });

            Assert(result.CanProceed, "A stale descriptor with canonical replacement metadata should recover.");
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.State);
            string staleBackup = result.BackupPath ?? string.Empty;
            Assert(!string.IsNullOrWhiteSpace(staleBackup) &&
                File.Exists(staleBackup),
                "Reconciliation should preserve the stale descriptor as a bounded backup.");
            Assert(staleBackup.StartsWith(
                    Path.Combine(coordinator, "artifacts", "descriptor-recovery") +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "stale descriptor backups must not dirty DevelopmentProjects");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
            AssertEqual("Source/Fixture.csproj", document.RootElement.GetProperty("sourceProject").GetString());
            AssertEqual("Fixture.dll", document.RootElement.GetProperty("expectedAssembly").GetString());
            AssertEqual("Debug", document.RootElement.GetProperty("configuration").GetString());
            AssertEqual("custom/Assemblies/Legacy.dll", document.RootElement.GetProperty("deploymentTarget").GetString());
            AssertEqual("custom-development", document.RootElement.GetProperty("testRecipe").GetString());
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static void AmbiguousDevelopmentDescriptorIsBlocked()
    {
        (string repository, string coordinator) = CreateDescriptorFixture();
        try
        {
            File.WriteAllText(
                Path.Combine(repository, "Source", "Other.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            string descriptorPath = Path.Combine(
                coordinator,
                "DevelopmentProjects",
                "unknown.json");
            DevBridgeDescriptorReconciliationResult result =
                DevBridgeDevelopmentDescriptorReconciler.Reconcile(
                    "unknown",
                    repository,
                    descriptorPath,
                    DescriptorOptions(coordinator) with
                    {
                        ChangedPaths = null,
                        TestRecipe = "unknown-development",
                        DeploymentTarget = "1.6/Assemblies/Unknown.dll"
                    });

            Assert(!result.CanProceed, "Ambiguous canonical project metadata must fail closed.");
            AssertEqual(PrerequisiteRecoveryState.RecoveryRequired, result.State);
            AssertEqual("DEVBRIDGE_DESCRIPTOR_SOURCE_AMBIGUOUS", result.ErrorCode);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(repository);
            DeleteDirectoryIncludingReadOnlyFiles(coordinator);
        }
    }

    private static DevBridgeModDevelopmentAdapterOptions DescriptorOptions(
        string coordinator) =>
        new()
        {
            RootPath = coordinator,
            ChangedPaths = ["Source/Changed.cs"],
            TestRecipe = "fixture-development"
        };

    private static (string Repository, string Coordinator) CreateDescriptorFixture()
    {
        string repository = CreateTempDirectory();
        string coordinator = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repository, "Source"));
        Directory.CreateDirectory(Path.Combine(repository, "1.6", "Assemblies"));
        Directory.CreateDirectory(Path.Combine(coordinator, "DevelopmentProjects"));
        File.WriteAllText(
            Path.Combine(repository, "Source", "Changed.cs"),
            "namespace Fixture; public class Changed {}\n");
        File.WriteAllText(
            Path.Combine(repository, "Source", "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Fixture</AssemblyName></PropertyGroup></Project>");
        return (repository, coordinator);
    }

    private static void LeaseRecoveryRetriesOwnerTransactionOnce()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) =>
                    calls++ == 0
                        ? FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED")
                        : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged")
            };
            var lease = new FakeLeaseAdapter();
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-lease-recovery"))
                    .GetAwaiter()
                    .GetResult();

            Assert(result.Success, "A compatible lease should allow one bounded owner retry.");
            AssertEqual(2, development.Calls.Count);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(1, lease.EndCalls);
            AssertEqual(PrerequisiteRecoveryState.Recovered, result.Status.RecoveryState);
            AssertEqual(1, result.Status.RecoveryAttempts);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void LeaseContentionRemainsExplicit()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            var development = new FakeModDevelopmentAdapter
            {
                Result = FailedDevelopmentResult(null, "RIMBRIDGE_LEASE_REQUIRED")
            };
            var lease = new FakeLeaseAdapter();
            lease.BeginResults.Enqueue(new DevBridgeLeaseResult(
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.DevBridgeRefusal,
                    "DEVBRIDGE_LEASE_CONTENDED"),
                null,
                null));
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            null))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "An actively owned lease must remain a blocker.");
            AssertEqual(PrerequisiteRecoveryState.Contended, result.Status.RecoveryState);
            AssertEqual(1, development.Calls.Count);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(0, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void LeaseRecoveryHasNoLoop()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (_, workflowId) =>
                {
                    calls++;
                    return FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED");
                }
            };
            var lease = new FakeLeaseAdapter();
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            null))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A second lease-required response must remain a bounded failure.");
            AssertEqual(2, calls);
            AssertEqual(1, lease.BeginCalls);
            AssertEqual(1, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void FreshnessCleanupFailureRemainsVisible()
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        string directory = CreateTempDirectory();
        try
        {
            int calls = 0;
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (sourceFingerprint, workflowId) => calls++ == 0
                    ? FailedDevelopmentResult(workflowId, "RIMBRIDGE_LEASE_REQUIRED")
                    : SuccessfulDevelopmentResult(sourceFingerprint, workflowId, "unchanged")
            };
            var lease = new FakeLeaseAdapter
            {
                EndResult = new DevBridgeLeaseResult(
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "DEVBRIDGE_LEASE_RELEASE_FAILED"),
                    null,
                    null)
            };
            ArtifactFreshnessTransactionResult result =
                new ArtifactFreshnessTransaction(development, lease)
                    .PrepareAsync(
                        new ArtifactFreshnessTransactionRequest(
                            "fixture",
                            directory,
                            [],
                            fingerprint,
                            "wf-cleanup-failure"))
                    .GetAwaiter()
                    .GetResult();

            Assert(!result.Success, "A failed lease release must block the freshness proof.");
            AssertEqual("DEVBRIDGE_LEASE_RELEASE_FAILED", result.Status.ErrorCode);
            Assert(result.Cleanup is not null, "Lease cleanup evidence must be present on failure.");
            AssertEqual("FAILED", result.Cleanup!.Status);
            AssertEqual(false, result.Cleanup.LeaseReleased);
            AssertEqual(false, result.Cleanup.TemporaryStateCleared);
            AssertEqual(1, lease.EndCalls);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static void CleanupFailureRemainsIndependentInOrchestration()
    {
        CatalogSuiteExecutionResult execution = new(
            "affected",
            [new RimTestResult { Status = "fail", Test = "fixture" }],
            0,
            Cancelled: false,
            Cleanup: new RimTestCleanupSummary
            {
                Status = "FAILED",
                LeaseReleased = false,
                TemporaryStateCleared = false,
                ErrorCode = "DEVBRIDGE_LEASE_RELEASE_FAILED"
            });

        RimTestSuiteResult result = RimTestSuiteResultFactory.FromExecution(execution, 1);

        AssertEqual("TEST_FAILURE", result.Orchestration!.Overall);
        AssertEqual("FAILED", result.Orchestration.Cleanup!.Status);
        AssertEqual("RIMTEST_TEST_FAILURE", result.Orchestration.Failure!.ErrorCode);
    }

    private static void AssertArtifactTransactionFailure(
        AffectedScenarioResult result,
        string errorCode)
    {
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual(CliExitCodes.InternalError, result.ExitCode);
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual(0, root.GetProperty("passed").GetInt32());
        AssertEqual(0, root.GetProperty("failed").GetInt32());
        AssertEqual(
            root.GetProperty("selectedTestCount").GetInt32(),
            root.GetProperty("blockedTestCount").GetInt32());
        AssertEqual(
            root.GetProperty("selectedTestCount").GetInt32(),
            root.GetProperty("blockedTests").GetArrayLength());
        Assert(!root.TryGetProperty("failures", out _),
            "Blocked prerequisites must not be serialized as test failures.");
        Assert(!root.GetProperty("artifactFreshness")
            .GetProperty("loadedArtifactFreshnessProven")
            .GetBoolean(), "A failed transaction cannot prove freshness.");
        JsonElement orchestration = root.GetProperty("orchestration");
        bool sourceBuildFailed = errorCode.StartsWith(
            "DEVELOPMENT_BUILD",
            StringComparison.Ordinal) || errorCode.StartsWith("BUILD_", StringComparison.Ordinal);
        AssertEqual(
            sourceBuildFailed ? "SOURCE_BUILD_FAILURE" : "INFRASTRUCTURE_FAILURE",
            orchestration.GetProperty("overall").GetString());
        AssertEqual(
            sourceBuildFailed ? "FAIL" : "NOT_RUN",
            orchestration.GetProperty("sourceBuild").GetString());
        AssertEqual(
            sourceBuildFailed ? "BLOCKED" :
                root.GetProperty("artifactFreshness").GetProperty("evaluationStatus").GetString() == "FAILED"
                    ? "FAILED"
                    : "NOT_EVALUATED",
            orchestration.GetProperty("deployment").GetString());
        AssertEqual("BLOCKED", orchestration.GetProperty("runtimeValidation").GetString());
        AssertEqual(
            errorCode.StartsWith("RIMTEST_", StringComparison.Ordinal)
                ? "RimLiaison"
                : "DevBridge2",
            orchestration.GetProperty("failure")
            .GetProperty("owner").GetString());
        AssertEqual(errorCode, orchestration.GetProperty("failure")
            .GetProperty("errorCode").GetString());
        AssertEqual(0, result.RecipeCalls.Count);
    }

    private static void ExplicitAffectedPathsTakePrecedence()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                false,
                [],
                "GIT_DISCOVERY_FAILED"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Foo.cs",
                        "Defs/Foo.xml",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            AssertEqual(CliExitCodes.Success, exitCode);
            AssertEqual(0, git.Calls.Count);
            AssertSequence(["Source/Foo.cs", "Defs/Foo.xml"], impactAdapter.ChangedPaths);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void GitDiscoveryFailureIsConservative()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var git = new FakeGitChangeProvider(new GitChangeDiscoveryResult(
                false,
                [],
                "GIT_DISCOVERY_FAILED"));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    ["affected", "--run", "--json", "--catalog", catalogPath],
                    stdout,
                    stderr,
                    gitChangeProvider: git)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.InternalError, exitCode);
            AssertEqual("infrastructure", root.GetProperty("status").GetString());
            AssertEqual("affected", root.GetProperty("suite").GetString());
            AssertEqual("blocked", root.GetProperty("selectionStatus").GetString());
            AssertEqual("GIT_DISCOVERY_FAILED", root.GetProperty("selectionErrorCode").GetString());
            AssertEqual("git status --short", root.GetProperty("nextAction").GetString());
            JsonElement orchestration = root.GetProperty("orchestration");
            AssertEqual("INFRASTRUCTURE_FAILURE", orchestration.GetProperty("overall").GetString());
            AssertEqual("UNAVAILABLE", orchestration.GetProperty("infrastructure").GetString());
            AssertEqual("NOT_EVALUATED", orchestration.GetProperty("deployment").GetString());
            AssertEqual("RimLiaison", orchestration.GetProperty("failure")
                .GetProperty("owner").GetString());
            AssertEqual("selection", orchestration.GetProperty("failure")
                .GetProperty("stage").GetString());
            AssertEqual("GIT_DISCOVERY_FAILED", orchestration.GetProperty("failure")
                .GetProperty("errorCode").GetString());
            Assert(!root.TryGetProperty("artifactFreshness", out _),
                "A selection failure must not emit a false freshness failure.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RimErrorDiagnosisProvidesNextAction()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = FailedRun(
            "assembler-fixture",
            "fp-diagnosis",
            "RECIPE_ASSERTION_FAILED");
        CliResult result = RunCatalogCliWithAdapters(
            CreateCatalog(),
            adapter,
            new FakeRimErrorDiagnosisAdapter(AvailableDiagnosis("d-81f72")),
            "run",
            "assembler-smoke");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("fail", root.GetProperty("status").GetString());
        AssertEqual("d-81f72", root.GetProperty("diagnostic").GetProperty("id").GetString());
        AssertEqual("rimerror show d-81f72", root.GetProperty("nextAction").GetString());
    }

    private static void AffectedStructuredDevBridgeFailurePreservesCausalChain()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            using var observabilityStore = new AgentObservabilityStore();
            AgentDiagnosticEvidenceReference stdoutEvidence =
                observabilityStore.PersistEvidence(
                    "command.stdout",
                    "{\"success\":false,\"errorCode\":\"OUTPUT_TOO_LARGE\"}")!;
            AgentDiagnosticEvidenceReference stderrEvidence =
                observabilityStore.PersistEvidence(
                    "command.stderr",
                    "DevBridge coordinator rejected the maximum payload.")!;
            var response = new DevBridgeProcessResponse(
                Success: false,
                Healthy: false,
                ExitCode: 2,
                ErrorCode: "OUTPUT_TOO_LARGE",
                Error: "The coordinator result exceeded the maximum payload length.",
                NextAction: "DevBridge.cmd doctor --json",
                State: "ERROR",
                SchemaVersion: "devbridge/v1");
            var adapter = new FakeRecipeAdapter();
            adapter.Runs["assembler-fixture"] = new DevBridgeRecipeRunResult(
                "assembler-fixture",
                new DevBridgeAdapterStatus(
                    DevBridgeOutcomeKind.InfrastructureFailure,
                    "OUTPUT_TOO_LARGE",
                    Response: response,
                    ProcessEvidence: new DevBridgeProcessEvidence(
                        "C:\\DevBridge\\DevBridge.cmd",
                        "C:\\DevBridge",
                        directory,
                        "devbridge:run",
                        stdoutEvidence.Id,
                        stderrEvidence.Id,
                        null,
                        17)),
                false,
                "run-assembler-fixture",
                null,
                null,
                null,
                "evidence-assembler-fixture",
                null,
                "DevBridge.cmd doctor --json",
                null,
                null,
                [],
                null);
            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Defs/Assembler.xml",
                        "--run",
                        "--json",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    adapter,
                    impactAdapter: impactAdapter,
                    observabilityStore: observabilityStore)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.InternalError, exitCode);
            AssertEqual("infrastructure", root.GetProperty("status").GetString());
            AssertEqual("OUTPUT_TOO_LARGE", root.GetProperty("failures")[0]
                .GetProperty("errorCode").GetString());
            AssertEqual("DevBridge2", root.GetProperty("orchestration")
                .GetProperty("failure").GetProperty("owner").GetString());
            AssertEqual("OUTPUT_TOO_LARGE", root.GetProperty("orchestration")
                .GetProperty("failure").GetProperty("errorCode").GetString());

            AgentEvent[] events = observabilityStore.GetEvents().ToArray();
            AgentEvent causalEvent = events.Last(
                value => value.Type == AgentEventTypes.TestFailed);
            AgentIssue issue = observabilityStore.GetIssues()
                .First(value => value.EventIds.Contains(causalEvent.Id));
            AgentDiagnosticBundle bundle = observabilityStore.CreateDiagnosticBundle([issue.Id]);
            Assert(bundle.SupportingEvents.Any(value =>
                    value.Type == AgentEventTypes.TestFailed &&
                    AgentObservabilityData.GetString(value.Data, "errorCode") ==
                        "OUTPUT_TOO_LARGE"),
                "affected failure must retain the causal test event");
            Assert(bundle.CommandEvidence.Any(value =>
                    value.Stdout?.Contains("OUTPUT_TOO_LARGE", StringComparison.Ordinal) == true &&
                    value.Stderr?.Contains("maximum payload", StringComparison.Ordinal) == true),
                "affected failure must retain DevBridge process evidence");
            AgentEvent lifecycleFailure = events.Single(
                value => value.Type == AgentEventTypes.CommandFailed &&
                    AgentObservabilityData.GetString(value.Data, "operationKey") ==
                        "cli:affected");
            string causeEventId = AgentObservabilityData.GetString(
                lifecycleFailure.Data,
                "causeEventId")!;
            Assert(bundle.SupportingEvents.Any(value => value.Id == causeEventId),
                "top-level affected failure must link to its causal event");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DevBridgeFailureProvidesNextAction()
    {
        var adapter = new FakeRecipeAdapter();
        adapter.Runs["assembler-fixture"] = InfrastructureRun(
            "assembler-fixture",
            "DEVBRIDGE_REFUSAL");
        CliResult result = RunCatalogCliWithAdapter(
            CreateCatalog(),
            adapter,
            "run",
            "assembler-smoke");

        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        AssertEqual("infrastructure", root.GetProperty("status").GetString());
        AssertEqual("DEVBRIDGE_REFUSAL", root.GetProperty("errorCode").GetString());
        AssertEqual("DevBridge.cmd doctor --json", root.GetProperty("nextAction").GetString());
    }

    private static void RimContextStaleProvidesNextAction()
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            var impactAdapter = new FakeImpactAdapter(new RimContextImpactResult(
                new RimContextAdapterStatus(
                    RimContextImpactOutcome.Unavailable,
                    "INDEX_NOT_FOUND"),
                [],
                [],
                false));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int exitCode = CliApplication.RunAsync(
                    [
                        "affected",
                        "Source/Foo.cs",
                        "--json",
                        "--fallback-suite",
                        "missing",
                        "--catalog",
                        catalogPath
                    ],
                    stdout,
                    stderr,
                    impactAdapter: impactAdapter)
                .GetAwaiter()
                .GetResult();

            using JsonDocument document = JsonDocument.Parse(stdout.ToString());
            JsonElement root = document.RootElement;
            AssertEqual(CliExitCodes.ConservativeSelection, exitCode);
            AssertEqual("blocked", root.GetProperty("status").GetString());
            AssertEqual("CONTEXT_STALE", root.GetProperty("errorCode").GetString());
            AssertEqual("rimliaison affected --run --json", root.GetProperty("nextAction").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunCapabilitiesFixture(
        string response,
        params string[] options)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport((_, _) => ProcessResult(response));
            var arguments = new List<string>
            {
                "capabilities",
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            arguments.AddRange(options);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunUiFixture(
        string operation,
        IReadOnlyList<string>? options = null,
        string? toolsResponse = null,
        string? targetResponse = null,
        string? targetScreenshotResponse = null,
        string? cellScreenshotResponse = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(toolsResponse ?? UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(targetResponse ?? UiTargetsCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/take_screenshot",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            targetScreenshotResponse ?? UiTargetScreenshotCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/screenshot_cell_rect",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            cellScreenshotResponse ?? UiCellScreenshotCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected DevBridge UI request: " +
                        string.Join(" ", request.Arguments));
                });
            var arguments = new List<string>
            {
                "ui",
                operation,
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            if (options is not null)
            {
                arguments.AddRange(options);
            }

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static (CliResult Result, FakeTransport Transport) RunTransactionalUiFixture(
        IReadOnlyList<string> options,
        string? restoreResponse = null,
        string? screenshotResponse = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            var transport = new FakeTransport(
                (request, _) =>
                {
                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(
                            "{\"success\":true,\"exitCode\":0,\"leaseId\":\"lease-1\",\"generation\":1}");
                    }

                    if (request.Arguments.Contains("test", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("end", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult("{\"success\":true,\"exitCode\":0}");
                    }

                    if (request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("begin", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(ViewportBeginResponse());
                    }

                    if (request.Arguments.Contains("environment", StringComparer.OrdinalIgnoreCase) &&
                        request.Arguments.Contains("restore", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(restoreResponse ?? ViewportRestoreResponse());
                    }

                    if (request.Arguments.Contains("tools", StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiToolsResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/get_screen_targets",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(UiTargetsCallResponse());
                    }

                    if (request.Arguments.Contains(
                        "rimworld/take_screenshot",
                        StringComparer.OrdinalIgnoreCase))
                    {
                        return ProcessResult(screenshotResponse ?? UiTargetScreenshotCallResponse());
                    }

                    throw new InvalidOperationException(
                        "Unexpected transactional UI request: " +
                        string.Join(" ", request.Arguments));
                });
            var arguments = new List<string>
            {
                "ui",
                "screenshot",
                "--json",
                "--devbridge",
                "DevBridge.cmd",
                "--devbridge-root",
                directory
            };
            arguments.AddRange(options);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return (
                new CliResult(exitCode, stdout.ToString(), stderr.ToString()),
                transport);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static string UiToolsResponse() =>
        """
        {
          "success": true,
          "rimBridgeRoute": {
            "success": true,
            "result": {
              "tools": [
                {
                  "id": "rimworld/get_screen_targets",
                  "title": "Visible screen targets",
                  "summary": "Inspect visible screen and UI targets",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "waitForVisualReady", "type": "boolean" }
                  ]
                },
                {
                  "id": "rimworld/take_screenshot",
                  "title": "Targeted screenshot",
                  "summary": "Capture a screenshot clipped to a visible UI target",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "targetId", "type": "string", "required": true },
                    { "name": "clipPadding", "type": "integer" },
                    { "name": "includeScreenTargets", "type": "boolean" },
                    { "name": "suppressMessage", "type": "boolean" },
                    { "name": "waitForVisualReady", "type": "boolean" },
                    { "name": "doNotResetCamera", "type": "boolean" }
                  ]
                },
                {
                  "id": "rimworld/screenshot_cell_rect",
                  "title": "Cell-region screenshot",
                  "summary": "Capture a screenshot of a map cell rectangle",
                  "category": "ui",
                  "providerId": "rimworld",
                  "source": "Core",
                  "parameters": [
                    { "name": "x", "type": "integer", "required": true },
                    { "name": "z", "type": "integer", "required": true },
                    { "name": "width", "type": "integer", "required": true },
                    { "name": "height", "type": "integer", "required": true },
                    { "name": "paddingCells", "type": "integer" },
                    { "name": "waitForVisualReady", "type": "boolean" },
                    { "name": "doNotResetCamera", "type": "boolean" }
                  ]
                }
              ]
            }
          }
        }
        """;

    private static string UiTargetsCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "targets": [
                {
                  "id": "window:main",
                  "kind": "window",
                  "label": "Main window",
                  "rect": { "x": 10, "y": 20, "width": 2, "height": 2 }
                },
                {
                  "id": "menu:context",
                  "kind": "context-menu",
                  "label": "Context menu",
                  "rect": { "x": 30, "y": 40, "width": 3, "height": 3 }
                }
              ]
            }
            """,
            "op-targets");

    private static string UiTargetScreenshotCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "path": "/evidence/main.png",
              "clipTargetId": "window:main",
              "clipTargetKind": "window",
              "clipTargetLabel": "Main window",
              "clipRect": { "x": 10, "y": 20, "width": 2, "height": 2 },
              "cameraRestored": true,
              "capturedAtUtc": "2026-08-17T00:00:00Z"
            }
            """,
            "op-target-shot",
            "evidence-target-shot");

    private static string UiCellScreenshotCallResponse() =>
        RouteResponse(
            """
            {
              "success": true,
              "path": "/evidence/cell.png",
              "requestedRect": { "x": 10, "z": 20, "width": 3, "height": 4 },
              "paddedRect": { "x": 9, "z": 19, "width": 5, "height": 6 },
              "cameraRestored": true,
              "capturedAtUtc": "2026-08-17T00:00:00Z"
            }
            """,
            "op-cell-shot",
            "evidence-cell-shot");

    private static string ViewportBeginResponse() =>
        "{" +
        "\"success\":true,\"exitCode\":0,\"viewport\":" +
        "{\"schemaVersion\":\"devbridge-viewport-environment/v1\",\"success\":true," +
        "\"status\":\"prepared\",\"transactionId\":\"viewport-1\",\"leaseId\":\"lease-1\"," +
        "\"generation\":1,\"requested\":{\"kind\":\"narrow\",\"width\":1024,\"height\":768}," +
        "\"capturedState\":{\"clientWidth\":1920,\"clientHeight\":1080,\"windowHandle\":7001}," +
        "\"effectiveViewport\":{\"clientWidth\":1024,\"clientHeight\":768,\"windowHandle\":7001}," +
        "\"persistentPreferenceMutation\":false,\"restorationVerified\":false}" +
        "}";

    private static string ViewportRestoreResponse() =>
        "{" +
        "\"success\":true,\"exitCode\":0,\"viewport\":" +
        "{\"schemaVersion\":\"devbridge-viewport-environment/v1\",\"success\":true," +
        "\"status\":\"restored\",\"transactionId\":\"viewport-1\",\"leaseId\":\"lease-1\"," +
        "\"generation\":1,\"restoredViewport\":{\"clientWidth\":1920,\"clientHeight\":1080,\"windowHandle\":7001}," +
        "\"persistentPreferenceMutation\":false,\"restorationVerified\":true,\"cleanupStatus\":\"restored\"}" +
        "}";

    private static string RouteResponse(
        string result,
        string? operationId = null,
        string? evidenceId = null)
    {
        string operation = operationId is null
            ? string.Empty
            : $"\"operationId\":{JsonSerializer.Serialize(operationId)},";
        string evidence = evidenceId is null
            ? string.Empty
            : $"\"evidenceId\":{JsonSerializer.Serialize(evidenceId)},";
        return $"{{\"success\":true,\"rimBridgeRoute\":{{\"success\":true,{operation}{evidence}\"result\":{result}}}}}";
    }

    private static CliResult RunDoctorFixture(
        bool contextAvailable,
        bool useExplicitOverrides = false,
        bool usePascalRimBridgeFields = false,
        bool identityMismatch = false,
        bool structuredFailure = false)
    {
        string directory = CreateTempDirectory();
        string? previousProductionManifest =
            Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
        string? previousProductionCli =
            Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI");
        string? previousDevBridgeSourceRoot =
            Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT");
        string? previousRimTestDevBridgeRoot =
            Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT");
        try
        {
            string projectDirectory = Path.Combine(directory, "FixtureMod");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".rimdev"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, ".git"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Source"));
            Directory.CreateDirectory(Path.Combine(projectDirectory, "About"));
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            string catalogPath = Path.Combine(projectDirectory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(CreateCatalog()));
            string rimWorldRoot = Path.Combine(directory, "RimWorld");
            string modsRoot = Path.Combine(rimWorldRoot, "Mods");
            Directory.CreateDirectory(modsRoot);
            File.WriteAllText(
                Path.Combine(projectDirectory, "Source", "FixtureMod.csproj"),
                "<Project><PropertyGroup><AssemblyName>FixtureMod</AssemblyName></PropertyGroup></Project>");
            File.WriteAllText(
                Path.Combine(projectDirectory, "About", "About.xml"),
                "<ModMetaData><packageId>fixture.package</packageId></ModMetaData>");
            string manifestCatalog = useExplicitOverrides ? "missing.json" : "catalog.json";
            File.WriteAllText(
                Path.Combine(projectDirectory, ".rimdev", "stack.json"),
                $"{{\"schemaVersion\":\"rimdev-stack/v1\",\"project\":\"FixtureMod\",\"devBridgeProject\":\"FixtureMod\",\"catalog\":\"{manifestCatalog}\",\"fallbackSuite\":\"settings\",\"rimBridge\":\"via-devbridge\",\"workload\":\"production\",\"projectType\":\"rimworld-content-mod\",\"packageId\":\"fixture.package\",\"sourceProject\":\"Source/FixtureMod.csproj\",\"configuration\":\"Release\",\"expectedAssembly\":\"FixtureMod.dll\",\"deploymentTarget\":\"1.6/Assemblies/FixtureMod.dll\",\"testRecipe\":\"assembler-smoke\",\"runtimeFolder\":\"FixtureMod\",\"runtimePackage\":{{\"sourceRoot\":\".\",\"include\":[\"About/**\",\"1.*/**\"],\"exclude\":[\".rimdev/**\",\"Source/**\",\"bin/**\",\"obj/**\"]}}}}");
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "workspace.json"),
                $"{{\"schemaVersion\":\"rimdev-workspace/v1\",\"rimWorldRoot\":{JsonSerializer.Serialize(rimWorldRoot)},\"activeModsRoot\":{JsonSerializer.Serialize(modsRoot)},\"repositories\":[],\"packageMappings\":{{}}}}");
            string overrideCatalogPath = Path.Combine(projectDirectory, "override.json");
            if (useExplicitOverrides)
            {
                File.WriteAllText(overrideCatalogPath, Serialize(CreateCatalog()));
            }
            if (contextAvailable)
            {
                new RimContextService().Index(
                    new RimContextIndexRequest(directory));
            }
            string rimContextPath = Path.Combine(directory, "rimctx.cmd");
            string devBridgePath = Path.Combine(directory, "DevBridge.cmd");
            string rimErrorPath = Path.Combine(directory, "rimerror.cmd");
            string identityRoot = JsonSerializer.Serialize(projectDirectory);
            string rimBridge = usePascalRimBridgeFields
                ? "{\"ConfiguredMode\":\"required\",\"LifecycleState\":\"READY\"}"
                : "{\"configuredMode\":\"optional\",\"lifecycleState\":\"READY\"}";
            string devBridgeResult = structuredFailure
                ? "{\"success\":false,\"exitCode\":2,\"state\":\"ERROR\",\"errorCode\":\"OUTPUT_TOO_LARGE\",\"error\":\"The coordinator result exceeded the maximum payload length.\"}"
                : identityMismatch
                    ? $"{{\"success\":false,\"healthy\":false,\"errorCode\":\"READINESS_IDENTITY_MISMATCH\",\"field\":\"runtimeGeneration\",\"expected\":\"8\",\"actual\":\"7\",\"classification\":\"runtimeGeneration\",\"recoverable\":true,\"authoritativeRoot\":{identityRoot},\"actualRoot\":{identityRoot},\"rimBridge\":{rimBridge}}}"
                    : $"{{\"success\":true,\"healthy\":true,\"rimBridge\":{rimBridge}}}";
            File.WriteAllText(rimContextPath, "fixture");
            File.WriteAllText(devBridgePath, "fixture");
            File.WriteAllText(rimErrorPath, "fixture");
            string promotedPackageRoot = Path.Combine(directory, "promoted");
            Directory.CreateDirectory(promotedPackageRoot);
            string coordinatorPath = Path.Combine(directory, "Coordinator", "DevBridge.Coordinator.exe");
            string consumerPath = Path.Combine(promotedPackageRoot, "mod-test.ps1");
            string unifiedManifestPath = Path.Combine(promotedPackageRoot, "unified-manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(coordinatorPath)!);
            File.WriteAllText(coordinatorPath, "fixture coordinator");
            File.WriteAllText(consumerPath, "fixture consumer");
            File.WriteAllText(
                unifiedManifestPath,
                "{\"schemaVersion\":\"rimliaison-unified-production-package/v2\",\"productFingerprint\":\"fixture-promoted\",\"ownerProduct\":\"RimLiaison\",\"runtimeSubsystem\":\"RimLiaison.Runtime\",\"rimBridgeServer\":{\"boundary\":\"external-game-side\"}}");
            string coordinatorHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(coordinatorPath))).ToLowerInvariant();
            string consumerHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(consumerPath))).ToLowerInvariant();
            string unifiedManifestHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(unifiedManifestPath))).ToLowerInvariant();
            string currentExecutablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The test process path is unavailable.");
            string rimLiaisonAssemblyPath = typeof(CliApplication).Assembly.Location;
            string rimLiaisonExecutableHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(currentExecutablePath))).ToLowerInvariant();
            string rimLiaisonAssemblyHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(rimLiaisonAssemblyPath))).ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(directory, ".devbridge-runtime-manifest.json"),
                $"{{\"packageSha256\":\"fixture-package\",\"files\":[{{\"path\":\"Coordinator/DevBridge.Coordinator.exe\",\"sha256\":\"{coordinatorHash}\"}}]}}");
            File.WriteAllText(
                Path.Combine(directory, "production-toolchain.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = "fixture-promoted",
                    fingerprint = "fixture-promoted",
                    ownerProduct = "RimLiaison",
                    runtimeSubsystem = "RimLiaison.Runtime",
                    rimLiaisonExecutablePath = currentExecutablePath,
                    rimLiaisonExecutableSha256 = rimLiaisonExecutableHash,
                    rimLiaisonAssemblyPath,
                    rimLiaisonAssemblySha256 = rimLiaisonAssemblyHash,
                    devBridgeRuntimeRoot = directory,
                    devBridgePackageSha256 = "fixture-package",
                    transactionConsumerPath = consumerPath,
                    transactionConsumerSha256 = consumerHash,
                    runtimeProtocolContract = "devbridge-mod-development/v1",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    unifiedManifestPath,
                    unifiedManifestSha256 = unifiedManifestHash
                }));

            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                Path.Combine(directory, "production-toolchain.json"));
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI", null);
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", null);
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", null);
            var transport = new FakeTransport(
                (request, _) => request.Arguments.Contains("summary")
                    ? ProcessResult(contextAvailable
                        ? "{\"schemaVersion\":\"rimctx/v1\",\"status\":\"ok\",\"command\":\"summary\",\"data\":{}}"
                        : "{\"schemaVersion\":\"rimctx/v1\",\"status\":\"error\",\"command\":\"summary\",\"code\":\"INDEX_NOT_FOUND\",\"message\":\"missing\"}")
                    : request.Arguments.Contains("project")
                        ? ProcessResult($"{{\"success\":true,\"projectResolution\":{{\"canonicalProjects\":[\"{request.Arguments[4]}\"]}}}}")
                    : ProcessResult(devBridgeResult));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var arguments = new List<string>
            {
                "doctor",
                "--json",
                "--rimcontext",
                rimContextPath,
                "--rimcontext-root",
                directory,
                "--devbridge",
                devBridgePath,
                "--devbridge-root",
                directory,
                "--rimerror",
                rimErrorPath
            };
            if (useExplicitOverrides)
            {
                arguments.Add("--catalog");
                arguments.Add(overrideCatalogPath);
                arguments.Add("--devbridge-project");
                arguments.Add("override");
            }

            int exitCode = WithCurrentDirectory(
                projectDirectory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        processTransport: transport)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST",
                previousProductionManifest);
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI", previousProductionCli);
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", previousDevBridgeSourceRoot);
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", previousRimTestDevBridgeRoot);
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunManifestOnlyDoctor(string? manifest)
    {
        string directory = CreateTempDirectory();
        string? previousRimWorldRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string? previousActiveModsRoot = Environment.GetEnvironmentVariable("RIMWORLD_MODS_ROOT");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            if (manifest is not null && manifest.Contains("\"project\"", StringComparison.Ordinal))
            {
                string rimWorldRoot = Path.Combine(directory, "RimWorld");
                string modsRoot = Path.Combine(rimWorldRoot, "Mods");
                Directory.CreateDirectory(modsRoot);
                Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
                File.WriteAllText(
                    Path.Combine(directory, ".rimdev", "workspace.json"),
                    $"{{\"schemaVersion\":\"rimdev-workspace/v1\",\"rimWorldRoot\":{JsonSerializer.Serialize(rimWorldRoot)},\"activeModsRoot\":{JsonSerializer.Serialize(modsRoot)},\"repositories\":[{{\"path\":\".\"}}],\"packageMappings\":{{}}}}");
                Environment.SetEnvironmentVariable("RIMWORLD_ROOT", rimWorldRoot);
                Environment.SetEnvironmentVariable("RIMWORLD_MODS_ROOT", modsRoot);
            }
            if (manifest is not null)
            {
                Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
                File.WriteAllText(Path.Combine(directory, ".rimdev", "stack.json"), manifest);
            }
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        ["doctor", "--json"],
                        stdout,
                        stderr)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIMWORLD_ROOT", previousRimWorldRoot);
            Environment.SetEnvironmentVariable("RIMWORLD_MODS_ROOT", previousActiveModsRoot);
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunManifestOnlyDoctorWithCatalog(
        string manifest,
        string catalog)
    {
        string directory = CreateTempDirectory();
        string? previousRimWorldRoot = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string? previousActiveModsRoot = Environment.GetEnvironmentVariable("RIMWORLD_MODS_ROOT");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            string rimWorldRoot = Path.Combine(directory, "RimWorld");
            string modsRoot = Path.Combine(rimWorldRoot, "Mods");
            Environment.SetEnvironmentVariable("RIMWORLD_ROOT", rimWorldRoot);
            Environment.SetEnvironmentVariable("RIMWORLD_MODS_ROOT", modsRoot);
            Directory.CreateDirectory(modsRoot);
            Directory.CreateDirectory(Path.Combine(directory, ".rimdev"));
            File.WriteAllText(
                Path.Combine(directory, ".rimdev", "workspace.json"),
                $"{{\"schemaVersion\":\"rimdev-workspace/v1\",\"rimWorldRoot\":{JsonSerializer.Serialize(rimWorldRoot)},\"activeModsRoot\":{JsonSerializer.Serialize(modsRoot)},\"repositories\":[{{\"path\":\".\"}}],\"packageMappings\":{{}}}}");
            File.WriteAllText(Path.Combine(directory, ".rimdev", "stack.json"), manifest);
            File.WriteAllText(Path.Combine(directory, "catalog.json"), catalog);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        ["doctor", "--json"],
                        stdout,
                        stderr)
                    .GetAwaiter()
                    .GetResult());
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIMWORLD_ROOT", previousRimWorldRoot);
            Environment.SetEnvironmentVariable("RIMWORLD_MODS_ROOT", previousActiveModsRoot);
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static CliResult RunInitFixture(
        string directory,
        params string[] options)
    {
        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] arguments = ["init", "--json", .. options];
        int exitCode = WithCurrentDirectory(
            directory,
            () => CliApplication.RunAsync(
                    arguments,
                    stdout,
                    stderr)
                .GetAwaiter()
                .GetResult());
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static T WithFallbackSuiteEnvironment<T>(
        string? value,
        Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable("RIMTEST_FALLBACK_SUITE");
        try
        {
            Environment.SetEnvironmentVariable("RIMTEST_FALLBACK_SUITE", value);
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIMTEST_FALLBACK_SUITE", previous);
        }
    }

    private static void RunGit(string directory, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git did not start.");
        process.WaitForExit();
        AssertEqual(0, process.ExitCode);
    }

    private static T WithCurrentDirectory<T>(string directory, Func<T> action)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static void DeleteDirectoryIncludingReadOnlyFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    private static RimErrorDiagnosisResult AvailableDiagnosis(string id) =>
        new(
            RimErrorDiagnosisOutcome.Available,
            new RimErrorAdapterStatus(RimErrorDiagnosisOutcome.Available),
            new RimErrorDiagnosticSummary
            {
                Id = id,
                Category = "runtime"
            },
            "fail");

    private static DevBridgeDiagnosticSourceResult AvailableSource(int generation) =>
        new(
            new DevBridgeDiagnosticSourceStatus(
                DevBridgeDiagnosticSourceOutcome.Available),
            new DevBridgeScopedDiagnosticSource(
                DevBridgeDiagnosticSchemas.ScopedSource,
                generation,
                "[RimWorld] controlled failure\n",
                System.Text.Encoding.UTF8.GetByteCount("[RimWorld] controlled failure\n"),
                1,
                false,
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("[RimWorld] controlled failure\n")))
                    .ToLowerInvariant()));

    private static DevBridgeRecipeRunResult PassRun(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            7,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult PassRunWithGeneration(
        string recipeId,
        int generation) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            generation,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult PassRunWithIdentity(
        string recipeId,
        int generation,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            generation,
            "lease-00000000000000000000000000000001",
            null,
            null,
            null,
            null,
            null,
            null,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                true,
                null,
                [],
                operationId,
                null,
                generation,
                "launch-1")]);

    private static DevBridgeModDevelopmentResult SuccessfulDevelopmentResult(
        string sourceFingerprint,
        string? workflowId,
        string deploymentDecision,
        int generation = 7,
        bool loadedArtifactFreshnessProven = true,
        string? errorCode = null,
        string? artifactSha256 = null,
        IReadOnlyList<DevBridgeBuildOutputEvidence>? buildOutputs = null)
    {
        string artifactHash = artifactSha256 ?? new string('b', 64);
        int generationBefore = deploymentDecision == "unchanged"
            ? generation
            : Math.Max(0, generation - 1);
        return new DevBridgeModDevelopmentResult(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Success,
                errorCode),
            true,
            "tx-1",
            workflowId,
            generation,
            "lease-00000000000000000000000000000001",
            new DevBridgeArtifactFreshness(
                sourceFingerprint,
                artifactHash,
                artifactHash,
                deploymentDecision,
                generationBefore,
                generation,
                generation,
                loadedArtifactFreshnessProven,
                deploymentDecision == "unchanged"
                    ? "identical-deployment-hash-plus-owned-generation-state"
                    : "deployment-hash-plus-new-owned-generation",
                "tx-1",
                workflowId,
                "lease-00000000000000000000000000000001",
                errorCode),
            BuildOutputs: buildOutputs);
    }

    private static ArtifactFreshnessTransactionResult RunWorktreeMutationScenario(
        WorktreeMutationScenario scenario)
    {
        string directory = CreateTempDirectory();
        try
        {
            string sourceRelative = "Source/Changed.cs";
            string artifactRelative = "1.6/Assemblies/Fixture.dll";
            string secondArtifactRelative = "1.6/Assemblies/Fixture.Support.dll";
            string unrelatedRelative = "README.md";
            string source = Path.Combine(directory, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
            string artifact = Path.Combine(directory, artifactRelative.Replace('/', Path.DirectorySeparatorChar));
            string secondArtifact = Path.Combine(directory, secondArtifactRelative.Replace('/', Path.DirectorySeparatorChar));
            string unrelated = Path.Combine(directory, unrelatedRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(source, "class Changed { int Value = 2; }\n");
            File.WriteAllBytes(artifact, "old-primary"u8.ToArray());
            File.WriteAllBytes(secondArtifact, "old-secondary"u8.ToArray());
            File.WriteAllText(unrelated, "stable\n");

            byte[] primaryBytes = "built-primary"u8.ToArray();
            byte[] secondaryBytes = "built-secondary"u8.ToArray();
            string primaryHash = Convert.ToHexString(SHA256.HashData(primaryBytes)).ToLowerInvariant();
            string secondaryHash = Convert.ToHexString(SHA256.HashData(secondaryBytes)).ToLowerInvariant();
            string sourceFingerprint = ComputeWorktreeFingerprint(directory, [sourceRelative]);
            GitRepositoryChange[] beforeChanges =
            [
                new GitRepositoryChange(sourceRelative, " M", false, false)
            ];
            var afterChanges = new List<GitRepositoryChange>(beforeChanges);
            switch (scenario)
            {
                case WorktreeMutationScenario.BuildOwnedArtifact:
                case WorktreeMutationScenario.ArtifactWithoutProvenance:
                case WorktreeMutationScenario.ArtifactUnexpectedBytes:
                    afterChanges.Add(new GitRepositoryChange(artifactRelative, " M", false, true));
                    break;
                case WorktreeMutationScenario.Source:
                    break;
                case WorktreeMutationScenario.UnrelatedTracked:
                    afterChanges.Add(new GitRepositoryChange(unrelatedRelative, " M", false, false));
                    break;
                case WorktreeMutationScenario.MultipleBuildOwnedArtifacts:
                    afterChanges.Add(new GitRepositoryChange(artifactRelative, " M", false, true));
                    afterChanges.Add(new GitRepositoryChange(secondArtifactRelative, " M", false, true));
                    break;
            }

            var repositoryState = new SequencedGitRepositoryStateProvider(
                WorktreeState(directory, beforeChanges),
                WorktreeState(directory, afterChanges.ToArray()));
            var development = new FakeModDevelopmentAdapter
            {
                Factory = (fingerprint, workflowId) =>
                {
                    switch (scenario)
                    {
                        case WorktreeMutationScenario.Source:
                            File.AppendAllText(source, "// concurrent source edit\n");
                            break;
                        case WorktreeMutationScenario.UnrelatedTracked:
                            File.AppendAllText(unrelated, "concurrent\n");
                            break;
                        case WorktreeMutationScenario.ArtifactUnexpectedBytes:
                            File.WriteAllBytes(artifact, "unexpected"u8.ToArray());
                            break;
                        case WorktreeMutationScenario.MultipleBuildOwnedArtifacts:
                            File.WriteAllBytes(artifact, primaryBytes);
                            File.WriteAllBytes(secondArtifact, secondaryBytes);
                            break;
                        default:
                            File.WriteAllBytes(artifact, primaryBytes);
                            break;
                    }

                    IReadOnlyList<DevBridgeBuildOutputEvidence>? outputs = scenario switch
                    {
                        WorktreeMutationScenario.BuildOwnedArtifact or
                        WorktreeMutationScenario.ArtifactUnexpectedBytes =>
                        [new DevBridgeBuildOutputEvidence(artifactRelative, primaryHash, "tx-1")],
                        WorktreeMutationScenario.MultipleBuildOwnedArtifacts =>
                        [
                            new DevBridgeBuildOutputEvidence(artifactRelative, primaryHash, "tx-1"),
                            new DevBridgeBuildOutputEvidence(secondArtifactRelative, secondaryHash, "tx-1")
                        ],
                        _ => null
                    };
                    string decision = scenario is WorktreeMutationScenario.Source or
                        WorktreeMutationScenario.UnrelatedTracked
                        ? "unchanged"
                        : "deployed";
                    return SuccessfulDevelopmentResult(
                        fingerprint,
                        workflowId,
                        decision,
                        artifactSha256: primaryHash,
                        buildOutputs: outputs);
                }
            };

            return new ArtifactFreshnessTransaction(
                    development,
                    repositoryStateProvider: repositoryState)
                .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                    "fixture",
                    directory,
                    [sourceRelative],
                    sourceFingerprint,
                    "wf-worktree-integrity"))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static GitRepositoryStateResult WorktreeState(
        string directory,
        params GitRepositoryChange[] changes) =>
        new(
            true,
            new GitRepositoryStateSnapshot(
                directory,
                "git:fixture",
                null,
                "head-1",
                null,
                null,
                null,
                changes.Length > 0,
                changes));

    private static string ComputeWorktreeFingerprint(
        string directory,
        IReadOnlyList<string> paths)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths
                     .Select(value => value.Replace('\\', '/'))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
            string fullPath = Path.Combine(
                directory,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0missing\0"));
                continue;
            }

            hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0file\0"));
            hash.AppendData(File.ReadAllBytes(fullPath));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes("\0end\0"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private enum WorktreeMutationScenario
    {
        BuildOwnedArtifact,
        Source,
        UnrelatedTracked,
        ArtifactWithoutProvenance,
        ArtifactUnexpectedBytes,
        MultipleBuildOwnedArtifacts
    }

    private static DevBridgeModDevelopmentResult FailedDevelopmentResult(
        string? workflowId,
        string errorCode) =>
        new(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode,
                "simulated owner failure"),
            false,
            "tx-failed",
            workflowId,
            null,
            null,
            null);

    private static DevBridgeModDevelopmentResult FailedTransitionResult(
        string sourceFingerprint,
        string? workflowId,
        string errorCode,
        int generation) =>
        SuccessfulDevelopmentResult(
            sourceFingerprint,
            workflowId,
            "unchanged",
            generation: generation) with
        {
            Status = new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode,
                "simulated shared runtime transition"),
            Success = false
        };

    private static AffectedScenarioResult RunAffectedSourceScenario(
        Func<string, string?, DevBridgeModDevelopmentResult>? resultFactory = null,
        DevBridgeRecipeRunResult? recipeRun = null,
        bool failFast = false,
        CatalogDocument? scenarioCatalog = null,
        Func<string, int, DevBridgeRecipeRunResult>? recipeRunFactory = null,
        IDevBridgeFreshGenerationAdapter? freshGenerationRecoveryAdapter = null)
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Source"));
            File.WriteAllText(
                Path.Combine(directory, "Source", "Changed.cs"),
                "class Changed { int Value = 0; }\n");
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(scenarioCatalog ?? CreateCatalog()));
            RunGit(directory, "init", "--quiet");
            RunGit(directory, "config", "user.email", "RimLiaison@example.invalid");
            RunGit(directory, "config", "user.name", "RimLiaison");
            RunGit(directory, "add", "Source/Changed.cs", "catalog.json");
            RunGit(directory, "commit", "--quiet", "-m", "initial");
            File.WriteAllText(
                Path.Combine(directory, "Source", "Changed.cs"),
                "class Changed { int Value = 1; }\n");

            var recipeAdapter = new FakeRecipeAdapter
            {
                RunFactory = recipeRunFactory is null
                    ? null
                    : (recipeId, _, _, index) => recipeRunFactory(recipeId, index)
            };
            recipeAdapter.Runs["assembler-fixture"] =
                recipeRun ?? PassRun("assembler-fixture");
            var developmentAdapter = new FakeModDevelopmentAdapter
            {
                Factory = resultFactory
            };

            var impactAdapter = new FakeImpactAdapter(SuccessfulImpact(
                new RimContextImpact(
                    "direct",
                    "def",
                    "def-assembler",
                    "CCM_Assembler",
                    null,
                    null,
                    null,
                    null)));
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var arguments = new List<string>
            {
                "affected",
                "Source/Changed.cs",
                "--run"
            };
            if (failFast)
            {
                arguments.Add("--fail-fast");
            }

            arguments.AddRange(
                [
                    "--json",
                    "--devbridge-project",
                    "fixture",
                    "--catalog",
                    catalogPath
                ]);

            int exitCode = WithCurrentDirectory(
                directory,
                () => CliApplication.RunAsync(
                        arguments.ToArray(),
                        stdout,
                        stderr,
                        recipeAdapter,
                        impactAdapter: impactAdapter,
                        developmentAdapter: developmentAdapter,
                        freshGenerationRecoveryAdapter: freshGenerationRecoveryAdapter)
                    .GetAwaiter()
                    .GetResult());

            return new AffectedScenarioResult(
                exitCode,
                stdout.ToString(),
                stderr.ToString(),
                developmentAdapter.Calls.ToArray(),
                recipeAdapter.RunCalls.ToArray(),
                freshGenerationRecoveryAdapter is FakeFreshGenerationAdapter fakeRecovery
                    ? fakeRecovery.Calls.Count
                    : 0);
        }
        finally
        {
            DeleteDirectoryIncludingReadOnlyFiles(directory);
        }
    }

    private static DevBridgeRecipeRunResult SuccessfulRun(
        string recipeId,
        int generation = 1,
        string? workflowId = null) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "run-" + recipeId,
            generation,
            null,
            null,
            "evidence-" + recipeId,
            null,
            null,
            null,
            null,
            [],
            workflowId);

    private static DevBridgeRecipeRunResult FailedRun(
        string recipeId,
        string fingerprint,
        string errorCode,
        int generation = 1,
        string? workflowId = null,
        IReadOnlyList<DevBridgeOperationSummary>? operations = null) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.TestFailure,
                errorCode),
            false,
            "run-" + recipeId,
            generation,
            null,
            null,
            "evidence-" + recipeId,
            fingerprint,
            null,
            null,
            null,
            operations ?? [],
            workflowId);

    private static DevBridgeRecipeRunResult InfrastructureRun(
        string recipeId,
        string errorCode) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipeRunResult CancelledRun(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.Cancelled,
                "RIMTEST_CANCELLED"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            []);

    private static DevBridgeRecipePlanResult SuccessfulPlan(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            new DevBridgeRecipePlan(
                recipeId,
                false,
                0,
                [],
                null,
                []));

    private static DevBridgeRecipePlanResult SatisfiedPlan(string recipeId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            new DevBridgeRecipePlan(
                recipeId,
                true,
                0,
                [],
                "none",
                []));

    private static CatalogTest ReusableTest(string id, string recipeId) =>
        ReusableTestWithKey(id, recipeId, "fixture-ready");

    private static CatalogTest ReusableTestWithKey(
        string id,
        string recipeId,
        string reuseKey,
        CatalogRecipeIsolationMode mode = CatalogRecipeIsolationMode.PureRead,
        string? resetRecipe = null) =>
        new()
        {
            Id = id,
            Recipe = recipeId,
            Isolation = new CatalogRecipeIsolation
            {
                Mode = mode,
                ReuseKey = reuseKey,
                ResetRecipe = resetRecipe
            }
        };

    private static CatalogSuiteRecipeProfile RecipeProfile(string signature) =>
        new(signature, [], []);

    private static void SetRecipeProfile(
        FakeRecipeAdapter adapter,
        string recipeId,
        IReadOnlyList<string> projects)
    {
        string projectJson = string.Join(",", projects.Select(project =>
            "\"" + project + "\""));
        using JsonDocument definition = JsonDocument.Parse(
            "{\"projects\":[" + projectJson + "],\"inputs\":{\"quicktest\":true}}");
        adapter.ShowDefinitions[recipeId] = definition.RootElement.Clone();
    }

    private static CatalogTest ResettableTest(string id, string recipeId) =>
        new()
        {
            Id = id,
            Recipe = recipeId,
            Isolation = new CatalogRecipeIsolation
            {
                Mode = CatalogRecipeIsolationMode.FixtureResettable,
                ReuseKey = "fixture-resettable",
                ResetRecipe = "fixture-reset"
            }
        };

    private static CatalogDocument CreateIsolationCatalog(params CatalogTest[] tests) =>
        new()
        {
            SchemaVersion = CatalogSchema.Current,
            Tests = tests.ToList(),
            Suites = []
        };

    private static DevBridgeLeaseResult SuccessLease(string leaseId, int generation) =>
        new(
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            leaseId,
            generation);

    private static DevBridgeResetResult SuccessfulReset(string leaseId, int generation) =>
        new(
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            generation,
            leaseId);

    private static DevBridgeRecipeRunResult PassRunWithLease(
        string recipeId,
        int generation,
        string? leaseId,
        string? workflowId,
        string runId,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            runId,
            generation,
            leaseId,
            null,
            null,
            null,
            null,
            false,
            0,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                true,
                null,
                [],
                operationId,
                workflowId,
                generation,
                "launch-" + runId)],
            workflowId);

    private static DevBridgeRecipeRunResult LeaseRequiredRun(
        string recipeId,
        string? workflowId,
        string? leaseId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                "RIMBRIDGE_LEASE_REQUIRED"),
            false,
            "run-lease-required",
            7,
            leaseId,
            null,
            null,
            null,
            null,
            null,
            0,
            [],
            workflowId);

    private static DevBridgeRecipeRunResult FailedRunWithLease(
        string recipeId,
        int generation,
        string? leaseId,
        string? workflowId,
        string runId,
        string operationId) =>
        new(
            recipeId,
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.TestFailure,
                "RECIPE_ASSERTION_FAILED"),
            false,
            runId,
            generation,
            leaseId,
            null,
            "evidence-" + runId,
            "failure-" + runId,
            null,
            false,
            0,
            [new DevBridgeOperationSummary(
                "rimworld/test",
                false,
                "RECIPE_ASSERTION_FAILED",
                [],
                operationId,
                workflowId,
                generation,
                "launch-" + runId)],
            workflowId);

    private static CatalogDocument CreateCatalog()
    {
        return new CatalogDocument
        {
            SchemaVersion = CatalogSchema.Current,
            Tests =
            [
                new CatalogTest
                {
                    Id = "settings-smoke",
                    Recipe = "settings-fixture",
                    Cost = CatalogCost.Medium,
                    Tags = ["settings"],
                    Covers = [new CatalogCoverage { Kind = "feature", Name = "settings" }]
                },
                new CatalogTest
                {
                    Id = "assembler-smoke",
                    Recipe = "assembler-fixture",
                    Cost = CatalogCost.Low,
                    Description = "Checks assembler registration.",
                    Tags = ["crafting", "assembler"],
                    Covers =
                    [
                        new CatalogCoverage { Kind = "def", Name = "CCM_Assembler" },
                        new CatalogCoverage { Kind = "csharp_type", Name = "CompAssembler" }
                    ]
                }
            ],
            Suites =
            [
                new CatalogSuite
                {
                    Id = "smoke",
                    Tests = ["assembler-smoke"],
                    Suites = ["settings"]
                },
                new CatalogSuite
                {
                    Id = "settings",
                    Tests = ["settings-smoke"]
                }
            ]
        };
    }

    private static CliResult RunCli(
        CatalogDocument catalog,
        params string[] command)
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(catalog));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using AgentObservabilityStore observabilityStore = new();
            string[] args = command
                .Concat(["--catalog", catalogPath])
                .ToArray();
            int exitCode = CliApplication.RunAsync(
                    args,
                    stdout,
                    stderr,
                    observabilityStore: observabilityStore)
                .GetAwaiter()
                .GetResult();
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static CliResult RunCatalogCliWithAdapter(
        CatalogDocument catalog,
        IDevBridgeRecipeAdapter recipeAdapter,
        params string[] command)
    {
        return RunCatalogCliWithAdapters(
            catalog,
            recipeAdapter,
            null,
            command);
    }

    private static CliResult RunCatalogCliWithAdapters(
        CatalogDocument catalog,
        IDevBridgeRecipeAdapter recipeAdapter,
        IRimErrorDiagnosisAdapter? diagnosisAdapter,
        params string[] command)
    {
        string directory = CreateTempDirectory();
        try
        {
            string catalogPath = Path.Combine(directory, "catalog.json");
            File.WriteAllText(catalogPath, Serialize(catalog));
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            using AgentObservabilityStore observabilityStore = new();
            string[] args = command
                .Concat(["--catalog", catalogPath])
                .ToArray();
            int exitCode = diagnosisAdapter is null
                ? CliApplication.RunAsync(
                    args,
                    stdout,
                    stderr,
                    recipeAdapter,
                    observabilityStore: observabilityStore)
                    .GetAwaiter()
                    .GetResult()
                : CliApplication.RunAsync(
                    args,
                    stdout,
                    stderr,
                    recipeAdapter,
                    diagnosisAdapter: diagnosisAdapter,
                    observabilityStore: observabilityStore)
                    .GetAwaiter()
                    .GetResult();
            return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
                "rimliaison-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Serialize(CatalogDocument catalog)
    {
        return JsonSerializer.Serialize(
            catalog,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters =
                {
                    new JsonStringEnumConverter(
                        JsonNamingPolicy.CamelCase,
                        allowIntegerValues: false)
                }
            });
    }

    private static DevBridgeRecipeAdapter CreateAdapter(FakeTransport transport)
    {
        return new DevBridgeRecipeAdapter(
            transport,
            new DevBridgeAdapterOptions
            {
                CommandPath = "DevBridge.cmd",
                RootPath = "DevBridgeRoot",
                ShowPlanTimeout = TimeSpan.FromSeconds(1),
                RunTimeout = TimeSpan.FromSeconds(1)
            });
    }

    private static DevBridgeProcessResult ProcessResult(
        string stdout,
        int exitCode = 0)
    {
        return new DevBridgeProcessResult(exitCode, stdout, string.Empty);
    }

    private static void AssertHasCode(
        IEnumerable<CatalogIssue> errors,
        string code)
    {
        Assert(errors.Any(error => error.Code == code), $"Expected error code {code}.");
    }

    private static void AssertSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        AssertEqual(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            AssertEqual(expected[index], actual[index]);
        }
    }

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
            throw new InvalidOperationException(
                $"Expected {expected}; got {actual}.");
        }
    }

    private sealed class FakeTransport : IDevBridgeProcessTransport
    {
        private readonly Func<
            DevBridgeProcessRequest,
            CancellationToken,
            DevBridgeProcessResult> handler;

        public FakeTransport(
            Func<
                DevBridgeProcessRequest,
                CancellationToken,
                DevBridgeProcessResult> handler)
        {
            this.handler = handler;
        }

        public List<DevBridgeProcessRequest> Requests { get; } = [];

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(handler(request, cancellationToken));
        }
    }

    private sealed class FakeCapabilityAdapter : IDevBridgeCapabilityAdapter
    {
        private readonly IReadOnlyList<DevBridgeCapability> capabilities;
        private readonly DevBridgeCapabilityStatus status;

        public FakeCapabilityAdapter(
            IReadOnlyList<DevBridgeCapability>? capabilities = null,
            DevBridgeCapabilityStatus? status = null)
        {
            this.capabilities = capabilities ?? [];
            this.status = status ?? new DevBridgeCapabilityStatus(
                DevBridgeCapabilityOutcome.Success);
        }

        public Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
            DevBridgeCapabilityQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result());

        public Task<DevBridgeCapabilityDiscoveryResult> DiscoverAsync(
            DevBridgeCapabilityQuery query,
            string? workflowId,
            string? leaseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result());

        private DevBridgeCapabilityDiscoveryResult Result() =>
            new(status, capabilities, capabilities.Count, false);
    }

    private sealed class FakeRecipeAdapter : IDevBridgeRecipeAdapter
    {
        public Dictionary<string, DevBridgeRecipeRunResult> Runs { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, DevBridgeRecipePlanResult> Plans { get; } =
            new(StringComparer.Ordinal);

        public List<string> PlanCalls { get; } = [];

        public List<string> RunCalls { get; } = [];

        public List<DevBridgeRecipeExecutionContext?> ExecutionContexts { get; } = [];

        public List<DevBridgeRecipeRunResult> RunResults { get; } = [];

        public Dictionary<string, JsonElement> ShowDefinitions { get; } =
            new(StringComparer.Ordinal);

        public Func<
            string,
            string?,
            DevBridgeRecipeExecutionContext?,
            int,
            DevBridgeRecipeRunResult>? RunFactory
        { get; init; }

        public Task<DevBridgeRecipeShowResult> ShowAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            JsonElement definition;
            if (ShowDefinitions.TryGetValue(recipeId, out JsonElement configured))
            {
                definition = configured.Clone();
            }
            else
            {
                using JsonDocument document = JsonDocument.Parse(
                    "{\"projects\":[],\"inputs\":{}}");
                definition = document.RootElement.Clone();
            }

            return Task.FromResult(new DevBridgeRecipeShowResult(
                recipeId,
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                definition));
        }

        public Task<DevBridgeRecipePlanResult> PlanAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            PlanCalls.Add(recipeId);
            return Task.FromResult(
                Plans.TryGetValue(recipeId, out DevBridgeRecipePlanResult? plan)
                    ? plan
                    : SuccessfulPlan(recipeId));
        }

        public Task<DevBridgeRecipeRunResult> RunAsync(
            string recipeId,
            CancellationToken cancellationToken = default)
        {
            return RunAsync(recipeId, null, null, cancellationToken);
        }

        public Task<DevBridgeRecipeRunResult> RunAsync(
            string recipeId,
            string? workflowId,
            DevBridgeRecipeExecutionContext? executionContext,
            CancellationToken cancellationToken = default)
        {
            RunCalls.Add(recipeId);
            ExecutionContexts.Add(executionContext);
            int index = RunResults.Count;
            DevBridgeRecipeRunResult result = RunFactory is not null
                ? RunFactory(recipeId, workflowId, executionContext, index)
                : Runs.TryGetValue(recipeId, out DevBridgeRecipeRunResult? configured)
                    ? configured
                    : InfrastructureRun(recipeId, "FAKE_RECIPE_NOT_CONFIGURED");
            RunResults.Add(result);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLeaseAdapter : IDevBridgeLeaseAdapter
    {
        public Queue<DevBridgeLeaseResult> BeginResults { get; } = [];

        public Queue<DevBridgeLeaseResult> RenewResults { get; } = [];

        public DevBridgeLeaseResult BeginResult { get; init; } = SuccessLease("lease-default", 7);

        public DevBridgeLeaseResult? EndResult { get; set; }

        public int BeginCalls { get; private set; }

        public int RenewCalls { get; private set; }

        public int EndCalls { get; private set; }

        private int CurrentGeneration { get; set; } = 7;

        public Task<DevBridgeLeaseResult> BeginLeaseAsync(
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            BeginCalls++;
            DevBridgeLeaseResult result = BeginResults.Count > 0
                ? BeginResults.Dequeue()
                : BeginResult;
            if (result.Generation is > 0)
            {
                CurrentGeneration = result.Generation.Value;
            }

            return Task.FromResult(result);
        }

        public Task<DevBridgeLeaseResult> RenewLeaseAsync(
            string leaseId,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            RenewCalls++;
            return Task.FromResult(
                RenewResults.Count > 0
                    ? RenewResults.Dequeue()
                    : SuccessLease(leaseId, CurrentGeneration));
        }

        public Task<DevBridgeLeaseResult> EndLeaseAsync(
            string leaseId,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            EndCalls++;
            return Task.FromResult(EndResult ?? SuccessLease(leaseId, CurrentGeneration));
        }
    }

    private sealed record ResetCall(
        string RecipeId,
        string LeaseId,
        int Generation,
        string? WorkflowId);

    private sealed class FakeResetAdapter : IDevBridgeFixtureResetAdapter
    {
        public List<ResetCall> Calls { get; } = [];

        public DevBridgeResetResult Result { get; init; } =
            SuccessfulReset("lease-default", 7);

        public Task<DevBridgeResetResult> ResetAsync(
            string resetRecipeId,
            string leaseId,
            int expectedGeneration,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ResetCall(resetRecipeId, leaseId, expectedGeneration, workflowId));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeFreshGenerationAdapter : IDevBridgeFreshGenerationAdapter
    {
        private readonly Queue<int> generations;

        public FakeFreshGenerationAdapter(params int[] generations)
        {
            this.generations = new Queue<int>(generations);
        }

        public List<(string RecipeId, int? PreviousGeneration, string? WorkflowId)> Calls { get; } = [];

        public Task<DevBridgeFreshGenerationResult> EnsureFreshGenerationAsync(
            string recipeId,
            int? previousGeneration,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((recipeId, previousGeneration, workflowId));
            if (generations.Count == 0)
            {
                return Task.FromResult(new DevBridgeFreshGenerationResult(
                    new DevBridgeAdapterStatus(
                        DevBridgeOutcomeKind.InfrastructureFailure,
                        "FAKE_FRESH_GENERATION_EXHAUSTED"),
                    null));
            }

            return Task.FromResult(new DevBridgeFreshGenerationResult(
                new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                generations.Dequeue(),
                1));
        }
    }

    private sealed class FakeModDevelopmentAdapter : IDevBridgeModDevelopmentAdapter
    {
        public List<(string Project, string SourceFingerprint, string? WorkflowId)> Calls { get; } = [];

        public DevBridgeModDevelopmentResult? Result { get; set; }

        public Func<string, string?, DevBridgeModDevelopmentResult>? Factory { get; set; }

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((project, sourceFingerprint, workflowId));
            if (Factory is not null)
            {
                return Task.FromResult(Factory(sourceFingerprint, workflowId));
            }

            if (Result is not null)
            {
                return Task.FromResult(Result);
            }

            return Task.FromResult(
                new DevBridgeModDevelopmentResult(
                    project,
                    new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
                    true,
                    "tx-1",
                    workflowId,
                    7,
                    "lease-00000000000000000000000000000001",
                    new DevBridgeArtifactFreshness(
                        sourceFingerprint,
                        new string('a', 64),
                        new string('a', 64),
                        "unchanged",
                        7,
                        7,
                        7,
                        true,
                        "test-proof",
                        "tx-1",
                        workflowId,
                        "lease-00000000000000000000000000000001")));
        }
    }
    private sealed record AffectedScenarioResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        IReadOnlyList<(string Project, string SourceFingerprint, string? WorkflowId)> DevelopmentCalls,
        IReadOnlyList<string> RecipeCalls,
        int FreshGenerationCalls);

    private sealed class FakeImpactAdapter : IRimContextImpactAdapter
    {
        private readonly RimContextImpactResult result;

        public FakeImpactAdapter(RimContextImpactResult result)
        {
            this.result = result;
        }

        public IReadOnlyList<string> ChangedPaths { get; private set; } = [];

        public Task<RimContextImpactResult> AffectedAsync(
            IReadOnlyList<string> changedPaths,
            CancellationToken cancellationToken = default) =>
            RecordAndReturn(changedPaths);

        private Task<RimContextImpactResult> RecordAndReturn(
            IReadOnlyList<string> changedPaths)
        {
            ChangedPaths = changedPaths.ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeGitChangeProvider : IGitChangeProvider
    {
        private readonly GitChangeDiscoveryResult result;

        public FakeGitChangeProvider(GitChangeDiscoveryResult result)
        {
            this.result = result;
        }

        public List<(string Root, string? Base)> Calls { get; } = [];

        public Task<GitChangeDiscoveryResult> DiscoverAsync(
            string rootPath,
            string? baseReference = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((rootPath, baseReference));
            return Task.FromResult(result);
        }
    }

    private sealed class FixedGitRepositoryStateProvider : IGitRepositoryStateProvider
    {
        private readonly GitRepositoryStateResult result;

        public FixedGitRepositoryStateProvider(GitRepositoryStateResult result)
        {
            this.result = result;
        }

        public Task<GitRepositoryStateResult> ReadAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class PathGitRepositoryStateProvider : IGitRepositoryStateProvider
    {
        private readonly IReadOnlyDictionary<string, GitRepositoryStateResult> results;

        public PathGitRepositoryStateProvider(
            IReadOnlyDictionary<string, GitRepositoryStateResult> results)
        {
            this.results = results;
        }

        public Task<GitRepositoryStateResult> ReadAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            string fullPath = Path.GetFullPath(rootPath);
            return Task.FromResult(results.TryGetValue(fullPath, out GitRepositoryStateResult? result)
                ? result
                : new GitRepositoryStateResult(
                    false,
                    ErrorCode: "GIT_FIXTURE_NOT_CONFIGURED",
                    Error: "No Git fixture was configured for the requested root."));
        }
    }

    private sealed class SequencedGitRepositoryStateProvider : IGitRepositoryStateProvider
    {
        private readonly Queue<GitRepositoryStateResult> results;

        public SequencedGitRepositoryStateProvider(params GitRepositoryStateResult[] results)
        {
            this.results = new Queue<GitRepositoryStateResult>(results);
        }

        public Task<GitRepositoryStateResult> ReadAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            ReadWorktreeAsync(rootPath, cancellationToken);

        public Task<GitRepositoryStateResult> ReadWorktreeAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            if (results.Count == 0)
            {
                throw new InvalidOperationException("No worktree snapshot remains.");
            }

            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class FakeRimErrorDiagnosisAdapter : IRimErrorDiagnosisAdapter
    {
        private readonly RimErrorDiagnosisResult result;

        public FakeRimErrorDiagnosisAdapter(RimErrorDiagnosisResult result)
        {
            this.result = result;
        }

        public int Calls { get; private set; }

        public RimErrorDiagnosisRequest? Request { get; private set; }

        public RimErrorDiagnosisResult Result => result;

        public Task<RimErrorDiagnosisResult> DiagnoseAsync(
            RimErrorDiagnosisRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeDiagnosticSourceAdapter : IDevBridgeDiagnosticSourceAdapter
    {
        private readonly DevBridgeDiagnosticSourceResult result;

        public FakeDiagnosticSourceAdapter(DevBridgeDiagnosticSourceResult result)
        {
            this.result = result;
        }

        public int Calls { get; private set; }

        public string? TestId { get; private set; }

        public string? RunId { get; private set; }

        public Task<DevBridgeDiagnosticSourceResult> AcquireAsync(
            string testId,
            DevBridgeRecipeRunResult run,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            TestId = testId;
            RunId = run.RunId;
            return Task.FromResult(result);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
