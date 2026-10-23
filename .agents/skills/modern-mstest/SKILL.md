---
name: modern-mstest
description: Write tests with current MSTest 4.x APIs on Microsoft.Testing.Platform, covering unified Assert methods, soft assertions, combinatorial data, [DependsOn], resource locks, execution conditions and host-owned dependency injection. Use when writing new tests or modernizing existing ones.
---

# Writing modern MSTest tests

Checked against `microsoft/testfx` at `v4.5.1` and the published NuGet packages. Version notes give the release that introduced an API; "4.5" means 4.5.1, since no 4.5.0 package was published. APIs marked experimental need `#pragma warning disable MSTESTEXP`.

Before using an API not shown here, search [`../../references/mstest-publicapi.md`](../../references/mstest-publicapi.md) for its type or member name. It lists every public symbol of `MSTest.TestFramework` and `MSTest.Extensions.Hosting` with its status and the version that introduced it.

## Test classes

Mark concrete test classes `sealed`, put `[TestClass]` on each one, and take `TestContext` through the primary constructor. Pass `context.CancellationToken` to calls that accept a token (`MSTEST0049`).

```csharp
[TestClass]
public sealed class PaymentGatewayTests(TestContext context)
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public async Task AuthorizeAsync_ValidCard_ReturnsApprovedReceipt()
    {
        var sut = new PaymentGateway(_time);

        var receipt = await sut.AuthorizeAsync(amount: 49.99m, context.CancellationToken);

        using (Assert.Scope()) // experimental
        {
            Assert.IsTrue(receipt.IsApproved);
            Assert.AreEqual(49.99m, receipt.AuthorizedAmount);
            Assert.AreEqual(_time.GetUtcNow(), receipt.Timestamp);
        }
    }
}
```

`Assert.Scope()` collects every failure inside the block and reports them together at the end.

## Services from a host (`MSTest.Extensions.Hosting`, prerelease, experimental)

The package is only published as `4.5.1-alpha.26507.8` and pulls in `Microsoft.Testing.Extensions.Hosting` 1.0.0-alpha. It builds test class instances from an `IServiceProvider`, with one service scope per test invocation (every data row and every retry gets its own). You supply a static host factory and point MSBuild at it; the generated entry point does the rest, so there is no `Main` to write.

```xml
<PropertyGroup>
  <TestingPlatformHostFactory>TestHost.CreateHost</TestingPlatformHostFactory>
</PropertyGroup>
```

```csharp
internal static class TestHost
{
    public static Task<IHost> CreateHost()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<InvoiceService>();
        builder.Services.AddMSTestTestClassInjection();
        return Task.FromResult(builder.Build());
    }
}

[TestClass]
public sealed class InvoiceServiceTests(InvoiceService service, TestContext context)
{
    [TestMethod]
    public async Task ExportAsync_WritesPdf()
    {
        string path = await service.ExportInvoiceAsync(invoiceId: 42, context.CancellationToken);

        context.AddResultFile(path);
        Assert.IsTrue(File.Exists(path));
    }
}
```

MSTest passes `TestContext` itself; it is not resolved from the container, and the parameter is optional. Injection is not supported with `PublishAot`, `RunAOTCompilation`, `EnableMSTestSourceGeneration` or browser-wasm. Using it there is the error `MSTEST0088`.

## Assertions

Use `Assert.*` instead of `CollectionAssert` (`MSTEST0068`) and `StringAssert` (`MSTEST0046`).

```csharp
// Order matters by default; on modern .NET there are Span overloads.
Assert.AreSequenceEqual(expected, actual);
Assert.AreSequenceEqual(expected, actual, SequenceOrder.InAnyOrder); // replaces CollectionAssert.AreEquivalent

// Deep comparison of object graphs. strict: true also fails on extra dictionary keys on actual,
// and, when actual has a different runtime type, on public members that expected lacks.
Assert.AreEquivalent(expectedOrder, actualOrder, strict: true);

// Predicate first, collection second; returns the matching item.
Customer vip = Assert.ContainsSingle(c => c.IsVip, customers);

Assert.ContainsAll(expectedSubset, actual);
Assert.AreAllNotNull(actual);
Assert.AreAllDistinct(actual);
Assert.AreAllOfType<ProcessedEvent>(events);

Assert.IsInRange(minValue: 1, maxValue: 100, score);
Assert.IsPositive(balanceDelta);
await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.CommitAsync(context.CancellationToken));
```

For a compound condition, `Assert.That` breaks the expression down on failure. It is a C# 14 extension member; on older language versions call `AssertExtensions.That(...)`.

```csharp
Assert.That(() => response.StatusCode == 200 && response.Items.Count > 0);
```

To control how a type appears in failure messages, register a formatter (experimental). It returns an `IDisposable` that removes it again:

```csharp
using IDisposable _ = Assert.AddValueFormatter<Money>(m => $"{m.Amount} {m.Currency}");
```

## Combinatorial data (4.4)

`[CombinatorialData]` goes next to `[TestMethod]` and runs every combination of the parameter values. No extra package is needed.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting.Combinatorial;

[TestMethod]
[CombinatorialData]
public void Compress(
    [CombinatorialValues("gzip", "br")] string encoding,
    [CombinatorialRange(from: 1, to: 9, step: 4)] int level,
    [CombinatorialRandomData(Count = 2, Minimum = 128, Maximum = 4096, Seed = 42)] int payloadSize,
    bool flushFinalBlock)
{
    // ...
}
```

A `bool` parameter without an attribute expands to `true` and `false`. An `int` parameter without an attribute expands only to `0` and `1`, so give it values explicitly.

## Ordering with `[DependsOn]` (4.4)

Keep parallelization on and declare the order the tests need. This works on MTP and VSTest, also across classes in the same assembly.

```csharp
[TestMethod]
public void CreateSchema() { /* ... */ }

[TestMethod]
[DependsOn(nameof(CreateSchema))]
public void SeedData() { /* ... */ }
```

If the prerequisite fails, the dependent test is skipped, and the skip propagates. Set `ProceedOnFailure = true` to run it anyway. Further overloads are `[DependsOn(typeof(OtherTests))]` (every test of that class) and `[DependsOn(typeof(OtherTests), nameof(OtherTests.Method))]`. On MTP the same edges can be declared in `testconfig.json` under `mstest.execution.dependencies`. `MSTEST0078` reports invalid targets and cycles.

## Process-wide state under parallelization (4.4)

Lock global state with the constants in `WellKnownResources` (`EnvironmentVariables`, `CurrentDirectory`, `Console`). The default mode is `ReadWrite`; tests that only read can use `Mode = ResourceAccessMode.Read`.

```csharp
[TestMethod]
[ResourceLock(WellKnownResources.EnvironmentVariables)]
public void ReadsTenantModeFromEnvironment()
{
    Environment.SetEnvironmentVariable("TENANT_MODE", "Isolated");
    Assert.AreEqual("Isolated", Environment.GetEnvironmentVariable("TENANT_MODE"));
}
```

Culture changes have no constant, so use `[DoNotParallelize]` for them (`MSTEST0076`). For files, use `TestContext.TestTempDirectory` instead of fixed paths (`MSTEST0077`).

## Execution conditions

Declare skip conditions as attributes instead of checking at the start of the test:

- `[OSCondition(OperatingSystems.Linux)]` (`MSTEST0061`)
- `[ArchitectureCondition(TestArchitectures.Arm64)]` (`MSTEST0079`)
- `[CICondition(ConditionMode.Include)]` (`MSTEST0080`)
- `[ExecutableCondition("docker")]` checks that the executable is on `PATH`; `[ExecutableCondition("git", "--version", TimeoutSeconds = 5)]` checks that the command succeeds (`MSTEST0083`).

If a test carries `[SupportedOSPlatform]`, add a matching `[OSCondition]` as well (`MSTEST0084`).

A programmatic assembly-wide filter is registered with `[assembly: TestFilterProvider<FastSuiteFilter>()]` (4.4, modern .NET, experimental). The filter type implements `ITestFilter` (`MSTEST0081`).

## `testconfig.json`

```json
{
  "mstest": {
    "output": {
      "captureTrace": "Live"
    },
    "execution": {
      "captureAssertionFailureDiagnostics": true
    }
  }
}
```

- `output.captureTrace` takes `None`, `Result` or `Live` (4.4); `Live` streams console and trace output while the test runs. `true` and `false` still work and mean `Result` and `None`.
- `execution.captureAssertionFailureDiagnostics` (4.5, default `false`) writes up to three JSON snapshots per failed attempt (`mstest-assertion-failure-state-attempt-*-invocation-*-capture-*.json`) and attaches them to the failed result. It does nothing under Native AOT, on browser/WASI or on UWP/WinUI.
