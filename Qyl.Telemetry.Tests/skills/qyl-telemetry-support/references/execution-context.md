# Qyl package and execution context

Use this reference when a change depends on package APIs, production policy or
delivery evidence. Resolve only the relevant inputs and reuse them until they
change. The names below describe task context, not environment variables.

- **Root and project:** `Qyl.Telemetry.Tests.slnx` identifies this repository's
  support projects and three executable suites. The support projects supply reusable primitives; the three
  consuming suites own executable cases and lifecycle registration. Ownership
  and production contract sources are mapped in [port ownership](../../../../docs/port.md).
- **Stack:** `global.json` selects the .NET SDK, MSTest.Sdk and native MTP runner.
  `Directory.Build.props` selects the AllMicrosoft extension profile.
  `Version.props`, `Directory.Packages.props`, the owning project and its imports
  select dependencies; `NuGet.Config` selects feeds. Current restored assets
  establish resolved MSTest, MTP and package APIs. Refresh stale assets through
  the normal entrypoint when executable work requires them. MSTest.Sdk and MTP
  have separate version lines.
- **Contracts:** use registry, protocol, API and persistence-policy source
  revisions matching the selected packages and artifacts. Receiver capture
  establishes raw and typed evidence; Collector retention and MCP read behavior
  belong to their respective production owners.
- **Execution:** inspect fixture launchers and delivery inputs for actual package
  origins, artifact roles, runtime identifiers and investigation transport.
  Managed Collector execution, published NativeAOT execution, REST investigation
  and MCP protocol invocation establish different evidence. The current delivery
  baseline is recorded in [README.md](../../../../README.md#delivery-evidence).
  The Collector commit and the npm identity live in the end-to-end suite's
  `Delivery/inputs.json`; package versions come from `Version.props`, and the
  resulting manifest records restored package and executable hashes. A local production checkout is not an implicit input.
- **Commands:** README.md documents focused native MTP commands. `build.sh` and
  CI define the solution entrypoint and required checks.

Keep version snapshots and absolute machine paths out of skills. Published
registry constants and typed enum selectors prevent drift; define repeated
scenario-owned keys once without changing adversarial inputs. Data enumeration
describes immutable cases; invocation fixtures own mutable resources. A row's
cancellation must not cancel shared publication or prevent required teardown.

Conformance requires attributable observations. Insufficient evidence cannot
pass; [telemetry policies](telemetry-policies.md) enforce this. Metric
attribution needs producer, resource/series identity, measurement window and
applicable correlation; a trace ID alone does not identify an aggregate metric.
