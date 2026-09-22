namespace StopwatchApp.Services;

/// <summary>
/// A single versioned schema step, applied at most once and stamped via SQLite's
/// <c>PRAGMA user_version</c>. See AGENTS.md §9 "Schema versioning".
/// </summary>
/// <param name="Version">
/// The schema version this step advances the database to. Versions are consecutive integers
/// starting at 1 and must never be reused or reordered.
/// </param>
/// <param name="Sql">The DDL executed to advance the database to <paramref name="Version"/>.</param>
internal sealed record Migration(long Version, string Sql);

/// <summary>
/// The ordered, append-only list of schema migrations for <see cref="Database"/>.
/// <see cref="Database.InitializeAsync"/> applies every migration whose <see cref="Migration.Version"/>
/// exceeds the database's current <c>PRAGMA user_version</c>, in order, each inside its own
/// transaction.
/// </summary>
/// <remarks>
/// <b>Never edit a shipped migration.</b> Once a migration has been released, its <see cref="Migration.Sql"/>
/// is frozen — a schema change is always a new entry appended with the next version number, never an
/// edit to an existing one. Editing a shipped migration would silently no-op on any database that
/// already recorded that version as applied. Migration 1 below was rewritten as a one-time exception
/// to that rule: it replaces the app's entire pre-release schema history (the original two-table
/// baseline plus the now-removed <c>window_position</c> table) with the laps-detail baseline, because
/// no shipped database existed with data to preserve at the time of the rewrite. Every future schema
/// change is a new, append-only migration, never another edit to migration 1.
/// </remarks>
internal static class SchemaMigrations
{
  private const string V1CreateTables = """
    CREATE TABLE records (
      id             INTEGER PRIMARY KEY AUTOINCREMENT,
      startTimestamp INTEGER NOT NULL,
      endTimestamp   INTEGER NOT NULL,
      elapsedMinutes INTEGER NOT NULL
    );

    CREATE TABLE record_laps (
      id             INTEGER PRIMARY KEY AUTOINCREMENT,
      recordId       INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,
      lapNumber      INTEGER NOT NULL,
      startTimestamp INTEGER NOT NULL,
      endTimestamp   INTEGER NOT NULL,
      elapsedMinutes INTEGER NOT NULL
    );

    CREATE INDEX record_laps_recordId ON record_laps (recordId, lapNumber DESC);

    CREATE TABLE paused_session (
      id               INTEGER PRIMARY KEY CHECK (id = 1),
      elapsedTime      INTEGER NOT NULL,
      sessionStartTime INTEGER NOT NULL,
      lapsJson         TEXT    NOT NULL,
      lastLapElapsed   INTEGER NOT NULL,
      lastLapTimestamp INTEGER NOT NULL,
      pausedAt         INTEGER NOT NULL
    );
    """;

  /// <summary>
  /// Every migration, in ascending, consecutive <see cref="Migration.Version"/> order.
  /// </summary>
  internal static IReadOnlyList<Migration> All { get; } = [new Migration(1, V1CreateTables)];

  /// <summary>
  /// The schema version a freshly initialized database ends up at — the highest version in
  /// <see cref="All"/>.
  /// </summary>
  internal static long Current => All[^1].Version;
}
