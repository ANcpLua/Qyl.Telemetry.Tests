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

# Test-code rules

Every test in this repository, and every test an agent writes with this library elsewhere, follows these rules. A diff that breaks one is rejected, whatever else it does.

## 1. The name is the specification

- Pattern `Subject_State_Expected`: three PascalCase segments joined by underscores. `DbCommandInterceptor_AsyncCommandThrowsBeforeTask_EndsItsSpan`, `Receiver_ProfilingFieldOnSignal_IsNotLeakedBetweenExports`, `Decoder_UnknownField127_IsAbsentFromEveryRecord`.
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

- The stack is MSTest.Sdk 4.5.1 on Microsoft.Testing.Platform 2.5.1 with source-generated discovery (https://devblogs.microsoft.com/dotnet/mstest-source-generation/): no reflection-only test tricks, `[DoNotParallelize]` on classes that change process-wide state, `[Timeout]` on tests that wait.
- Before a push: build the touched projects in Release, run the unit tests, run `./build.sh --target InspectCode` to zero findings. `./build.sh` (Fallout `Check`) is what CI runs. A red is fixed at its cause; no suppression, no skipped, deleted or loosened test, no special-cased input.
- Configurable support types follow the options pattern for library authors (https://learn.microsoft.com/en-us/dotnet/core/extensions/options-library-authors): no static mutable configuration.
