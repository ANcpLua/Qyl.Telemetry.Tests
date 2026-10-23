using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qyl.Api.Contracts;


namespace Qyl.Telemetry.IntegrationTests.Contracts;

[TestClass]
public sealed class ContractParityTests
{
    private const string NullRight = "optional-tracedivergence-b-null";
    private const string NullLeft = "optional-tracedivergence-a-null";
    private static readonly string s_fixturePath = Path.Combine(AppContext.BaseDirectory, "Contracts", "Fixtures", "contract-parity.json");
    private static readonly JsonArray s_fixtures = JsonNode.Parse(File.ReadAllText(s_fixturePath))!.AsArray();
    private static Dictionary<string, JsonNode> s_typescript = null!;

    [ClassInitialize]
    [Timeout(60_000, CooperativeCancellation = true)]
    public static async Task ReadTypeScriptResults(TestContext context)
    {
        var repository = FindRepository();
        var start = new ProcessStartInfo("bun") { WorkingDirectory = repository };
        start.ArgumentList.Add(Path.Combine(repository, "services", "qyl.mcp", "contract-parity.ts"));
        start.ArgumentList.Add(s_fixturePath);
        var result = await ChildProcess.RunAsync(start, "TypeScript contract fixtures", context.CancellationToken,
            new ChildProcessOptions { ExecutionTimeout = TimeSpan.FromSeconds(30) });
        if (result.ExitCode != 0) throw new InvalidOperationException($"TypeScript fixtures failed: {result.Error}");
        s_typescript = JsonNode.Parse(result.Output)!.AsArray().ToDictionary(node => node!["id"]!.GetValue<string>(), node => node!);
    }

    public static IEnumerable<object[]> Cases() => s_fixtures.Select(fixture => new object[] { fixture!["id"]!.GetValue<string>() });
    public static IEnumerable<object[]> RoundTrips() => s_fixtures.Where(fixture => fixture!["roundTrip"]!.GetValue<bool>())
        .Select(fixture => new object[] { fixture!["id"]!.GetValue<string>() });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ContractValidation_SharedFixture_CSharpMatchesDeclaredResult(string fixtureId)
    {
        var fixture = Fixture(fixtureId);
        var issue = ContractJson.Validate(fixture["schema"]!.GetValue<string>(), fixture["value"]);
        Assert.AreEqual(fixture["valid"]!.GetValue<bool>(), issue is null, $"{fixtureId}: {issue}");
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ContractValidation_SharedFixture_TypeScriptMatchesDeclaredResult(string fixtureId)
    {
        var fixture = Fixture(fixtureId);
        var actual = s_typescript[fixtureId];
        Assert.AreEqual(fixture["valid"]!.GetValue<bool>(), actual["valid"]!.GetValue<bool>(), $"{fixtureId}: {actual}");
    }

    [TestMethod]
    [DynamicData(nameof(RoundTrips))]
    public void ContractSerialization_AcceptedFixture_BothRuntimesPreserveEveryValue(string fixtureId)
    {
        var fixture = Fixture(fixtureId);
        var expected = fixture["value"];
        var csharp = ContractJson.RoundTrip(fixture["schema"]!.GetValue<string>(), ContractJson.ToElement(expected));
        var typescript = s_typescript[fixtureId]["value"];
        Assert.IsTrue(JsonNode.DeepEquals(expected, JsonNode.Parse(csharp.GetRawText())), $"C# changed {fixtureId}: {csharp}");
        Assert.IsTrue(JsonNode.DeepEquals(expected, typescript), $"TypeScript changed {fixtureId}: {typescript}");
    }

    [TestMethod]
    public void GeneratedSchemas_CSharpAndTypeScript_AreByteIdentical()
    {
        using var stream = typeof(ContractJson).Assembly.GetManifestResourceStream("qyl.contract-schemas.json");
        Assert.IsNotNull(stream);
        using var reader = new StreamReader(stream);
        var typescript = File.ReadAllText(Path.Combine(FindRepository(), "services", "qyl.mcp", "src", "contracts", "generated", "contract-schemas.json"));
        Assert.AreEqual(typescript, reader.ReadToEnd());
    }

    [TestMethod]
    [DataRow(NullRight, "A absent vs B null")]
    [DataRow(NullLeft, "A null vs B absent")]
    public void TraceSummary_NullAndAbsentValues_DescribesPresenceAccurately(string fixtureId, string expected)
    {
        Assert.Contains(expected, s_typescript[fixtureId]["summary"]!.GetValue<string>());
    }

    [TestMethod]
    public void ContractSerialization_UnsetOptionalField_OmitsTheField()
    {
        var expected = Fixture("triage-output")["value"]!;
        var producer = expected.DeepClone().AsObject();
        producer["next_cursor"] = null;
        var output = ContractJson.Serialize("Mcp.Tools.TriageOutput", ContractJson.ToElement(producer));
        Assert.IsTrue(JsonNode.DeepEquals(expected, JsonNode.Parse(output.GetRawText())));
    }

    [TestMethod]
    public void ContractSerialization_NullRequiredArray_RejectsTheProducer()
    {
        var producer = ContractJson.ToElement(Fixture("null-required-array")["value"]);
        Assert.ThrowsExactly<JsonException>(() => ContractJson.Serialize("Mcp.Tools.TriageOutput", producer));
    }

    [TestMethod]
    public void ContractSerialization_UnknownField_RejectsTheProducer()
    {
        var producer = ContractJson.ToElement(Fixture("unknown-output")["value"]);
        Assert.ThrowsExactly<JsonException>(() => ContractJson.Serialize("Mcp.Tools.TriageOutput", producer));
    }

    private static JsonNode Fixture(string id) => s_fixtures.First(fixture => fixture!["id"]!.GetValue<string>() == id)!;

    private static string FindRepository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "contracts", "main.tsp"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the parity suite inside the qyl checkout.");
    }
}
