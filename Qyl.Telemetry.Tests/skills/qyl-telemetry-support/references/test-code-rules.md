# Test-code rules

Every test in this repository, and every test an agent writes with this library elsewhere, follows these rules. A diff that breaks one is rejected, whatever else it does.

## 1. The name is the specification

- Pattern `Subject_State_Expected`: three PascalCase segments joined by underscores. `DbCommandInterceptor_AsyncCommandThrowsBeforeTask_EndsItsSpan`, `Receiver_ProfilingFieldOnSignal_IsNotLeakedBetweenExports`, `Decoder_UnknownField127_IsAbsentFromEveryRecord`.
- Subject is the type or behavior under test, State the input or precondition, Expected the observable outcome. A name that needs a comment to be understood is wrong; fix the name.
- No `//` comment and no `///` doc comment inside or on a test method. What a comment would say goes into the name, a named local or a named constant (`unknownField127Varint1`, `acceptedSizeLeavingRoomForKestrelChunkFraming`). Doc comments stay on support types, fixtures and helpers.
- Class names stay stable: CI, the repinning skill and the eval filter by `ClassName=`, never by method name.

## 2. One behavior per test

The assertion semantics and the NativeAOT rules are in the repository's root skill, /Users/alexandernachtmann/RiderProjects/Qyl.Telemetry.Tests/SKILL.md; it binds every test.

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
