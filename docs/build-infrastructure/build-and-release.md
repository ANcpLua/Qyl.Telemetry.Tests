# Build and package mechanics

## Current entrypoints

`build.sh` restores the pinned JetBrains local tool and launches
`eng/build/_build.csproj`. The build uses Fallout; its default `Check` target
compiles the solution once, runs the three native MTP suites with Microsoft
coverage, and runs InspectCode. `Test`, `Coverage` and `InspectCode` are separate
targets when only that evidence is needed.

The build host restores its own dependencies. `Compile` restores and builds
`Qyl.Telemetry.Tests.slnx`; subsequent test and inspection steps reuse those
outputs. `eng/inspection.slnx` includes the four projects, their two application
fixtures and the build host. Local tool versions are pinned in
`.config/dotnet-tools.json`; package versions are owned by `Version.props`.

Native per-application TRX and Cobertura files are retained in
`artifacts/results`. The coverage extension chooses distinct report filenames
for each application. DotCov merges them into `artifacts/coverage/coverage.json`;
only top-level reports are merged, excluding the duplicate TRX attachment copies.
coverage is informational, with no invented percentage threshold. A run with no
coverage measurements fails. InspectCode writes `artifacts/inspection.sarif` and
fails the target when it reports any INFO-or-higher findings. Machine-global
settings layers are disabled so developer and CI inspection settings agree.

## Restore assets and execution

Restore depends on project/import inputs, framework, configuration, RID, package
sources and cache locations. `--no-restore` uses existing restore assets;
`--no-build` uses existing compiled outputs. Neither flag establishes that the
artifacts match the current inputs.

Project references establish build dependencies. Unrelated stages can overlap,
while stages consuming an output depend on its completion. Shared mutable
intermediates can create races even when processes have different final output
folders.

## Test and consumer boundaries

| Boundary | What executes |
| --- | --- |
| Pure logic | In-memory assertion, filtering or evidence processing |
| Component interaction | Receivers, transports, exporters and lifecycle behavior |
| Assembled application | Applications and their external components |
| Public package consumer | Outputs and dependencies restored from the public package |

A test filename does not establish its boundary. A model-named test can still
start an HTTP receiver. Deliberate failure probes produce expected failed child
results; those results differ from the outcome of their parent verification
suite.

MSTest discovery, execution and structured reporting expose different evidence.
Source generation, managed execution and execution of a Native AOT artifact
also represent distinct mechanisms.

## Package and release artifacts

Packing produces a package containing assemblies, dependency metadata and other
payload. Package verification can observe contents and restore behavior that a
source build does not exercise. Any future package for the shared library must
carry the proto license and notices alongside its assemblies.

Publication uploads an artifact. Indexing makes its version discoverable through
registry metadata. Consumer execution observes the restored artifact's behavior.
A release can therefore have different publication, indexing and consumer
verification states.

This repository currently has `IsPackable=false` and no release command. Its
shared library is consumed by project references from the three suites.
`Qyl.Telemetry.Testing` 3.0.3 remains a separate published historical package.
Its package smoke and publication scripts are retained in the archived source;
they have not been copied as a working release path for this repository.
