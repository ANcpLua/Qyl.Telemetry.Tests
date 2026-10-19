using System.Reflection;
using JetBrains.Annotations;
using Qyl.Telemetry.SemanticConventions;

namespace Qyl.Telemetry.Tests.Policies;

/// <summary>
///     A telemetry obligation declared on a test method or class. <see cref="TracePerTestAttribute" /> verifies it
///     once the test has passed, against every trace that <see cref="TelemetryCapture" /> or
///     <see cref="Collector.TelemetryPipeline" /> published during the invocation, and fails the test with the
///     violations and an evidence file. A policy without published evidence fails instead of passing vacuously.
/// </summary>
/// <remarks>A method declaration replaces the class declaration of the same policy.</remarks>
[PublicAPI]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public abstract class TelemetryPolicyAttribute : Attribute
{
    private protected TelemetryPolicyAttribute()
    {
    }

    internal string Name => GetType().Name[..^nameof(Attribute).Length];

    internal IEnumerable<string> Check(TelemetryInvocation invocation) => invocation.Traces.Count == 0
        ? ["No trace was published. Capture through TelemetryCapture.TraceAsync or a TelemetryPipeline root inside the test."]
        : Violations(invocation);

    private protected abstract IEnumerable<string> Violations(TelemetryInvocation invocation);

    private protected static string Describe(SpanRecord span) =>
        $"'{span.Name}' ({span.Kind}, {span.Service ?? "no service"}, {span.Scope?.Name ?? "no scope"})";

    internal static IReadOnlyList<TelemetryPolicyAttribute> DeclaredOn(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var declared = method.GetCustomAttributes<TelemetryPolicyAttribute>(inherit: true).ToList();
        var type = method.ReflectedType ?? method.DeclaringType;
        if (type is not null)
            declared.AddRange(type.GetCustomAttributes<TelemetryPolicyAttribute>(inherit: true)
                .Where(policy => declared.TrueForAll(own => own.GetType() != policy.GetType())));
        return declared.AsReadOnly();
    }

    /// <summary>Fails a publication from a plain test class whose policies would otherwise never run.</summary>
    internal static void RejectUnverifiedPolicies()
    {
#pragma warning disable MSTESTEXP
        var context = TestContext.Current;
#pragma warning restore MSTESTEXP
        string? name;
        try { name = context?.FullyQualifiedTestClassName; }
        catch (InvalidOperationException) { return; }
        var type = name is null ? null : AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, throwOnError: false)).OfType<Type>().FirstOrDefault();
        if (type is null || type.IsDefined(typeof(TracePerTestAttribute), inherit: false)) return;
        var policies = type.GetCustomAttributes<TelemetryPolicyAttribute>(inherit: true)
            .Concat(type.GetMethods().SelectMany(test => test.GetCustomAttributes<TelemetryPolicyAttribute>(inherit: true)))
            .Select(policy => policy.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (policies.Length > 0)
            throw new InvalidOperationException($"{name} declares [{string.Join("], [", policies)}], but only a "
                + "[TracePerTest] class verifies telemetry policies. Replace [TestClass] with [TracePerTest].");
    }
}
