# Telemetry policy catalogue

Use this reference when designing or changing reusable telemetry support.
Each entry marked proposed is a design option, not an available API or a
requirement for every test. Select the obligation first, then the evidence and
the smallest suitable native MSTest mechanism.

## Contents

- [Selection and composition](#selection-and-composition)
- [Capture and signal semantics](#capture-and-signal-semantics)
- [Ingestion and persistence](#ingestion-and-persistence)
- [Investigation](#investigation)
- [Delivery and lifecycle](#delivery-and-lifecycle)
- [Architecture relationships](#architecture-relationships)
- [Data providers](#data-providers)

## Selection and composition

Keep expected application values, producer selection and observation windows
in the consuming scenario. Reuse published registry and API definitions while
deriving expected outcomes independently of the implementation under test.

A conformance decision requires relevant, attributable evidence. Distinguish a
violated obligation from insufficient evidence; neither can produce a passing
conformance result. Require only the fields and signals promised by that scenario.

Use an assertion extension for a repeated expectation, native data mechanisms
for repeated inputs and execution composition for an invocation-wide obligation.
Passive policy attributes share one executor; they do not each rerun the test.
Inspect the resolved MSTest APIs and the existing
[ArchitectureAttribute](../../../Architecture/ArchitectureAttribute.cs) and
[method executor](../../../Architecture/ArchitectureTestMethodAttribute.cs).
[QylTelemetry](../../../Architecture/QylTelemetry.cs) is an investigation reader.

When implementing an entry, replace its proposed status and empty implementation
with links to the actual declaration and its consuming executor or assertion.
Read those sources before reuse; a stale catalogue is not API authority.

## Capture and signal semantics

### RequiresTelemetryEvidence

Status: proposed. Implementation: none.
- Applies when: a scenario needs a declared set of signals and terminal observations.
- Evidence: selected producers, resource/scope identities, required signals and a bounded completion condition.
- Decision: require attributable, complete observations before evaluating dependent policies. Empty or incomplete required capture is insufficient evidence.

### ConformsToSemanticConventions

Status: proposed. Implementation: none.
- Applies when: selected telemetry definitions promise semantic conformance.
- Evidence: the resolved registry revision, selected definitions and applicable requirements, with actual typed values and units.
- Decision: reject violations of those requirements while permitting legitimate application-defined names. Do not infer conformance from an empty selection.

### PreservesNativeSpanOwnership

Status: proposed. Implementation: none.
- Applies when: Qyl augments a native instrumentation source.
- Evidence: expected source, operation, span kind, identities and relevant relationships, with a completed observation window.
- Decision: require the original native span without replacement or duplicate instrumentation. A total span count alone does not establish ownership.

### PreservesTraceContext

Status: proposed. Implementation: none.
- Applies when: an operation crosses a declared propagation boundary.
- Evidence: expected parent/child and link relationships, producer identities and boundary observations.
- Decision: require the selected relationships and identify broken propagation or incorrect parentage. Select session propagation separately when it is part of the contract.

### HonorsCaptureSelection

Status: proposed. Implementation: none.
- Applies when: producer configuration selects HTTP headers or gRPC metadata.
- Evidence: selected and unselected fields, actual values, multiplicity, punctuation, normalized names and relevant metadata phases.
- Decision: retain the selected fields according to the resolved producer contract and expose accidental overcapture or loss. Binary metadata follows its documented representation.

### PreservesOtlpValues

Status: proposed. Implementation: none.
- Applies when: a named boundary promises typed OTLP fidelity.
- Evidence: independent expected integers, doubles, arrays, bytes, timestamps, resources, scopes, events or links, and observations on the relevant sides.
- Decision: preserve the selected types, values and field presence. Judge documented normalization separately from a promise of exact preservation.

### PreservesMetricSemantics

Status: proposed. Implementation: none.
- Applies when: a scenario promises metric identity, point fidelity or documented aggregation.
- Evidence: explicit producer, resource/scope and series identity, measurement window, correlation method, and the applicable point types, units and temporality.
- Decision: establish exact original-point fidelity separately from documented aggregation or approximation. Select histograms, summaries and exemplars only when relevant; a trace ID alone does not identify an aggregate metric.

## Ingestion and persistence

### HonorsOtlpRejectionContract

Status: proposed. Implementation: none.
- Applies when: an ingestion batch contains rejected records or exceeds a declared limit.
- Evidence: controlled valid and invalid records, the actual acknowledgment, rejected-record counts and subsequent observations of accepted records.
- Decision: match the resolved rejection contract and preserve accepted records. Transport success alone cannot establish complete ingestion.

### AppliesAttributePersistencePolicy

Status: proposed. Implementation: none.
- Applies when: the Collector retains, denies or normalizes attributes.
- Evidence: the policy for the selected Collector artifact, submitted attributes, persisted observations and actual readback.
- Decision: require allowed retention and documented normalization, and reject prohibited retention or unintended loss. Respect valid selected captured-field families when that policy preserves them; credential-like names are not a universal denial rule.

### SanitizesExceptionEvidence

Status: proposed. Implementation: none.
- Applies when: the selected artifact promises sanitization of exception fields.
- Evidence: policy-selected exception fields, independent redaction expectations, useful text to preserve, size bounds and failure representation.
- Decision: apply the bounded sanitizer's actual promise while preserving useful evidence. Do not broaden this into a blanket NoSensitiveData or universal PII-removal claim.

### IsolatesProjectData

Status: proposed. Implementation: none.
- Applies when: ingestion, reads or scoped handles are partitioned by project identity.
- Evidence: distinct project identities, deliberately overlapping telemetry identifiers and operations through the actual scoped entrypoints.
- Decision: each project can access its own permitted data without exposing or mutating another project's data.

### PreservesTelemetryAcrossRestart

Status: proposed. Implementation: none.
- Applies when: the Collector promises durable retained telemetry across reopening.
- Evidence: controlled persisted records, store identity, actual shutdown/restart and readback before and after reopening.
- Decision: preserve the expected durable evidence according to retention policy. Observations made only before restart do not establish durability.

## Investigation

### ConformsToApiContract

Status: proposed. Implementation: none.
- Applies when: a running product exposes a selected public API operation.
- Evidence: the resolved contract revision and actual response, error, pagination or stream shapes through the selected interface.
- Decision: require the applicable public contract. Generated models alone do not establish the behavior of the running endpoint.

### PreservesQueryPagination

Status: proposed. Implementation: none.
- Applies when: an investigation spans multiple pages.
- Evidence: a controlled expected dataset, filters, time window, continuation tokens and all selected pages.
- Decision: preserve documented continuation semantics and ordering, with no missing or duplicated expected results. Preserve filters and windows across page requests.

### ReportsInvestigationCoverage

Status: proposed. Implementation: none.
- Applies when: an investigation describes the completeness of its observations.
- Evidence: known input coverage, matched evidence, contextual ancestors, truncation and known or unknown counts.
- Decision: report partial observations and unknown counts accurately. Require evidence for any completeness claim.

### EnforcesOptimisticConcurrency

Status: proposed. Implementation: none.
- Applies when: writable investigation resources require concurrency preconditions.
- Evidence: controlled resource revisions, missing/stale/current preconditions and actual write responses and resulting state.
- Decision: enforce the selected precondition contract and prevent silent overwrites of annotations, saved queries or failure state.

## Delivery and lifecycle

### CompletesGracefulShutdown

Status: proposed. Implementation: none.
- Applies when: shutdown promises final exports and release of owned resources.
- Evidence: required terminal exports, dependency ownership, process handles and a bounded shutdown deadline.
- Decision: finish required exports, stop dependencies in the needed order and join child processes within the deadline. Preserve shutdown failures alongside an earlier scenario failure.

### UsesCandidatePackages

Status: proposed. Implementation: none.
- Applies when: delivery evidence must exercise designated candidate packages.
- Evidence: the candidate manifest and actual resolved package IDs, versions, sources and content hashes.
- Decision: match the designated candidates without silent substitution by project references or public releases. A cache hit is acceptable only when its contents and provenance satisfy the selected manifest.

### RunsPublishedNativeArtifact

Status: proposed. Implementation: none.
- Applies when: a scenario claims execution of a designated published NativeAOT artifact.
- Evidence: artifact role, path, RID and hash, with the actual launched executable and observed behavior.
- Decision: require execution of that artifact. Publish success or managed execution cannot establish native behavior. Select application and Collector roles explicitly.

## Architecture relationships

### ServiceOwnedStores

Status: proposed. Implementation: none.
- Applies when: the exercised services promise separate store ownership.
- Evidence: declared service/store ownership and observed attributable store operations for every required service.
- Decision: require the selected ownership relationships without foreign or shared access that violates the declaration. Missing required service/store observations are insufficient evidence.

### SharedStore

Status: proposed. Implementation: none.
- Applies when: declared services intentionally share a store.
- Evidence: the expected service set, selected shared-store identity and attributable operations from each required service.
- Decision: require the declared shared relationship within the exercised observations. Store-shaped spans establish observed relationships; business persistence requires actual stored business data and readback.

[Monolith, Microservices and DistributedMonolith](../../../Architecture/ArchitectureAttribute.cs)
are fixture profiles. Keep their verdicts scoped to measured behavior. The
[Orders workload store](../../../../Qyl.Telemetry.EndToEndTests/Applications/Orders.Api/Store.cs)
is a telemetry model; Collector persistence is a separate storage contract.

## Data providers

### OtlpTransportData

Status: proposed. Implementation: none.
- Applies when: several scenarios reuse the same named OTLP transport profiles.
- Inputs: immutable transport and endpoint-configuration descriptions using the supported protocol options.
- Behavior: enumerate independent named cases through native MSTest data mechanisms. Acquire receivers and other mutable resources during invocation.

### CapturedFieldData

Status: proposed. Implementation: none.
- Applies when: repeated scenarios share captured-field input profiles.
- Inputs: immutable selection, name, value multiplicity, binary representation and phase descriptions relevant to the contract.
- Behavior: enumerate meaningful named cases without changing adversarial values or acquiring invocation resources.

Use DataRow or DynamicData for ordinary cases. Add a custom provider when its
repeated domain inputs justify a reusable data contract.
