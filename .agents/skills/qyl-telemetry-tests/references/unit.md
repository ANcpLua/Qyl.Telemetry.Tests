# Qyl.Telemetry.UnitTests

Verify deterministic Qyl-owned logic: attribute mapping, registry validation,
trace selection, resource partitioning, numeric conversions, evidence
interpretation and assertion diagnostics.
Inputs are explicit records or values; output, rejection and observable state
changes are the evidence. The expected result comes from the contract rather
than the algorithm being tested. Preserve type, field presence, ordering and
identity only where those affect the promised behavior.

Use ordinary in-memory construction and native MSTest rows. Replace genuine
external dependencies at their public boundary when needed to isolate the unit;
do not mock the behavior under test or create a seam merely to obtain a mock.
No receiver startup, listening socket, child application, external URL, Collector
storage or package publication is needed to establish a pure result.

For example, two punctuation variants can verify a pure key mapping. This proves
the mapping result; an integration case separately proves both survive capture
on one real activity and transport. A redactor unit can verify array elements
and benign controls without claiming a full read endpoint was exercised.

A helper with its own assertions is still checked evidence. Verify required
parents/results before their properties and await async assertions. Cancellation
or suspension cases use a releasable dependency, observe successful resumption
and cancellation separately, and join original work before disposing resources.
Use native context and lifecycle instead of reflection-based invocation.

The [unit-test agent article](https://devblogs.microsoft.com/dotnet/polyglot-unit-testing-agent/)
explicitly excludes integration and end-to-end generation. Apply its repository
research and scenario-to-case mapping here. Choose an implementation effort that
fits the request, inspect plausible accepted wrong outputs, and confirm new
cases are discovered by the normal repository test entrypoint. Its full-suite
verification is an upstream workflow, not a requirement to rerun unrelated work
for each local edit.

Use source tests that already cover a behavior before recommending another
copy here. This repository adds Qyl-domain and cross-package confidence; it does
not replace the source owners' focused suites or test the language/framework
itself. Production algorithms stay with their production package.
