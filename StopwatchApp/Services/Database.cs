using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IStopwatchStore"/>. See AGENTS.md §9 for the schema.
/// </summary>
[SuppressMessage(
  "Design",
  "CA1031:Do not catch general exception types",
  Justification = "Deliberate per AGENTS.md §9: every storage method swallows all errors — a corrupt or unreadable saved session returns null, and a failed write must never surface to the UI."
)]
public sealed class Database : IStopwatchStore, IAsyncDisposable
{
  private readonly string _databasePath;
  private SqliteConnection? _connection;

  /// <summary>
  /// Initializes a new instance of the <see cref="Database"/> class for the given file path. The
  /// file and its containing directory are created by <see cref="InitializeAsync"/> if they do
  /// not already exist.
  /// </summary>
  /// <param name="databasePath">The path to the SQLite database file.</param>
  public Database(string databasePath)
  {
    _databasePath = databasePath;
  }

  /// <summary>
  /// Gets the default database path: <c>%LOCALAPPDATA%\StopwatchApp\stopwatch.db</c>.
  /// </summary>
  public static string DefaultDatabasePath { get; } =
    Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "StopwatchApp",
      "stopwatch.db"
    );

  /// <summary>
  /// Opens the database connection and creates the schema if it does not already exist. Call once
  /// at startup, after construction; idempotent across repeated calls.
  /// </summary>
  public async Task InitializeAsync()
  {
    string? directory = Path.GetDirectoryName(_databasePath);
    if (!string.IsNullOrEmpty(directory))
    {
      Directory.CreateDirectory(directory);
    }

    _connection = new SqliteConnection($"Data Source={_databasePath}");
    await _connection.OpenAsync().ConfigureAwait(false);

    await using SqliteCommand createRecords = _connection.CreateCommand();
    createRecords.CommandText = """
      CREATE TABLE IF NOT EXISTS records (
        id             INTEGER PRIMARY KEY AUTOINCREMENT,
        startTimestamp INTEGER NOT NULL,
        endTimestamp   INTEGER NOT NULL,
        elapsedMinutes INTEGER NOT NULL
      );
      """;
    await createRecords.ExecuteNonQueryAsync().ConfigureAwait(false);

    await using SqliteCommand createPausedSession = _connection.CreateCommand();
    createPausedSession.CommandText = """
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
    await createPausedSession.ExecuteNonQueryAsync().ConfigureAwait(false);
  }

  /// <inheritdoc />
  public async Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs)
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      await using SqliteCommand insert = connection.CreateCommand();
      insert.CommandText = """
        INSERT INTO records (startTimestamp, endTimestamp, elapsedMinutes)
        VALUES ($startTimestamp, $endTimestamp, $elapsedMinutes);
        """;
      insert.Parameters.AddWithValue("$startTimestamp", startTimestamp);
      insert.Parameters.AddWithValue("$endTimestamp", endTimestamp);
      insert.Parameters.AddWithValue("$elapsedMinutes", elapsedMs / 60000);
      await insert.ExecuteNonQueryAsync().ConfigureAwait(false);

      await using SqliteCommand lastId = connection.CreateCommand();
      lastId.CommandText = "SELECT last_insert_rowid();";
      object? result = await lastId.ExecuteScalarAsync().ConfigureAwait(false);
      return result is long id ? id : 0;
    }
    catch (Exception)
    {
      return 0;
    }
  }

  /// <inheritdoc />
  public async Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
        SELECT id, startTimestamp, endTimestamp, elapsedMinutes
        FROM records
        ORDER BY id DESC;
        """;

      List<StopwatchRecord> records = [];
      await using SqliteDataReader reader = await command
        .ExecuteReaderAsync()
        .ConfigureAwait(false);
      while (await reader.ReadAsync().ConfigureAwait(false))
      {
        records.Add(
          new StopwatchRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3)
          )
        );
      }
      return records;
    }
    catch (Exception)
    {
      return [];
    }
  }

  /// <inheritdoc />
  public async Task ClearAllRecordsAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();
      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM records;";
      await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async Task SavePausedSessionAsync(PausedSession session)
  {
    try
    {
      SqliteConnection connection = RequireConnection();
      string lapsJson = JsonSerializer.Serialize(session.Laps);

      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
        INSERT INTO paused_session
          (id, elapsedTime, sessionStartTime, lapsJson, lastLapElapsed, lastLapTimestamp, pausedAt)
        VALUES
          (1, $elapsedTime, $sessionStartTime, $lapsJson, $lastLapElapsed, $lastLapTimestamp, $pausedAt)
        ON CONFLICT(id) DO UPDATE SET
          elapsedTime = excluded.elapsedTime,
          sessionStartTime = excluded.sessionStartTime,
          lapsJson = excluded.lapsJson,
          lastLapElapsed = excluded.lastLapElapsed,
          lastLapTimestamp = excluded.lastLapTimestamp,
          pausedAt = excluded.pausedAt;
        """;
      command.Parameters.AddWithValue("$elapsedTime", session.ElapsedTime);
      command.Parameters.AddWithValue("$sessionStartTime", session.SessionStartTime);
      command.Parameters.AddWithValue("$lapsJson", lapsJson);
      command.Parameters.AddWithValue("$lastLapElapsed", session.LastLapElapsed);
      command.Parameters.AddWithValue("$lastLapTimestamp", session.LastLapTimestamp);
      command.Parameters.AddWithValue("$pausedAt", session.PausedAt);
      await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async Task<PausedSession?> LoadPausedSessionAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = """
        SELECT elapsedTime, sessionStartTime, lapsJson, lastLapElapsed, lastLapTimestamp, pausedAt
        FROM paused_session
        WHERE id = 1;
        """;

      await using SqliteDataReader reader = await command
        .ExecuteReaderAsync()
        .ConfigureAwait(false);
      if (!await reader.ReadAsync().ConfigureAwait(false))
      {
        return null;
      }

      long elapsedTime = reader.GetInt64(0);
      long sessionStartTime = reader.GetInt64(1);
      string lapsJson = reader.GetString(2);
      long lastLapElapsed = reader.GetInt64(3);
      long lastLapTimestamp = reader.GetInt64(4);
      long pausedAt = reader.GetInt64(5);

      List<Lap>? laps = JsonSerializer.Deserialize<List<Lap>>(lapsJson);
      if (laps is null)
      {
        return null;
      }

      return new PausedSession(
        elapsedTime,
        sessionStartTime,
        laps,
        lastLapElapsed,
        lastLapTimestamp,
        pausedAt
      );
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a corrupt or unreadable saved session (e.g. malformed
      // lapsJson) returns null rather than throwing.
      return null;
    }
  }

  /// <inheritdoc />
  public async Task ClearPausedSessionAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();
      await using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "DELETE FROM paused_session WHERE id = 1;";
      await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    if (_connection is not null)
    {
      await _connection.DisposeAsync().ConfigureAwait(false);
      // Microsoft.Data.Sqlite pools connections by default: disposing the SqliteConnection
      // returns it to the pool rather than releasing the OS file handle, which would otherwise
      // leave the database file locked (observable when a caller tries to delete it right after
      // disposal, e.g. a temp-file test fixture). Clearing the pool forces the underlying native
      // handle closed.
      SqliteConnection.ClearPool(_connection);
      _connection = null;
    }
  }

  private SqliteConnection RequireConnection() =>
    _connection
    ?? throw new InvalidOperationException("Database.InitializeAsync must be called before use.");
}
