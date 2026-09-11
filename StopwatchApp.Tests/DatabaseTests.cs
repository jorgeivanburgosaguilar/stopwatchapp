using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using StopwatchApp.Models;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Round-trip tests for <see cref="Database"/> against a temporary file-backed database created
/// and deleted per test class (AGENTS.md §13 — never against the real <c>%LOCALAPPDATA%</c> file).
/// </summary>
[SuppressMessage(
  "Design",
  "CA1001:Types that own disposable fields should be disposable",
  Justification = "Disposal is handled by xUnit's IAsyncLifetime.DisposeAsync, not the standard IDisposable pattern."
)]
public sealed class DatabaseTests : IAsyncLifetime
{
  private readonly string _databasePath = Path.Combine(
    Path.GetTempPath(),
    $"stopwatch-tests-{Guid.NewGuid():N}.db"
  );
  private Database _database = null!;

  public async Task InitializeAsync()
  {
    _database = new Database(_databasePath);
    await _database.InitializeAsync();
  }

  public async Task DisposeAsync()
  {
    await _database.DisposeAsync();
    if (File.Exists(_databasePath))
    {
      File.Delete(_databasePath);
    }
  }

  [Fact]
  public async Task GetAllRecordsAsync_OnEmptyTable_ReturnsEmptyList()
  {
    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();

    Assert.Empty(records);
  }

  [Fact]
  public async Task GetAllRecordsAsync_AfterInsertingInOrder_ReadsBackNewestFirst()
  {
    await _database.SaveRecordAsync(0, 1000, 60_000);
    await _database.SaveRecordAsync(1, 2000, 120_000);
    await _database.SaveRecordAsync(2, 3000, 180_000);

    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();

    Assert.Equal(3, records.Count);
    Assert.Equal(2, records[0].StartTimestamp);
    Assert.Equal(1, records[1].StartTimestamp);
    Assert.Equal(0, records[2].StartTimestamp);
  }

  [Fact]
  public async Task SaveRecordAsync_FloorsElapsedMsToWholeMinutes()
  {
    await _database.SaveRecordAsync(startTimestamp: 0, endTimestamp: 30_000, elapsedMs: 30_000);

    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();

    Assert.Equal(0, Assert.Single(records).ElapsedMinutes);
  }

  [Fact]
  public async Task ClearAllRecordsAsync_EmptiesTheTable()
  {
    await _database.SaveRecordAsync(0, 1000, 60_000);
    await _database.SaveRecordAsync(1, 2000, 120_000);

    await _database.ClearAllRecordsAsync();
    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();

    Assert.Empty(records);
  }

  [Fact]
  public async Task LoadPausedSessionAsync_WithNoSavedSession_ReturnsNull()
  {
    PausedSession? session = await _database.LoadPausedSessionAsync();

    Assert.Null(session);
  }

  [Fact]
  public async Task SavePausedSessionAsync_ThenLoad_RoundTripsLaps()
  {
    Lap lap = new(Id: 1, StartTimestamp: 0, EndTimestamp: 61_000, ElapsedMinutes: 1);
    PausedSession original = new(
      ElapsedTime: 61_000,
      SessionStartTime: 0,
      Laps: [lap],
      LastLapElapsed: 61_000,
      LastLapTimestamp: 61_000,
      PausedAt: 61_000
    );

    await _database.SavePausedSessionAsync(original);
    PausedSession? loaded = await _database.LoadPausedSessionAsync();

    Assert.NotNull(loaded);
    Assert.Equal(original.ElapsedTime, loaded.ElapsedTime);
    Assert.Equal(original.SessionStartTime, loaded.SessionStartTime);
    Assert.Equal(original.LastLapElapsed, loaded.LastLapElapsed);
    Assert.Equal(original.LastLapTimestamp, loaded.LastLapTimestamp);
    Assert.Equal(original.PausedAt, loaded.PausedAt);
    Lap loadedLap = Assert.Single(loaded.Laps);
    Assert.Equal(lap, loadedLap);
  }

  [Fact]
  public async Task SavePausedSessionAsync_CalledTwice_OverwritesTheSingleSlot()
  {
    Lap firstLap = new(1, 0, 1000, 0);
    Lap secondLap = new(1, 0, 2000, 0);
    await _database.SavePausedSessionAsync(
      new PausedSession(1000, 0, [firstLap], 1000, 1000, 1000)
    );
    await _database.SavePausedSessionAsync(
      new PausedSession(2000, 0, [secondLap], 2000, 2000, 2000)
    );

    PausedSession? loaded = await _database.LoadPausedSessionAsync();

    Assert.NotNull(loaded);
    Assert.Equal(2000, loaded.ElapsedTime);
    Assert.Equal(secondLap, Assert.Single(loaded.Laps));
  }

  [Fact]
  public async Task ClearPausedSessionAsync_RemovesTheRow()
  {
    await _database.SavePausedSessionAsync(new PausedSession(1000, 0, [], 0, 0, 1000));

    await _database.ClearPausedSessionAsync();
    PausedSession? loaded = await _database.LoadPausedSessionAsync();

    Assert.Null(loaded);
  }

  [Fact]
  public async Task LoadPausedSessionAsync_WithCorruptLapsJson_ReturnsNullWithoutThrowing()
  {
    await _database.SavePausedSessionAsync(new PausedSession(1000, 0, [], 0, 0, 1000));
    await CorruptLapsJsonAsync();

    PausedSession? loaded = await _database.LoadPausedSessionAsync();

    Assert.Null(loaded);
  }

  [Fact]
  public async Task LoadWindowPositionAsync_WithNoSavedPosition_ReturnsNull()
  {
    (int X, int Y)? position = await _database.LoadWindowPositionAsync();

    Assert.Null(position);
  }

  [Fact]
  public async Task SaveWindowPositionAsync_ThenLoad_RoundTrips()
  {
    await _database.SaveWindowPositionAsync(123, 456);

    (int X, int Y)? loaded = await _database.LoadWindowPositionAsync();

    Assert.NotNull(loaded);
    Assert.Equal(123, loaded.Value.X);
    Assert.Equal(456, loaded.Value.Y);
  }

  [Fact]
  public async Task SaveWindowPositionAsync_CalledTwice_OverwritesTheSingleSlot()
  {
    await _database.SaveWindowPositionAsync(1, 2);
    await _database.SaveWindowPositionAsync(3, 4);

    (int X, int Y)? loaded = await _database.LoadWindowPositionAsync();

    Assert.NotNull(loaded);
    Assert.Equal(3, loaded.Value.X);
    Assert.Equal(4, loaded.Value.Y);
  }

  [Fact]
  public async Task InitializeAsync_OnFreshDatabase_StampsCurrentSchemaVersion()
  {
    long userVersion = await ReadUserVersionAsync();

    Assert.Equal(SchemaMigrations.Current, userVersion);
  }

  [Fact]
  public async Task InitializeAsync_OnLegacyDatabaseWithNoVersionStamp_AdoptsItAsV1AndKeepsData()
  {
    // Build a database the pre-versioning way: both CREATE TABLE statements, one row, and no
    // PRAGMA user_version stamp (defaults to 0) — simulating a database created by an earlier
    // build of this app, before schema versioning existed.
    await _database.DisposeAsync();
    if (File.Exists(_databasePath))
    {
      File.Delete(_databasePath);
    }

    await using (SqliteConnection legacyConnection = new($"Data Source={_databasePath}"))
    {
      await legacyConnection.OpenAsync();
      await using SqliteCommand createTables = legacyConnection.CreateCommand();
      createTables.CommandText = """
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
      await createTables.ExecuteNonQueryAsync();

      await using SqliteCommand insertRow = legacyConnection.CreateCommand();
      insertRow.CommandText = """
        INSERT INTO records (startTimestamp, endTimestamp, elapsedMinutes)
        VALUES (0, 60000, 1);
        """;
      await insertRow.ExecuteNonQueryAsync();
    }

    _database = new Database(_databasePath);
    await _database.InitializeAsync();

    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();
    long userVersion = await ReadUserVersionAsync();

    Assert.Equal(SchemaMigrations.Current, userVersion);
    StopwatchRecord record = Assert.Single(records);
    Assert.Equal(60000, record.EndTimestamp);
  }

  [Fact]
  public async Task InitializeAsync_OnV1Database_AppliesMigration2AndKeepsData()
  {
    // Build a database at exactly the pre-S11b shape: records/paused_session present (migration 1's
    // DDL) and user_version stamped at 1, no window_position table — simulating a database created
    // by the build immediately before this stage. Mirrors
    // InitializeAsync_OnLegacyDatabaseWithNoVersionStamp_AdoptsItAsV1AndKeepsData above, one version
    // later (AGENTS.md §9 "Schema versioning", §17 dated 2026-09-11).
    await _database.DisposeAsync();
    if (File.Exists(_databasePath))
    {
      File.Delete(_databasePath);
    }

    await using (SqliteConnection v1Connection = new($"Data Source={_databasePath}"))
    {
      await v1Connection.OpenAsync();
      await using SqliteCommand createTables = v1Connection.CreateCommand();
      createTables.CommandText = """
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
      await createTables.ExecuteNonQueryAsync();

      await using SqliteCommand insertRow = v1Connection.CreateCommand();
      insertRow.CommandText = """
        INSERT INTO records (startTimestamp, endTimestamp, elapsedMinutes)
        VALUES (0, 60000, 1);
        """;
      await insertRow.ExecuteNonQueryAsync();

      await using SqliteCommand stampVersion = v1Connection.CreateCommand();
      stampVersion.CommandText = "PRAGMA user_version = 1;";
      await stampVersion.ExecuteNonQueryAsync();
    }

    _database = new Database(_databasePath);
    await _database.InitializeAsync();

    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();
    (int X, int Y)? windowPosition = await _database.LoadWindowPositionAsync();
    long userVersion = await ReadUserVersionAsync();

    Assert.Equal(SchemaMigrations.Current, userVersion);
    Assert.Equal(2, userVersion);
    StopwatchRecord record = Assert.Single(records);
    Assert.Equal(60000, record.EndTimestamp);
    Assert.Null(windowPosition);

    // The new table exists and is usable, not just present as an empty migration no-op.
    await _database.SaveWindowPositionAsync(10, 20);
    windowPosition = await _database.LoadWindowPositionAsync();
    Assert.NotNull(windowPosition);
    Assert.Equal(10, windowPosition.Value.X);
    Assert.Equal(20, windowPosition.Value.Y);
  }

  [Fact]
  public async Task InitializeAsync_CalledTwice_IsIdempotent()
  {
    await _database.SaveRecordAsync(0, 1000, 60_000);

    await _database.InitializeAsync();

    IReadOnlyList<StopwatchRecord> records = await _database.GetAllRecordsAsync();
    long userVersion = await ReadUserVersionAsync();
    Assert.Equal(SchemaMigrations.Current, userVersion);
    Assert.Single(records);
  }

  [Fact]
  public async Task GetAllRecordsAsync_MapsEveryColumnToItsOwnMember()
  {
    await _database.SaveRecordAsync(startTimestamp: 111, endTimestamp: 999_222, elapsedMs: 180_000);

    StopwatchRecord record = Assert.Single(await _database.GetAllRecordsAsync());

    Assert.Equal(111, record.StartTimestamp);
    Assert.Equal(999_222, record.EndTimestamp);
    Assert.Equal(3, record.ElapsedMinutes);
  }

  private async Task CorruptLapsJsonAsync()
  {
    await using SqliteConnection connection = new($"Data Source={_databasePath}");
    await connection.OpenAsync();
    await using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE paused_session SET lapsJson = 'not valid json' WHERE id = 1;";
    await command.ExecuteNonQueryAsync();
  }

  private async Task<long> ReadUserVersionAsync()
  {
    await using SqliteConnection connection = new($"Data Source={_databasePath}");
    await connection.OpenAsync();
    await using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    object? result = await command.ExecuteScalarAsync();
    return result is long version ? version : 0;
  }
}
