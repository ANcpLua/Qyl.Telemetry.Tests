---
name: qyl-telemetry-tests
description: "Binding rules for every test in the qyl family on MSTest 4.5.1 with the native Microsoft.Testing.Platform 2.5.1 runner: assertions chosen by their exact semantics, NativeAOT first, no VSTest, no weakened tests; and the test-code rules (the name is the specification, one behavior per test, DataRow constants, no try/catch/finally, time through TimeProvider, the gate decides). Load before writing, changing or reviewing any test in Qyl.Telemetry.Tests or in a repository that consumes it."
---

# MSTest: assertions, equality semantics and NativeAOT

Stack: .NET 10 (SDK 10.0.402), MSTest.Sdk 4.5.1, Microsoft.Testing.Platform 2.5.1 as the runner. Every claim below was verified against the installed 4.5.1 packages on 2026-10-22 (MSTest.TestFramework.xml, MSTest.TestAdapter.targets, MSTest.Sdk/Sdk.targets, MSTest.Analyzers).

Distinguish strictly between **binding requirements**, **preferences** and **what is not required**. A preference never narrows the test's semantics or the architecture.

## Binding requirements

### 1. Runner and architecture

- Only `MSTest.Sdk 4.5.1` with `Microsoft.Testing.Platform 2.5.1` as the runner (`global.json`: `"MSTest.Sdk": "4.5.1"`, `"runner": "Microsoft.Testing.Platform"`).
- **VSTest is forbidden.** No VSTest execution, VSTestBridge, VSTest collectors, VSTest fallbacks or extra VSTest infrastructure. `MSTest.TestAdapter` is part of the native stack and is not VSTestBridge.
- No own runner, bootstrap layer or duplicate SDK registration.
- NativeAOT is mandatory for every executable project, test projects included, as far as the concrete case is supported. Libraries are trimming-safe and NativeAOT-compatible as far as possible.
- Source-generated discovery and direct invocation where supported. The SDK default `MSTestSourceGenMode=ReflectionFree` (MSTest.TestAdapter.targets, line 57) is never replaced by a reflection-based mode. The SDK turns source generation on by itself when `PublishAot=true` and `UseVSTest` is not true (MSTest.Sdk Sdk.targets, lines 31–54).
- A test never disappears from discovery because of a source-generation limit without being noticed: the executed, passed, failed and skipped counts are part of every report.
- A technically unavoidable managed exception stays limited to the affected test area and still uses the native MTP runner, never VSTest.

Current runner scope: Unit opts into `EnableMSTestSourceGeneration=true` and the default `ReflectionFree` mode. Integration, EndToEnd and `TelemetryInvocation.Probe` retain managed MTP execution because the pinned 4.5.1 generator's exact `[TestClass]` lookup omits classes annotated with the derived `[TracePerTest]` attribute. Keep those classes and their policy invocation hook together; enabling generation for those assemblies would lose tests. The Unit native path uses `pwsh ./eng/test.ps1 -Suite Unit -NativeAot`, which sets `PublishAot` only inside the Unit project through `QylNativeTestRunner`; a global `PublishAot=true` incorrectly reaches the referenced `netstandard2.0` SemConv target.

### 2. Assertions and equality semantics

Use the assertion whose semantics equals the test's contract exactly.

```csharp
// value equality of the type (its Equals), no recursive structural comparison
Assert.AreEqual(expected, actual);

// element count only
Assert.HasCount(n, collection);

// element equality in order, EqualityComparer<T>.Default per element
Assert.AreSequenceEqual(expected, actual);

// element equality ignoring order, duplicate counts still matter
Assert.AreSequenceEqual(expected, actual, SequenceOrder.InAnyOrder);

// collection state
Assert.IsEmpty(collection);
Assert.IsNotEmpty(collection);
var only = Assert.ContainsSingle(collection);

// explicit, statically typed structural comparison
Assert.AreEqual(expected.Id, actual.Id);
Assert.AreSequenceEqual(expected.Items, actual.Items);

// the thrown exception is the test's input
var error = Assert.ThrowsExactly<InvalidOperationException>(() => subject.Run());
```

- `AreEqual` uses the type's equality and does no recursive structural comparison.
- `AreSequenceEqual` compares elements with `EqualityComparer<T>.Default`; `SequenceOrder.InAnyOrder` ignores order but counts duplicates. Nested object structures are not compared recursively.
- `AreEquivalent` and `AreNotEquivalent` compare object graphs recursively and order-sensitively; by default extra members or dictionary keys on the actual side are allowed, `strict: true` forbids them; a matching `IEquatable<T>` ends the recursion.
- `HasCount` checks only the element count, `IsEmpty` and `IsNotEmpty` only emptiness, `ContainsSingle` exactly one element.
- `ImmutableArray<T>.Equals` and `==` compare the identity of the underlying array, not the elements: use `AreSequenceEqual` for element equality.
- Do not replace `Assert.IsTrue(a == b)` blindly with `AreEqual`: an overloaded `==` can carry other equality rules. Overloaded operators are not smells by themselves, but their semantics must be consistent and intended.
- Never replace an assertion by a weaker check. Count, identity, order, element values and full object structure are different contracts.

### 3. NativeAOT limits of the assertions

- `Assert.AreEquivalent` and `Assert.AreNotEquivalent` are annotated `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` (present in the net8.0 and net9.0 assemblies of MSTest.TestFramework 4.5.1): their reflection over runtime types and `MakeGenericType` raise `IL2026` and `IL3050`.
- `Assert.That(Expression<Func<bool>>)` needs dynamic code as well. The `Assert.That` singleton property for extension assertions is a different thing; its compatibility depends on each extension's implementation.
- In NativeAOT test code use statically typed assertions that keep the full intended semantics.
- `IsAotCompatible=true` turns the compatibility analysis on; it does not guarantee that every public API is NativeAOT-safe.
- Never blanket-suppress `IL2026`, `IL3050` and related diagnostics. Remove the cause through the right API or architecture.
- A reflection-based assertion lives only in an explicitly delimited managed test area that is necessary.

### 4. Test-runner boundaries

These capabilities are independent of each other:

- **Generated discovery:** tests are found through generated metadata.
- **Generated invocation:** test methods run through generated direct calls.
- **Managed execution:** the runner runs on the .NET runtime, possibly with a reflection fallback.
- **NativeAOT execution:** a test binary published with `PublishAot=true` for the target RID runs natively.

Source generation alone proves no NativeAOT execution; ReadyToRun is not NativeAOT either. Async tests, custom test attributes, dynamic test data, generic test types, fixture providers and optional hosting extensions can limit generated discovery or invocation; the source-generation diagnostics (`AOTSG*`) and the supported execution paths decide.

In the pinned generator, asynchronous `Task`/`ValueTask` methods have generated direct invokers, and accessible static `DynamicData` sources can have generated accessors. Its narrower descriptor discovery path accepts only supported synchronous methods and metadata; generated metadata may still serve the adapter's existing discovery path. `ReflectionFree` retains bounded startup reflection for member keys and fallback for incomplete metadata, so the property alone is not proof that every runtime reflection path disappeared.

The plain-test policy guard has an explicit runtime boundary: publication outside an active `[TracePerTest]` invocation fails before reflection when dynamic code is unavailable. Its reflection helper carries `RequiresUnreferencedCode` and requires an untrimmed managed process. Dynamic-code support alone does not establish trimming safety. Failure to resolve an active managed test class is an error, never permission to bypass policy verification.

A managed MTP test process may test a NativeAOT production binary when that separation is needed. That makes the test process no more NativeAOT-compatible and justifies no change to the production artifact. In this repository, `Qyl.Telemetry.Tests.Capture` exists for NativeAOT console fixtures, and the delivery tests publish NativeAOT probes and a native Collector and test them from a managed runner.

## Preferences, not extra requirements

- Prefer the specific MSTest assertion over a general boolean comparison when the semantics is identical.
- Pass existing collections, arrays, `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>` and `ReadOnlyMemory<T>` straight to the matching overloads.
- Avoid needless `ToArray()` / `ToList()`; MSTest already uses direct `Count` and indexer access on suitable collection types.
- Prefer `IsEmpty(collection)` over `HasCount(0, collection)`.
- Express structural comparisons as explicit, statically typed member comparisons that leave no relevant property out.
- Prefer expected/actual diagnostics; add a message only where it adds context.
- Follow `MSTEST0037` (use the proper `Assert` method) and `MSTEST0065` (`Assert.AreEqual` on collection types) where their fixes keep the semantics; `MSTEST0046` likewise. Take an automatic fix only when the semantics is unchanged.
- No extra abstraction and no own assertion helper when the API already states the contract; a reusable policy or assertion type belongs in the support library (`Qyl.Telemetry.Tests`), not in a test file.

## Not required

- Not every test library has to produce a NativeAOT binary of its own; it has to stay compatible with the NativeAOT consumers.
- No extra configuration for runner and source-generation features the SDK already provides correctly.
- No generic deep-equality framework for convenience.
- No full object comparison when the contract asks for single properties.
- No extra materialization or own counting when MSTest supports the collection directly.
- No extra MTP extension when existing MSTest features suffice.

**Principle:** NativeAOT first and only the native MTP runner. Choose assertions by their actual semantics. Preferences are optimization room, never a pretext for weaker tests or extra architecture. VSTest is never a fallback.

The suite skills are listed in `AGENTS.md`.

## MSTest 4.5.1 and MTP: verified usage

Verified on 2026-10-23 against the installed `MSTest.Sdk`, `MSTest.TestAdapter` and `MSTest.TestFramework` 4.5.1 packages, the repository's project files, and the [4.5.1 source changelog](https://github.com/microsoft/testfx/blob/v4.5.1/docs/Changelog.md). API availability and configuration inspection are not evidence that a test has run under NativeAOT. Record actual execution separately.

The imported MSTest API table covers the pinned source files listed in `.github/versions.yaml`, not every target-framework-specific public API. This scope also applies to the imported skills' "every public symbol" wording. Verify availability against the package asset selected for this repository's `net10.0` target and its pinned upstream source; an API missing from the table may still be available.

- `MSTest.Sdk` supplies executable output and the native MTP runner for test applications. `global.json` selects native SDK test mode. Use `dotnet test --project <project>` or `dotnet test --solution <solution>`; do not add a VSTest bridge, another runner, or duplicate SDK packages.
- For `PublishAot=true`, the SDK selects its NativeAOT runner targets and includes `MSTest.SourceGeneration` automatically. For managed execution, source generation requires `EnableMSTestSourceGeneration=true` if desired. The adapter defaults `MSTestSourceGenMode` to `ReflectionFree`; do not replace it with reflection-based discovery to hide unsupported tests.
- `[TestClass]` belongs directly on concrete test classes. `MSTEST0069` detects inherited discovery assumptions; `MSTEST0085` flags abstract test classes. Keep every expected data row visible in the discovery and execution counts.
- Prefer `Assert.*` collection methods to legacy `CollectionAssert` when their semantics match; `MSTEST0068` assists migration. `Assert.Scope()` can collect multiple failures about one behavior. It does not justify combining independent behaviors or using reflection-based equivalence in NativeAOT code.
- `[DependsOn]` and `mstest.execution.dependencies` express prerequisites inside a test assembly, not MSBuild project references or cross-process product startup. Keep ordinary unit tests independent. A failed prerequisite may skip dependents; never use that to conceal required coverage.
- `DataRowAttribute.ArgumentsDisplayName` and `TestDataRow<T>.ArgumentsDisplayName` label arguments while retaining the method name. Full `DisplayName` overrides take precedence according to each type's contract; dynamic row labels require a compatible adapter. Generated display names escape control characters. Do not infer a project-reference failure from display-name behavior, and do not use formatted display text as a substitute for stable method identity or test categories. See the [4.5.1 DataRow implementation](https://github.com/microsoft/testfx/blob/v4.5.1/src/TestFramework/TestFramework/Attributes/DataSource/DataRowAttribute.cs).
- Built-in combinatorial attributes live in `Microsoft.VisualStudio.TestTools.UnitTesting.Combinatorial`. Use a bounded, meaningful matrix and verify discovery with the chosen runner; API presence alone does not establish support by source-generated invocation.
- `[ResourceLock]` names are shared constants. `MSTEST0073` through `MSTEST0077` check resource naming and unsafe process/global/filesystem changes. Locks coordinate a test assembly, not independent test processes; cross-process resources still need unique paths, ports and owned lifetimes.
- Both `[assembly: TestFilterProvider(typeof(MyFilter))]` and `[assembly: TestFilterProvider<MyFilter>]` exist in 4.5.1. The generic form adds type constraints; it did not remove the non-generic form. These APIs remain experimental in the installed XML documentation. Do not use a filter provider to silently drop required tests.
- `[AssemblyFixtureProvider]` is unsupported under NativeAOT (`MSTEST0072`). Shared support classes do not automatically provide executable fixtures to every consuming assembly; use the lifecycle supported by the concrete runner and prove it executes.
- `MSTest.Extensions.Hosting` test-class injection is opt-in, not a default modernization. `MSTEST0088` rejects it with NativeAOT, browser WebAssembly/AOT compilation or MSTest source generation. Do not introduce it into this repository's AOT path.
- The output key is `mstest.output.captureTrace`, with `"None"`, `"Result"` or `"Live"`; it is not `captureOutput` or `captureTraceOutput` in `testconfig.json`. Boolean values remain compatible. Retain `"Result"` for normal runs; use `"Live"` for focused diagnostics when useful.
- Assertion diagnostics use `mstest.execution.captureAssertionFailureDiagnostics`. They produce at most three bounded JSON captures per failed attempt and are not supported under NativeAOT or other environments without dynamic code. Do not enable them as an AOT diagnostic solution.
- `TestContext.AddResultFile(path)` attaches artifacts to native MTP results. Attach the existing process log or manifest that explains a failure. Data-driven display names are escaped by 4.5; do not implement a second display-name sanitizer.
- For multiple test projects, let TRX choose unique names or use supported `{asm}`, `{tfm}` and `{arch}` placeholders. Do not force a shared fixed report filename. This repository is pinned to .NET 10; .NET 11 switches are not part of its command contract.

Configuration reference: [Configure MSTest](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-configure). The installed SDK targets, adapter targets and framework XML under the NuGet package cache are the local evidence for the resolved version. Recheck them when the pin changes instead of importing every feature from a newer tutorial.

Execution evidence on 2026-10-23: the sibling `Qyl.Telemetry.Tests-codex` checkout built its exact solution in Release with zero warnings/errors and ran a focused native-MTP selection across all three suites: 21 unit cases, 5 receiver integration cases, 1 application header-delivery case and 2 HTTP/MCP failure-context cases; all 29 passed, none skipped. Its semantic probe was separately published for `osx-arm64` with NativeAOT and executed for a string, integer, Boolean and null input; all four returned `dynamicCodeSupported=false` with the expected acceptance/rejection. This is evidence for that checkout and those cases, not a full-suite result or a native test-runner result. The primary checkout's end-to-end Release build also passed after restoring the capture exporter's required OpenTelemetry dependency, and its focused application header-delivery test passed (1 executed, 1 passed, none skipped).

## Product projects, probes and the three telemetry suites

`Qyl.Telemetry.Tests` stays a support library (`IsTestApplication=false`). A production program, TypeScript service or sample application is exercised by a suite; it is not turned into a test assembly by including its source files.

| Component | Integration point | Evidence required |
| --- | --- | --- |
| Support and capture libraries | Unit tests reference support; support references capture | Deterministic evidence, assertion and policy behavior |
| Real exporters and auto-instrumentation | Integration tests exercise the resolved hosting/instrumentation packages through the receiver | Exported spans and attributes, not merely restored assemblies |
| `Applications/TelemetryProbe` | End-to-end build-only `ProjectReference` plus `TestFixtureProject` payload | Application exports reach the Collector |
| `Applications/SemanticContractProbe` | End-to-end build-only `ProjectReference` plus `SemanticContractProject` delivery path | Published NativeAOT consumer reads the pinned semantic contracts |
| Native Collector and published TypeScript MCP | `Delivery/inputs.json`, `DeliveryArtifacts`, `McpProcess` and the end-to-end suite | Pinned artifact identity, real process startup and REST/MCP results |

Keep probe references `ReferenceOutputAssembly=false` and exclude `Applications/**/*.cs` from the test assembly. The probes retain their own entry points and publish settings. A TypeScript service is a child process with a protocol boundary, not a C# `ProjectReference`. Keep source-checkout tests distinct from pinned-package delivery tests so a local source change cannot silently replace the published artifact under test.

`Qyl.Telemetry.IntegrationTests/Contracts` exercises the parent qyl checkout's C# contracts, Collector consumers and TypeScript contract implementation. These are integration cases and require Bun plus the parent source checkout; they are not a fourth executable test suite or shared support code.

## Focused local validation

Honor an explicit request to limit local CPU use. Choose the smallest test class or method covering the change, run one test module at a time, and use a command-scoped `DOTNET_PROCESSOR_COUNT=2` for managed builds and test processes when appropriate. Inspect the pinned runner's help before choosing a filter switch. Do not run delivery publication, all suites, coverage or InspectCode for a documentation-only change.

Project evaluation and solution listing prove wiring only; a successful build proves compilation only. A filtered managed run does not prove NativeAOT execution. Report exactly which checks ran, their test counts, and any full CI or native-publication checks left unrun. The full pre-push gate below remains required for a push; a bounded local check is not a claim that the full gate passed.

# Test-code rules

Every test in this repository, and every test an agent writes with this library elsewhere, follows these rules. A diff that breaks one is rejected, whatever else it does.

## 1. The name is the specification

- Default pattern `<Method>_<Scenario>_<Expected>` unless the nearest component policy overrides it: three PascalCase segments joined by underscores. `DbCommandInterceptor_AsyncCommandThrowsBeforeTask_EndsItsSpan`, `Receiver_ProfilingFieldOnSignal_IsNotLeakedBetweenExports`, `Decoder_UnknownField127_IsAbsentFromEveryRecord`.
- Subject is the type or behavior under test, State the input or precondition, Expected the observable outcome. A name that needs a comment to be understood is wrong; fix the name.
- No `//` comment and no `///` doc comment inside or on a test method. What a comment would say goes into the name, a named local or a named constant (`unknownField127Varint1`, `acceptedSizeLeavingRoomForKestrelChunkFraming`). Doc comments stay on support types, fixtures and helpers.
- Class names stay stable: CI, the repinning skill and the eval filter by `ClassName=`, never by method name.

## 2. One behavior per test


- A test asserts one behavior. A method that asserts several independent behaviors is split, one test per behavior, even when the count grows a lot.
- Use the MSTest assertion that states the intent: `Assert.ThrowsExactly` / `ThrowsExactlyAsync` (it returns the exception), `Assert.IsInstanceOfType<T>`, `Assert.ContainsSingle`, `CollectionAssert`, `StringAssert`, `Assert.AreEqual` with a message only when the values alone do not explain the failure. Reference: https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-assertions
- No assertion hidden in a private helper. A helper builds data or runs the operation; the test asserts. The exception is a reusable policy or assertion type in the support library.
- A unit test fakes, stubs or mocks the dependencies in front of the assertion: no file system, clock, process or network. The fake is a real type in the support library (`IEvidenceStore` with an in-memory store, `FakeTimeProvider`), never a shim or a flag in production code.

## 3. Data rows name their values

- A string that selects a branch (a signal `"traces"`, a metric kind `"histogram"`, a log value kind `"bytes"`) is a `private const string` of the test class or a project-wide constants class (`Signals.Traces`, `MetricShapes.Histogram`), used in the `[DataRow]` and in every `case`, comparison and argument that means the same value. Attribute arguments stay compile-time constants.
- Pure input data stays a literal: URLs, content types, encodings, HTTP status codes, registry type names, numbers, booleans, enum members. Do not invent a constant for a one-off input.
- `[DataRow]` for a handful of fixed rows, `[DynamicData]` when rows come from code; the test body names the row's parameter by what it is, never `value` or `input`. Reference: https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-data-driven

## 4. No try, catch or finally inside a test method

- Restoring state (environment variables, settings) is a disposable scope (`EnvironmentScope`) under `using`, or a `[TestInitialize]`/`[TestCleanup]` pair, with `[ResourceLock(WellKnownResources.EnvironmentVariables)]` on the test.
- Disposal is `using` or `await using`. Ending a root span is `using var ending = TelemetryPipeline.Ending(root)`.
- Waiting for something to appear is one shared helper with a deadline that returns the value or fails with the last observed state (`CollectorClient.WaitForTraceAsync`, `WaitForSpanEventsAsync`); never a loop that swallows an exception and breaks.
- A thrown-and-caught exception used as test input is `Assert.ThrowsExactly`, which returns it.
- A child process is `ChildProcess`, which kills, joins and drains on disposal.
- Lifecycle, execution control and organization: https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-lifecycle , ...-controlling-execution , ...-organizing , ...-testcontext

## 5. Time flows through TimeProvider

- A support type that reads the clock or waits takes a `TimeProvider` (constructor or method parameter, default `TimeProvider.System`) and uses `GetUtcNow()` (a `DateTimeOffset`), `GetTimestamp()`/`GetElapsedTime()`, `new PeriodicTimer(interval, timeProvider)` and `Task.Delay(delay, timeProvider, token)`. `DateTime.UtcNow`, `DateTimeOffset.UtcNow` and `Stopwatch` appear in no test and in no support type.
- Unit tests drive time with `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing). End-to-end tests pass `TimeProvider.System` in at one seam and read it from there.
- Tick math uses `TimeSpan.TicksPerMillisecond`, `TimeSpan.TicksPerSecond`, `TimeSpan.NanosecondsPerTick` or `TimeSpan.FromTicks`, or the suites' named constants (`Millisecond`, `Second`, `Minute`); never a bare `10_000_000`.
- `TimeZoneInfo`, never the obsolete `TimeZone`. `DateOnly`/`TimeOnly` where a value is a date or a time of day only (`TimeOnly.FromDateTime`), never for an instant the test compares.
- References: https://learn.microsoft.com/en-us/dotnet/core/extensions/timeprovider-testing , https://learn.microsoft.com/en-us/dotnet/standard/datetime/timeprovider-overview , https://learn.microsoft.com/en-us/dotnet/standard/datetime/how-to-use-dateonly-timeonly

## 6. The gate decides

- The stack is MSTest.Sdk 4.5.1 on Microsoft.Testing.Platform 2.5.1, with generated metadata and invocation in Unit and the explicit managed exceptions above (https://devblogs.microsoft.com/dotnet/mstest-source-generation/): no reflection-only test tricks, `[DoNotParallelize]` on classes that change process-wide state. The three suite configurations provide cooperative per-test deadlines through `mstest.timeout`; use `[Timeout]` for a test that needs a different budget. A wait must observe the runner's cancellation token as well as any operation-specific deadline.
- Before a push: build the touched projects in Release, run the unit tests, run `./build.sh --target InspectCode` to zero findings. `./build.sh` (Fallout `Check`) is what CI runs. A red is fixed at its cause; no suppression, no skipped, deleted or loosened test, no special-cased input.
- Configurable support types follow the options pattern for library authors (https://learn.microsoft.com/en-us/dotnet/core/extensions/options-library-authors): no static mutable configuration.
