using System.Diagnostics.CodeAnalysis;

// Test method names follow the xUnit `MethodUnderTest_Scenario_ExpectedResult` convention, which
// requires underscores for readability. This is a single, justified, project-wide suppression of
// one rule (not a blanket <NoWarn> list, per AGENTS.md §6) scoped to test code only —
// StopwatchApp.csproj still enforces CA1707 normally.
[assembly: SuppressMessage(
  "Naming",
  "CA1707:Identifiers should not contain underscores",
  Justification = "xUnit test names use underscores for readability (MethodUnderTest_Scenario_ExpectedResult).",
  Scope = "module"
)]
