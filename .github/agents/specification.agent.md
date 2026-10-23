---
name: mstest-publicapi-spec-agent
description: Tracks and compares the public API of microsoft/testfx across MSTest releases and keeps the shared skill reference .agents/references/mstest-publicapi.md current with .github/generate_table.py. Use when adding a release, checking when an API appeared, or reviewing what changed between versions.
---

# MSTest PublicAPI specification

## Files

- `.github/versions.yaml`: versions and source files (below).
- `.github/generate_table.py`: run from the repo root with `python .github/generate_table.py`. It resolves each tag to its commit itself and treats the highest version as current.
- `.github/api_table.md.j2`: template for the output.
- `.agents/references/mstest-publicapi.md`: the generated reference that both skills in `.agents/skills/` link to.

The workflow `.github/workflows/sync-api-status.yml` regenerates the reference when one of the first three files changes on `main`.

## Versions (`versions.yaml`)

```yaml
repo: microsoft/testfx
target_files:
  - src/TestFramework/TestFramework/PublicAPI/PublicAPI.Shipped.txt
  - src/TestFramework/TestFramework/PublicAPI/PublicAPI.Unshipped.txt
  - src/TestFramework/TestFramework/PublicAPI/net/PublicAPI.Shipped.txt
  - src/TestFramework/TestFramework/PublicAPI/net/PublicAPI.Unshipped.txt
  - src/Adapter/MSTest.Extensions.Hosting/PublicAPI/PublicAPI.Shipped.txt
  - src/Adapter/MSTest.Extensions.Hosting/PublicAPI/PublicAPI.Unshipped.txt
versions:
  - version: "4.3.0"
    tag: v4.3.0   # 79f26ff
  - version: "4.4.1"
    tag: v4.4.1   # 9a14d19
  - version: "4.5.1"
    tag: v4.5.1   # 0bf77e3
```

The `Unshipped` files belong in the list. At a release tag they still hold APIs that shipped in that release. In `v4.3.0`, for example, 174 of the TestFramework entries are only there. Files that a version does not have yet, such as the Hosting package before 4.5, count as empty. Versions must be quoted, otherwise YAML reads `4.10` as `4.1`.

To add a release, append its version and tag. The workflow regenerates the reference; never edit it by hand. Each symbol's "Introduced" value is the first listed version that has it, so a symbol shown as 4.3.0 may be older.

## What changed between 4.3.0, 4.4.1 and 4.5.1

| Area | 4.3.0 (`79f26ff`) | 4.4.1 (`9a14d19`) | 4.5.1 (`0bf77e3`) |
|---|---|---|---|
| Combinatorial tests | Not built in | New: namespace `...UnitTesting.Combinatorial` with `CombinatorialData`, `CombinatorialValues`, `CombinatorialRange`, `CombinatorialRandomData`, `ICombinatorialValuesProvider` | Generated display names escape control characters and unpaired surrogates in `string`/`char` arguments |
| Test order | Only `[DoNotParallelize]` or manual ordering | New: `[DependsOn]` (method, class, class + method, `ProceedOnFailure`), works on both hosts; on MTP also `mstest.execution.dependencies` in `testconfig.json`; checked by `MSTEST0078` | Unchanged |
| Parallel safety | — | New: `[ResourceLock]`, `ResourceAccessMode`, `WellKnownResources`; analyzers `MSTEST0073`–`MSTEST0077` | Unchanged |
| Source generation | — | New: `DynamicDataSourceResolver`, which `MSTest.SourceGeneration` uses to register data sources without reflection | Unchanged |
| Retry | `[Retry]` | New, experimental: `RetryResult.AllResults` for authors of custom retry attributes | Unchanged |
| Test filters | Experimental `TestFilterProvider(Type)` | Added experimental `TestFilterProvider<TFilter>` (modern .NET); analyzer `MSTEST0081` | Unchanged |
| Output capture | `captureTrace` as a boolean | `captureTrace` also takes `None`, `Result`, `Live` | New: `mstest.execution.captureAssertionFailureDiagnostics` |
| Data rows | — | — | New: `ArgumentsDisplayName` on `DataRowAttribute` and `TestDataRow<T>` |
| Dependency injection | Constructor with `TestContext` only | Unchanged | New package `MSTest.Extensions.Hosting` (prerelease): `AddMSTestTestClassInjection()`, experimental; `MSTEST0088` blocks it under AOT, source generation and browser-wasm |
| Analyzers | Up to `MSTEST0071` | `MSTEST0072`–`MSTEST0083` | `MSTEST0084`–`MSTEST0089` |

Already in 4.3.0, so not new later: `Assert.That`, `Assert.Scope()`, `Assert.AddValueFormatter`, `AssertFailedException.ExpectedText`/`ActualText`, `[OSCondition]`, `[ArchitectureCondition]`, `[CICondition]`, `[ExecutableCondition]`.

The published packages match the tags: the public types and members of `MSTest.TestFramework` 4.3.0, 4.4.1 and 4.5.1 give the same deltas, and MSTest 4.5.1 depends on Microsoft.Testing.Platform 2.5.1. No 4.5.0 package was published; `MSTest.Extensions.Hosting` exists only as `4.5.1-alpha.26507.8`.

4.5.1 also adds the separate `MSTest.Windows.UIAutomation` package (`ApplicationTest`, `WindowTest`), which the file list above does not cover.

## Reference

The full list is `.agents/references/mstest-publicapi.md`. For the file list above it has 1225 symbols: 963 introduced in 4.3.0, 256 in 4.4.1 and 6 in 4.5.1. Nothing was removed and no API changed its experimental status between these versions.

The 4.5.1 additions, as generated:

| Symbol | Status | Introduced | Last seen |
|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection.MSTestHostingServiceCollectionExtensions` | experimental | 4.5.1 | 4.5.1 |
| `Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute.ArgumentsDisplayName.get -> string?` | stable | 4.5.1 | 4.5.1 |
| `Microsoft.VisualStudio.TestTools.UnitTesting.DataRowAttribute.ArgumentsDisplayName.set -> void` | stable | 4.5.1 | 4.5.1 |
| `Microsoft.VisualStudio.TestTools.UnitTesting.TestDataRow<T>.ArgumentsDisplayName.get -> string?` | stable | 4.5.1 | 4.5.1 |
| `Microsoft.VisualStudio.TestTools.UnitTesting.TestDataRow<T>.ArgumentsDisplayName.set -> void` | stable | 4.5.1 | 4.5.1 |
| `static Microsoft.Extensions.DependencyInjection.MSTestHostingServiceCollectionExtensions.AddMSTestTestClassInjection(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!` | experimental | 4.5.1 | 4.5.1 |
