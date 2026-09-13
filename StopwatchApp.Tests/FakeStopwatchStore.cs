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
  private PausedSession? _pausedSession;
  private (int X, int Y)? _windowPosition;

  public TaskCompletionSource? PausedSessionSaveStarted { get; init; }

  public TaskCompletionSource? PausedSessionSaveGate { get; init; }

  public Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs)
  {
    long id = _records.Count + 1;
    _records.Insert(0, new StopwatchRecord(id, startTimestamp, endTimestamp, elapsedMs / 60000));
    return Task.FromResult(id);
  }

  public Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync() =>
    Task.FromResult<IReadOnlyList<StopwatchRecord>>([.. _records]);

  public Task DeleteRecordAsync(long id)
  {
    _records.RemoveAll(record => record.Id == id);
    return Task.CompletedTask;
  }

  public Task ClearAllRecordsAsync()
  {
    _records.Clear();
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

  public Task SaveWindowPositionAsync(int x, int y)
  {
    _windowPosition = (x, y);
    return Task.CompletedTask;
  }

  public Task<(int X, int Y)?> LoadWindowPositionAsync() => Task.FromResult(_windowPosition);
}
