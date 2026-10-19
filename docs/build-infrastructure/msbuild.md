# MSBuild mechanics

## Imports and evaluation

`Microsoft.Common.props` searches upward from a project for the nearest
`Directory.Build.props` and imports it early. A project body can override
properties assigned earlier. `Microsoft.Common.targets` imports the nearest
`Directory.Build.targets` later; subsequent imports can still affect values.

An inner Directory.Build file stops the automatic parent search. Explicit
imports compose multiple levels. The same nearest-file principle applies
independently to props and targets.

Properties and items outside targets evaluate while the project and its imports
are read. Targets execute afterward. Consequently, a `TargetFramework` assigned
in the project body can be empty in an earlier import, and an evaluation-time
file glob cannot include a file first created during target execution.

## Properties and items

| Construct | Effect |
| --- | --- |
| Property assignment | Sets a scalar value at that point in evaluation or execution |
| `Include` | Adds an item |
| `Update` | Changes metadata on matching existing items |
| `Remove` | Removes matching items |
| `Condition` | Controls whether the associated declaration or operation applies |

`MSBuildThisFileDirectory` identifies the containing imported file's directory.
`MSBuildProjectDirectory` identifies the consuming project's directory. Their
values differ when configuration is shared through imports.

## Output paths

Intermediate and final output paths can vary with project, configuration,
framework and runtime identifier. Two builds writing the same intermediate files
can interfere even when their final publish directories differ.

The SDK artifact layout supports project-separated output under a shared root:

```xml
<PropertyGroup>
  <UseArtifactsOutput>true</UseArtifactsOutput>
  <ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
</PropertyGroup>
```

Explicit path overrides and SDK defaults participate in evaluation. Consumers
that construct a `bin/Release/net10.0` path do not automatically follow a changed
layout. `GetTargetPath` exposes a project's evaluated target output through
MSBuild. Child test applications can be deployed through build-time items or
evaluated target metadata without runtime reflection over build configuration.

The intentionally failing telemetry invocation probe is not a solution test
project. Its parent sets `ShouldUnsetParentConfigurationAndPlatform=false` so
MSBuild preserves the selected configuration for that out-of-solution reference.
`CopyTestFixturePayloads` obtains the child's output through `GetTargetPath` and
deploys its complete runnable payload; it does not construct a `bin/Release` path.

## Targets and generated files

`DependsOnTargets` expresses prerequisite targets; `BeforeTargets` and
`AfterTargets` attach behavior to named extension points. Target declaration
order alone does not define execution order. `Inputs` and `Outputs` support
incremental execution through the declared dependency relationship.

Custom generated source can participate in compilation through a `Compile` item;
`FileWrites` participates in cleanup accounting. `IntermediateOutputPath`
reflects the evaluated intermediate directory. Item registration and file
creation are distinct: compilation still needs the item when an incremental
generation step is skipped.

The existing protobuf files use `Protobuf` items and `Grpc.Tools`, which supplies
its own generation integration. Roslyn source generators have a different
compiler-managed output mechanism.

## Diagnostic representations

A preprocessed project expands imports and helps explain assignment order.
Evaluated-property and item queries expose selected values. Binary logs capture
build execution, including task activity and project relationships. A target's
inclusive duration can contain time waiting for dependencies; it is not the
same measurement as task self-time.
