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
/// <para>
/// <b>Never edit a shipped migration.</b> Once a migration has been released, its <see cref="Migration.Sql"/>
/// is frozen — a schema change is always a new entry appended with the next version number, never an
/// edit to an existing one. Editing a shipped migration would silently no-op on any database that
/// already recorded that version as applied.
/// </para>
/// <para>
/// <b>Migration 1 is the exception to "no <c>IF NOT EXISTS</c>".</b> It reproduces the two
/// <c>CREATE TABLE IF NOT EXISTS</c> statements this project shipped before schema versioning
/// existed, verbatim, so that a pre-existing database (which already has both tables but carries
/// <c>user_version = 0</c>) adopts them as its version-1 baseline instead of failing on a duplicate
/// table. Every migration from version 2 onward uses plain <c>CREATE TABLE</c> / <c>ALTER TABLE</c> —
/// the version stamp guarantees each one runs exactly once, and an <c>IF NOT EXISTS</c> there would
/// only hide an ordering bug.
/// </para>
/// </remarks>
internal static class SchemaMigrations
{
  private const string V1CreateTables = """
    CREATE TABLE IF NOT EXISTS records (
      id             INTEGER PRIMARY KEY AUTOINCREMENT,
      startTimestamp INTEGER NOT NULL,
      endTimestamp   INTEGER NOT NULL,
      elapsedMinutes INTEGER NOT NULL
    );

    CREATE TABLE IF NOT EXISTS paused_session (
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
