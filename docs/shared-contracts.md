# Shared evidence contracts

`RimDev.Contracts` owns the dependency-free, versioned exchange shapes used by the RimWorld
AI-development tools. It contains identity, evidence, validation-requirement, entity-reference,
and lightweight tool-event contracts only. It does not launch RimWorld, select tests, diagnose
failures, query storage, or implement RimContext, RimContent, RimTest, RimError, or DevBridge logic.

## Identity semantics

`rimdev-execution-identity/v1` carries repository/project, source revision and fingerprint, source
inputs and dependencies, build/artifact/deployment identity, runtime generation/instance, execution
and test IDs, and tool/configuration/environment versions. Omitted values are unknown; consumers
must not infer them. `ExecutionIdentityComparer` returns:

- `exact`: all represented fields agree;
- `compatible`: no conflict, but an optional field is absent on one side;
- `mismatch`: a represented field differs;
- `insufficient`: a required identity dimension is absent or the schema is unsupported.

Validation applicability requires matching source identity. Runtime applicability additionally
requires artifact hash, deployment identity, and process generation. Thus an older runtime record
cannot validate a newer source, artifact, deployment, or process generation.

## Evidence and requirements

`rimdev-evidence/v1` is a compact envelope with a stable evidence ID, producer/type, identity,
UTC timestamp, subject references, status, bounded structured payload, optional artifact reference,
and provenance. Large payloads are replaced with a bounded omission marker and SHA-256 digest;
detailed logs remain in owner storage and are referenced rather than embedded.

`rimdev-validation-requirement/v1` lets content or planning producers state a subject, assertion,
preferred evidence level, static/runtime policy, runtime condition, prerequisites, severity, and
provenance. It contains no tool command or workflow choice.

## Dependency direction and compatibility

`RimContext.Core`, `RimError.Core`, and `RimLiaison.Cli` reference `RimDev.Contracts`; the shared
assembly references no tool implementation. Existing `rimliaison-validation-evidence/v1`, suite
results, DevBridge freshness fields, and `rimliaison-agent-event/v2` remain compatible wire
surfaces. RimLiaison adapters project those records into the shared contracts while preserving the
legacy fields. RimLiaison's in-process result/freshness adapters project the shared execution
identity without expanding compact legacy wire responses, and observability normalizes event
payloads through the bounded shared event envelope.

The shared layer is not an orchestration subsystem or a second database. Owner-specific stores and
transports remain authoritative.
