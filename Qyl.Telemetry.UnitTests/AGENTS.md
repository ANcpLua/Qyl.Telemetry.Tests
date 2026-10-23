# Unit tests

Inherit [repository guidance](../AGENTS.md) and the [binding test skill](../SKILL.md). Read the [project skill](skills/qyl-telemetry-unit/SKILL.md) before changing this component.

Use deterministic in-memory dependencies and FakeTimeProvider. Unit tests do not start network listeners, processes or real Collectors.

## Component-specific test policy

This policy overrides the root test defaults for this project:
- Name tests `Should_<ExpectedBehavior>_When_<Condition>`.
- Mark tests with `[TestCategory("Fast")]`.
- Do not seal test classes; derived fixtures must remain possible.

Run `pwsh ./eng/test.ps1 -Suite Unit -NoRestore` from the repository root after dependencies have been restored. The script maps the Unit suite to the Fast category. Keep the same test behavior and data-row counts when changing names or metadata.
