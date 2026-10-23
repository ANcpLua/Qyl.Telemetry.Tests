---
name: mstest-antipatterns
description: Find and fix MSTest and Microsoft.Testing.Platform (MTP) anti-patterns, from VSTest leftovers and MSBuild ordering traps to parallel-safety bugs and pre-4.5 idioms. Use when reviewing, auditing or refactoring a .NET test suite.
---

# MSTest anti-patterns

Checked against `microsoft/testfx` at `v4.5.1` and the published NuGet packages. The "Since" column gives the MSTest version that ships the analyzer. "4.5" means 4.5.1; no 4.5.0 package was published.

To check whether an API exists, is experimental, or which version introduced it, search [`../../references/mstest-publicapi.md`](../../references/mstest-publicapi.md) for the type or member name.

## Platform and MSBuild

**Conditioning on `$(IsTestProject)` in `Directory.Build.props`.** `Directory.Build.props` is imported before the project body and before package props, so `IsTestProject` is usually still empty there. Conditions such as `<OutputType>Exe</OutputType>` or `<EnableMSTestRunner>true</EnableMSTestRunner>` on it then never apply. Condition on something known early, for example `$(MSBuildProjectName.EndsWith('.Tests'))`, or move the logic to `Directory.Build.targets`.

**Selecting MTP through `TestingPlatformDotnetTestSupport`.** On the .NET 10 SDK and later, `dotnet test` picks the runner from `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

Do not set `TestingPlatformDotnetTestSupport` in new projects.

**VSTest command-line syntax.** MTP rejects `--logger` and `--collect`. Use `--report-trx` and `--coverage` instead. MSTest still supports `--filter` and `.runsettings` (`--settings`) on MTP, so those need no rewrite. When several projects write TRX files into one folder, give them distinct names with placeholders: `--report-trx-filename "{asm}_{tfm}_{arch}.trx"`. The available placeholders are `{pname}`, `{pid}`, `{asm}`, `{tfm}`, `{arch}` and `{time}`.

**Referencing `Microsoft.NET.Test.Sdk` in MTP projects.** MTP does not need it, and it also pulls in `Microsoft.CodeCoverage`. `MSTest.Sdk` adds it only when `UseVSTest` is set.

## Analyzer-backed anti-patterns

| Anti-pattern | Rule | Since | Fix |
|---|---|---|---|
| `CollectionAssert.*` | `MSTEST0068` | 4.3 | `Assert.AreSequenceEqual`, `Assert.AreAllNotNull`, `Assert.AreAllDistinct`, `Assert.AreAllOfType`, `Assert.Contains`. `CollectionAssert.AreEquivalent` becomes `Assert.AreSequenceEqual(..., SequenceOrder.InAnyOrder)`, not `Assert.AreEquivalent`, which compares object graphs. |
| `StringAssert.*` | `MSTEST0046` | 3.10 | The matching `Assert.*` method, for example `Assert.MatchesRegex`. |
| `[TestClass]` on an `abstract` class | `MSTEST0085` | 4.5 | Remove it; it has no effect there. |
| Concrete class that inherits `[TestClass]` without declaring it, with source generation | `MSTEST0069` (in `MSTest.SourceGeneration`) | 4.4 | Put `[TestClass]` on every concrete test class. |
| `AddMSTestTestClassInjection()` with `PublishAot`, `RunAOTCompilation`, `EnableMSTestSourceGeneration` or browser-wasm | `MSTEST0088` (error) | 4.5 | Do not use host injection in those builds; use a parameterless or `TestContext` constructor. |
| `[assembly: TestFilterProvider(typeof(X))]` with an invalid type | `MSTEST0081` | 4.4 | The type must implement `ITestFilter`, be concrete and have a public parameterless constructor; only one provider per assembly. On modern .NET, prefer `TestFilterProvider<T>` so the compiler checks the type. Both forms are experimental. |
| String literal in `[ResourceLock("...")]` | `MSTEST0073` | 4.4 | Use a `WellKnownResources` constant or a shared `const`, so every test locks the same key. |
| `Environment.SetEnvironmentVariable` or `Console.SetOut/SetError/SetIn` in parallel tests | `MSTEST0074` | 4.4 | `[ResourceLock(WellKnownResources.EnvironmentVariables)]` or `WellKnownResources.Console`, or `[DoNotParallelize]`. |
| Changing the current directory in parallel tests | `MSTEST0075` | 4.4 | `[ResourceLock(WellKnownResources.CurrentDirectory)]` or `[DoNotParallelize]`. |
| Setting `CultureInfo.DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture` | `MSTEST0076` | 4.4 | `[DoNotParallelize]`. There is no resource constant for culture. |
| Constant path passed to a mutating `File.*`/`Directory.*` call | `MSTEST0077` | 4.4 | A per-test location such as `TestContext.TestTempDirectory`. |
| Early `return` or `Assert.Inconclusive` behind an OS, architecture, `CI` variable or `File.Exists` check | `MSTEST0061` (4.1), `0079`, `0080`, `0083` (4.4) | 4.1–4.4 | `[OSCondition]`, `[ArchitectureCondition]`, `[CICondition]`, `[ExecutableCondition]`. |
| `[SupportedOSPlatform]` on a test without a matching `[OSCondition]` | `MSTEST0084` | 4.5 | Add `[OSCondition]`. The platform attributes only feed compatibility analysis; they do not skip the test. |
| Method attribute that repeats the class (`[Retry]` with identical settings, `[DoNotParallelize]`, `[ResourceLock]`, `[TestCategory]`, and condition attributes when the class is sealed) | `MSTEST0086` | 4.5 | Remove the method-level copy. |
| `[DoNotParallelize]` used to force an order | — | — | `[DependsOn(nameof(Prerequisite))]`. It works with parallelization on, on both MTP and VSTest; `MSTEST0078` validates the target. |
| `[AssemblyFixtureProvider]` with `PublishAot` or `RunAOTCompilation` | `MSTEST0072` | 4.4 | Without dynamic code the providers are ignored at runtime. Declare `[AssemblyInitialize]`/`[AssemblyCleanup]` in the test assembly itself. |
| `[Timeout]` without `CooperativeCancellation = true` | `MSTEST0045` | 3.10 | Set it, and observe `TestContext.CancellationToken`. |
| Not passing `TestContext.CancellationToken` where an overload accepts one | `MSTEST0049` | 3.10 | Pass the token. |
| Duplicate `DisplayName` on `[DataRow]`s, or conflicting constant `DisplayName` and `ArgumentsDisplayName` values on one row | `MSTEST0087`, `MSTEST0089` | 4.5 | Give each row a unique name; for one row, use `DisplayName` for the whole name or `ArgumentsDisplayName` for the arguments part, not both. |

## Test quality

- **Tests without a real assertion.** A test that only checks `Assert.IsNotNull(result)` or relies on "did not throw" proves almost nothing. Assert the resulting state.
- **Tautological assertions.** `Assert.AreEqual(service.Compute(x), service.Compute(x))` compares the code with itself. The same goes for inputs that a no-op implementation would pass unchanged.
- **Moving short setup into shared `[TestInitialize]` fields.** Two or three lines of explicit arrangement per test are easier to read and keep tests isolated. Use `readonly` fields or primary-constructor state rather than mutable shared fields.
- **Parsing `AssertFailedException.Message`.** Read `ExpectedText` and `ActualText` instead.
