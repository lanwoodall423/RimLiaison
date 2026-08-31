# RimLiaison Ordinary-Agent Acceptance

The ordinary-agent gate separates production evidence from deliberate fault injection.

## Required evidence

- **Healthy production proof:** one real `rimliaison affected --run --json` workflow with fresh deployed artifact evidence.
- **Project failure classification:** real project-owned failures normalize to `MOD_FAILURE`; they must not become `TOOLCHAIN_FATAL`.
- **Managed restart/recovery proof:** a supported lifecycle workflow demonstrates bounded restart, readiness, generation, and cleanup evidence.
- **Concurrency proof:** bounded concurrent workflows preserve workflow, project, lease, and artifact identities.
- **Fault-injection recovery proof:** deterministic/process-level test-harness tests exercise the same production recovery service used by doctor, affected validation, and release artifact workflows.

Fault-injection evidence may be recorded as `SATISFIED_BY_TEST_HARNESS`. It is not a production workflow requirement.

## Shared recovery coverage

`DevBridgeCapabilityRecovery` is the shared implementation. Its coverage includes:

- no structured response and stale/unavailable coordinator handling;
- reconnect/reconcile recovery;
- coordinator recycle;
- full managed runtime reset and controlled RimWorld restart;
- generation and readiness verification;
- lease cleanup and bounded retry behavior;
- exhausted recovery resulting in `TOOLCHAIN_FATAL`.

The deterministic coverage is in `tests/RimLiaison.Tests/ManagedRuntimeEscalationTests.cs` and
`tests/RimLiaison.Tests/ProductionExecutionTests.cs`. Doctor invokes the service from
`RimTestDoctorRunner`; affected and release artifact workflows invoke it through
`ArtifactFreshnessTransaction`.

`DEVBRIDGE_NO_STRUCTURED_RESPONSE` is already covered by the process-transport recovery tests,
including coordinator recycle, bounded evidence, and artifact-transaction retry. No additional
implementation or test is required for this correction.

## Build ownership and transparent recovery

Every development build carries explicit `buildOwnerType`, `buildOwnerProject`,
`buildTarget`, `buildSourceRoot`, `buildCommandIdentity`, and `buildEvidenceId` metadata.
`PROJECT_BUILD` failures are project-owned and normalize to `MOD_FAILURE`; toolchain,
runtime-materialization, and test-harness build failures enter the existing bounded
`DevBridgeCapabilityRecovery` path and normalize to `TOOLCHAIN_FATAL` only when recovery
cannot establish readiness. Build command, exit code, bounded diagnostics, and build
duration remain attached to the failure.

Ordinary agents do not need to run `preflight` manually. `affected --run` owns its
preflight and recovery path; a failed project build stops before deployment and does not
trigger toolchain recovery. The only ordinary outcomes are `PASS`, `MOD_FAILURE`, and
`TOOLCHAIN_FATAL`.

## Production fault-injection disposition

The production CLI intentionally exposes no command to crash the coordinator, suppress a response,
break an endpoint, corrupt a lease, or force a timeout. Adding such controls solely for acceptance
would weaken the production safety boundary. Test-only fault injection remains test-only.

## Closure 2 interpretation

Closure 2 passes when production doctor is `READY`, the healthy and project matrix evidence is
valid, managed restart and bounded concurrency pass, qualification and CI pass, deterministic tests
pass, and fault cases are satisfied by the test harness rather than induced in production.

After acceptance passes, architecture is frozen for the reliability campaign. The campaign counts
clean `PASS`, recovered `PASS`, `MOD_FAILURE`, and `TOOLCHAIN_FATAL` separately; `MOD_FAILURE` and
successful internal recovery do not break the project-workflow streak, while a tooling-caused
`TOOLCHAIN_FATAL` does.
