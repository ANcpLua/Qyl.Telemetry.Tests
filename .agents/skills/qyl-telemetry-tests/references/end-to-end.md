# Qyl.Telemetry.EndToEndTests

Own retained prototype applications and complete Qyl scenarios. The intended
path is candidate Qyl libraries in those applications, an actually published
Native AOT Qyl executable, then retrieval and verification through the intended
C# MCP investigation interface. Exercise the
application's actual Qyl registration, dependencies and public behavior through
the delivery path under test. Do not recreate every old prototype scenario
merely because it exists; retain scenarios that distinguish an assembled-stack
contract from component coverage.

For candidate-package evidence, pack the candidate before restoring the consumer
from its deliberate feed. Verify resolved package identity; a project reference
or cached public release must not silently replace that candidate. Keep source,
candidate-package and released-package observations distinct. A candidate test
need not publish to a public registry.

An expensive immutable publication can be shared under a native fixture. Each
selectable data row owns its child process, configuration, evidence partition
and workspace. Carry invocation cancellation to cancellable process waits and
join/reap children before removing their files. Readiness and bounded
condition-based observation replace fixed sleeps. Sequential publish/restore/
launch dependencies remain sequential even when implemented asynchronously.

Assert native operation identities, uniqueness, source, kind/status and required
parent relationships. Do not create replacement spans to satisfy the verifier.
Check relevant traces, logs, metrics or events only when the scenario promises
them; trace identity alone does not partition an aggregate metric. Missing
required evidence fails with diagnostics rather than skipping an assertion.

Generated discovery, managed execution and Native AOT execution are separate
claims. An AOT scenario publishes for the actual RID and runs that binary;
a compiler error, startup crash or publish exit code alone proves no test
assertion. CLR profiler injection is not available to Native AOT. SDK registration
names do not decide AOT compatibility. Use [Qyl stack](qyl-stack.md) for producer
ownership and preserve the test runner's native outcome and failure reporting.

Inspect the actual MCP boundary before implementation. The existing prototype's
`QylTelemetry` reader calls REST routes, and its collector fixture launches a
managed DLL. They provide useful existing evidence but do not yet fulfill this
new MCP/native-artifact contract. A REST analysis request is not by itself an
MCP protocol invocation, even when both reach the same C# analysis engine.
Record whether a scenario exercises the C# investigation backend, an SDK wrapper
or the actual MCP transport and server; name the claim accordingly.

Assert the final scenario through its promised read path. A direct DuckDB query
or receiver snapshot can supply diagnostics or a separate assertion, but does
not substitute for an MCP response when investigation is under test. Exercise
relevant missing/partial/redacted evidence and meaningful error results as well
as the successful query; do not manufacture a successful result when evidence
is absent. Full MCP transport needs coverage when that transport is the contract,
not in every scenario that only tests a C# investigation operation.

Record producer package IDs/versions/feed or hashes and native executable
version/RID/hash. Candidate instrumentation against a released collector proves
that combination. Changes to Collector storage or investigation require the
corresponding candidate Collector artifact, not only candidate producer packages.
A test runner or workload application need not be AOT solely because the
Collector artifact is AOT. Publish the artifacts whose native behavior is claimed.
