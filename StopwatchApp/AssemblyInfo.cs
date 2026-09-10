using System.Runtime.CompilerServices;

// Lets StopwatchApp.Tests see internal types (e.g. SchemaMigrations) without making them public —
// AGENTS.md §17 2026-09-10 "SchemaMigrations visibility".
[assembly: InternalsVisibleTo("StopwatchApp.Tests")]
