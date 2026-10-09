using StopwatchApp.Models;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// An in-memory <see cref="IStopwatchStore"/> used by <c>StopwatchTimerTests</c> so the state
/// machine is exercised without a temp SQLite database (AGENTS.md §3.1 — <c>StopwatchTimer</c>
/// depends only on the interface).
/// </summary>
internal sealed class FakeStopwatchStore : IStopwatchStore
{
  private readonly List<StopwatchRecord> _records = [];
  private readonly Dictionary<long, IReadOnlyList<Lap>> _laps = [];
  private PausedSession? _pausedSession;

  public TaskCompletionSource? PausedSessionSaveStarted { get; init; }

  public TaskCompletionSource? PausedSessionSaveGate { get; init; }

  /// <summary>When set, <see cref="SaveRecordAsync"/> commits nothing and returns 0 (a failed write).</summary>
  public bool FailRecordSaves { get; set; }

  /// <summary>When set, the interface reads report failure (<see langword="null"/>).</summary>
  public bool FailReads { get; set; }

  public Task<long> SaveRecordAsync(
    long startTimestamp,
    long endTimestamp,
    long elapsedMs,
    IReadOnlyList<Lap> laps
  )
  {
    if (FailRecordSaves)
    {
      return Task.FromResult(0L);
    }

    long id = _records.Count + 1;
    _records.Insert(
      0,
      new StopwatchRecord(id, startTimestamp, endTimestamp, elapsedMs / 60000, laps.Count)
    );
    _laps[id] = [.. laps];
    // Mirrors Database: the record and the snapshot deletion are one atomic write.
    _pausedSession = null;
    return Task.FromResult(id);
  }

  public Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync() =>
    Task.FromResult<IReadOnlyList<StopwatchRecord>>([.. _records]);

  public Task<IReadOnlyList<Lap>> GetLapsAsync(long recordId) =>
    Task.FromResult(_laps.TryGetValue(recordId, out IReadOnlyList<Lap>? laps) ? laps : []);

  // The interface reports a failed read as null; the public members above stay non-null so tests
  // that inspect the fake directly need no null handling.
  async Task<IReadOnlyList<StopwatchRecord>?> IStopwatchStore.GetAllRecordsAsync() =>
    FailReads ? null : await GetAllRecordsAsync();

  async Task<IReadOnlyList<Lap>?> IStopwatchStore.GetLapsAsync(long recordId) =>
    FailReads ? null : await GetLapsAsync(recordId);

  public Task DeleteRecordAsync(long id)
  {
    _records.RemoveAll(record => record.Id == id);
    _laps.Remove(id);
    return Task.CompletedTask;
  }

  public Task ClearAllRecordsAsync()
  {
    _records.Clear();
    _laps.Clear();
    return Task.CompletedTask;
  }

  public async Task SavePausedSessionAsync(PausedSession session)
  {
    PausedSessionSaveStarted?.TrySetResult();
    if (PausedSessionSaveGate is not null)
    {
      await PausedSessionSaveGate.Task;
    }

    _pausedSession = session;
  }

  public Task<PausedSession?> LoadPausedSessionAsync() => Task.FromResult(_pausedSession);

  public Task ClearPausedSessionAsync()
  {
    _pausedSession = null;
    return Task.CompletedTask;
  }
}
