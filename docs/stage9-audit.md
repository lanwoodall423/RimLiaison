# Stage 9 production audit

The audit matrix is implemented by `ProductionReadinessAuditTests` and uses the real streaming file path. Output bytes are UTF-8 bytes of the normal agent-facing JSON; baseline scenarios use compact `compare --json` semantics.

| scenario | input | raw | unique | roots | output | raw/output | ingest ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| clean startup + baseline warnings | 248 | 3 | 3 | 0 | 48 | 5.2x | 123.64 |
| new missing Def | 117 | 1 | 1 | 1 | 190 | 0.6x | 1.19 |
| repeated runtime (10,000) | 2,056,770 | 10,000 | 1 | 1 | 202 | 10,182.0x | 979.64 |
| initialization + downstream (3,004) | 513,893 | 3,004 | 4 | 1 | 202 | 2,544.0x | 120.39 |
| Harmony missing target | 84 | 1 | 1 | 1 | 185 | 0.5x | 2.13 |
| compiler/build | 349 | 4 | 4 | 3 | 411 | 0.8x | 1.28 |
| missing asset | 81 | 1 | 1 | 1 | 191 | 0.4x | 0.71 |
| RimBridge operation correlation | 116 | 1 | 1 | 1 | 267 | 0.4x | 0.91 |
| unchanged baseline, transient values | 207 | 1 | 1 | 1 | 48 | 4.3x | 0.74 |
| resolved diagnostic | 25 | 1 | 1 | 0 | 61 | 0.4x | 0.64 |
| independent errors | 412 | 4 | 4 | 4 | 601 | 0.7x | 0.83 |
| malformed/truncated + huge line | 100,230 | 4 | 4 | 1 | 148 | 677.2x | 13.32 |

Stress result: 1,000,000 repeated lines, 49,000,000 input bytes, one stored diagnostic, 42-byte latest output, 15,998 ms ingestion on the audit machine. Compiled deterministic regexes reduced this from 39,532 ms in the initial profile.

Reliability checks cover malformed stores, Unicode, Windows paths, partial lines/stacks, unique-diagnostic caps, baseline compatibility, repeated ingestion, source-index cache invalidation, conservative causal grouping, and optional/mismatched bridge metadata. The store uses atomic temporary replacement but does not coordinate multiple concurrent writers.

Known limitations: text framing remains conservative for undocumented log formats; exact source lines require index evidence; baseline filtering is separate from `latest`; concurrent writers and live log rotation are not a shared-state protocol. External bridge assemblies are not required.
