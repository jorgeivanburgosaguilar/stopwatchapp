using System.Runtime.CompilerServices;

// Lets StopwatchApp.Tests see internal types (e.g. SchemaMigrations) without making them public —
// See AGENTS.md §5/§6: keep internal plumbing internal while exposing it to the test assembly.
[assembly: InternalsVisibleTo("StopwatchApp.Tests")]
