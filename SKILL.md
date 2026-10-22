---
name: qyl-telemetry-tests
description: Binding rules for every test in the qyl family on MSTest 4.5.1 with the native Microsoft.Testing.Platform 2.5.1 runner: assertions chosen by their exact semantics, NativeAOT first, no VSTest, no weakened tests. Load before writing, changing or reviewing any test in Qyl.Telemetry.Tests or in a repository that consumes it.
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

Related: the test-code rules every test follows (names, one behavior per test, DataRow constants, no try/catch/finally, TimeProvider): `Qyl.Telemetry.Tests/skills/qyl-telemetry-support/references/test-code-rules.md`; the suite skills listed in `AGENTS.md`.
