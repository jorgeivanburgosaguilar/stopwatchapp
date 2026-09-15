using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IStopwatchStore"/>. See AGENTS.md §9 for the schema.
/// Rows are mapped via Dapper, by column name, against the model records' constructors — see
/// <see cref="SchemaMigrations"/> for how the schema itself is versioned and applied.
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
  /// Opens the database connection and brings the schema up to <see cref="SchemaMigrations.Current"/>
  /// by applying every not-yet-applied entry in <see cref="SchemaMigrations.All"/>, in order. Call
  /// once at startup, after construction; idempotent across repeated calls — a database already at
  /// the current version applies nothing.
  /// </summary>
  public async Task InitializeAsync()
  {
    if (_connection is null)
    {
      string? directory = Path.GetDirectoryName(_databasePath);
      if (!string.IsNullOrEmpty(directory))
      {
        Directory.CreateDirectory(directory);
      }

      _connection = new SqliteConnection($"Data Source={_databasePath}");
      await _connection.OpenAsync().ConfigureAwait(false);
    }

    long appliedVersion = await _connection
      .ExecuteScalarAsync<long>("PRAGMA user_version;")
      .ConfigureAwait(false);

    foreach (Migration migration in SchemaMigrations.All)
    {
      if (migration.Version <= appliedVersion)
      {
        continue;
      }

      await using SqliteTransaction transaction = (SqliteTransaction)
        await _connection.BeginTransactionAsync().ConfigureAwait(false);
      await _connection.ExecuteAsync(migration.Sql, transaction: transaction).ConfigureAwait(false);
      // PRAGMA user_version cannot be parameterized (SQLite does not accept a bound parameter in
      // pragma position); migration.Version is a hardcoded long from SchemaMigrations, never
      // user input, so this interpolation carries no injection surface.
      await _connection
        .ExecuteAsync($"PRAGMA user_version = {migration.Version};", transaction: transaction)
        .ConfigureAwait(false);
      await transaction.CommitAsync().ConfigureAwait(false);
    }
  }

  /// <inheritdoc />
  public async Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs)
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      await connection
        .ExecuteAsync(
          """
          INSERT INTO records (startTimestamp, endTimestamp, elapsedMinutes)
          VALUES (@startTimestamp, @endTimestamp, @elapsedMinutes);
          """,
          new
          {
            startTimestamp,
            endTimestamp,
            elapsedMinutes = elapsedMs / 60000,
          }
        )
        .ConfigureAwait(false);

      return await connection
        .ExecuteScalarAsync<long>("SELECT last_insert_rowid();")
        .ConfigureAwait(false);
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

      IEnumerable<StopwatchRecord> records = await connection
        .QueryAsync<StopwatchRecord>(
          """
          SELECT id, startTimestamp, endTimestamp, elapsedMinutes
          FROM records
          ORDER BY id DESC;
          """
        )
        .ConfigureAwait(false);
      return records.AsList();
    }
    catch (Exception)
    {
      return [];
    }
  }

  /// <inheritdoc />
  public async Task DeleteRecordAsync(long id)
  {
    try
    {
      SqliteConnection connection = RequireConnection();
      await connection
        .ExecuteAsync("DELETE FROM records WHERE id = @id;", new { id })
        .ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async Task ClearAllRecordsAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();
      await connection.ExecuteAsync("DELETE FROM records;").ConfigureAwait(false);
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

      await connection
        .ExecuteAsync(
          """
          INSERT INTO paused_session
            (id, elapsedTime, sessionStartTime, lapsJson, lastLapElapsed, lastLapTimestamp, pausedAt)
          VALUES
            (1, @elapsedTime, @sessionStartTime, @lapsJson, @lastLapElapsed, @lastLapTimestamp, @pausedAt)
          ON CONFLICT(id) DO UPDATE SET
            elapsedTime = excluded.elapsedTime,
            sessionStartTime = excluded.sessionStartTime,
            lapsJson = excluded.lapsJson,
            lastLapElapsed = excluded.lastLapElapsed,
            lastLapTimestamp = excluded.lastLapTimestamp,
            pausedAt = excluded.pausedAt;
          """,
          new
          {
            elapsedTime = session.ElapsedTime,
            sessionStartTime = session.SessionStartTime,
            lapsJson,
            lastLapElapsed = session.LastLapElapsed,
            lastLapTimestamp = session.LastLapTimestamp,
            pausedAt = session.PausedAt,
          }
        )
        .ConfigureAwait(false);
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

      PausedSessionRow? row = await connection
        .QuerySingleOrDefaultAsync<PausedSessionRow>(
          """
          SELECT elapsedTime, sessionStartTime, lapsJson, lastLapElapsed, lastLapTimestamp, pausedAt
          FROM paused_session
          WHERE id = 1;
          """
        )
        .ConfigureAwait(false);
      if (row is null)
      {
        return null;
      }

      List<Lap>? laps = JsonSerializer.Deserialize<List<Lap>>(row.LapsJson);
      if (laps is null)
      {
        return null;
      }

      return new PausedSession(
        row.ElapsedTime,
        row.SessionStartTime,
        laps,
        row.LastLapElapsed,
        row.LastLapTimestamp,
        row.PausedAt
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
      await connection
        .ExecuteAsync("DELETE FROM paused_session WHERE id = 1;")
        .ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async Task SaveWindowPositionAsync(int x, int y)
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      await connection
        .ExecuteAsync(
          """
          INSERT INTO window_position (id, x, y)
          VALUES (1, @x, @y)
          ON CONFLICT(id) DO UPDATE SET
            x = excluded.x,
            y = excluded.y;
          """,
          new { x, y }
        )
        .ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a failed write must never surface to the UI.
    }
  }

  /// <inheritdoc />
  public async Task<(int X, int Y)?> LoadWindowPositionAsync()
  {
    try
    {
      SqliteConnection connection = RequireConnection();

      WindowPositionRow? row = await connection
        .QuerySingleOrDefaultAsync<WindowPositionRow>(
          """
          SELECT x, y
          FROM window_position
          WHERE id = 1;
          """
        )
        .ConfigureAwait(false);
      if (row is null)
      {
        return null;
      }

      return ((int)row.X, (int)row.Y);
    }
    catch (Exception)
    {
      // Deliberate per AGENTS.md §9: a corrupt or unreadable saved position returns null rather
      // than throwing.
      return null;
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

  /// <summary>
  /// The raw <c>paused_session</c> row shape, used only to receive Dapper's column mapping before
  /// <see cref="LapsJson"/> is deserialized into <see cref="PausedSession.Laps"/>. Not part of the
  /// public data model — <see cref="PausedSession"/> is.
  /// </summary>
  private sealed record PausedSessionRow(
    long ElapsedTime,
    long SessionStartTime,
    string LapsJson,
    long LastLapElapsed,
    long LastLapTimestamp,
    long PausedAt
  );

  /// <summary>
  /// The raw <c>window_position</c> row shape, used only to receive Dapper's column mapping
  /// before it is narrowed to <c>int</c> and projected into the
  /// <see cref="IStopwatchStore.LoadWindowPositionAsync"/> tuple. <c>long</c>, not <c>int</c>,
  /// because SQLite's <c>INTEGER</c> affinity always round-trips through Microsoft.Data.Sqlite as
  /// <see cref="long"/> — Dapper's constructor-based record materialization requires an exact
  /// parameter-type match against the column's runtime type, not just a name match (see AGENTS.md
  /// §9), the same reason every column in <see cref="PausedSessionRow"/> is
  /// <see cref="long"/>.
  /// </summary>
  private sealed record WindowPositionRow(long X, long Y);
}
