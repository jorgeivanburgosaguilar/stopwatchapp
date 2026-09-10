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

  private async Task CorruptLapsJsonAsync()
  {
    await using SqliteConnection connection = new($"Data Source={_databasePath}");
    await connection.OpenAsync();
    await using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE paused_session SET lapsJson = 'not valid json' WHERE id = 1;";
    await command.ExecuteNonQueryAsync();
  }
}
