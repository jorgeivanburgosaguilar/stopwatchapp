using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// The UI-free stopwatch state machine: tick loop, start/pause/lap/stop transitions, and the
/// records list. See AGENTS.md §8 for the full behavioral contract, including §12's intentional
/// behaviors — this class reproduces that pseudocode verbatim, not a reinterpretation of it.
/// </summary>
public sealed class StopwatchTimer : IDisposable
{
  private readonly IStopwatchStore _store;
  private readonly TimeProvider _time;
  private readonly SemaphoreSlim _snapshotGate = new(1, 1);
  private readonly long _autosaveIntervalMs;
  private readonly List<Lap> _laps = [];
  private IReadOnlyList<StopwatchRecord> _records = [];
  private long _startTime;
  private long _sessionStartMs;
  private long _lastLapElapsed;
  private long _lastLapTimestamp;
  private long _nextAutosaveElapsedMs;

  /// <summary>
  /// Initializes a new instance of the <see cref="StopwatchTimer"/> class.
  /// </summary>
  /// <param name="store">The persistence surface for completed records and the paused-session snapshot.</param>
  /// <param name="time">
  /// The clock source. Production callers pass <see cref="TimeProvider.System"/>; tests pass a
  /// fake so ticks can be advanced deterministically (AGENTS.md §13).
  /// </param>
  /// <param name="autosaveIntervalMinutes">The positive running-time checkpoint interval, in minutes.</param>
  public StopwatchTimer(
    IStopwatchStore store,
    TimeProvider time,
    int autosaveIntervalMinutes = AppSettings.DefaultAutosaveIntervalMinutes
  )
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(autosaveIntervalMinutes);

    _store = store;
    _time = time;
    _autosaveIntervalMs = (long)TimeSpan.FromMinutes(autosaveIntervalMinutes).TotalMilliseconds;
  }

  /// <summary>Gets a value indicating whether the timer is currently running.</summary>
  public bool IsRunning { get; private set; }

  /// <summary>Gets a value indicating whether the timer is currently paused.</summary>
  public bool IsPaused { get; private set; }

  /// <summary>
  /// Gets the current elapsed time, in milliseconds. Written only by <see cref="Tick"/>; never
  /// zeroed by <see cref="StopAsync"/>.
  /// </summary>
  public long ElapsedMs { get; private set; }

  /// <summary>Gets the current session's laps, newest first.</summary>
  public IReadOnlyList<Lap> Laps => _laps;

  /// <summary>Gets every persisted record, newest first.</summary>
  public IReadOnlyList<StopwatchRecord> Records => _records;

  /// <summary>
  /// Gets the wall-clock time a saved session was paused at, in epoch milliseconds, or
  /// <c>0</c> if no session has been restored.
  /// </summary>
  public long RestoredPausedAtMs { get; private set; }

  /// <summary>Fires at the end of <see cref="Start"/>, when it actually transitions to running.</summary>
  public event Action<long>? OnStart;

  /// <summary>
  /// Fires at the end of <see cref="PauseAsync"/>, only if it was running, after the session
  /// snapshot has been saved.
  /// </summary>
  public event Action<long>? OnPause;

  /// <summary>
  /// Fires inside <see cref="Tick"/>, only at 5, 10, 15… seconds of elapsed time, never while
  /// paused or stopped.
  /// </summary>
  public event Action<long>? OnTick;

  /// <summary>
  /// Fires in <see cref="StopAsync"/>, before the database write, only when
  /// <see cref="ElapsedMs"/> and the session start are both positive.
  /// </summary>
  public event Action<long, long, long>? OnStop;

  /// <summary>Fires whenever <see cref="Records"/> is reloaded from <see cref="IStopwatchStore"/>.</summary>
  public event Action? RecordsChanged;

  /// <summary>
  /// Starts a fresh session, or resumes a paused one. No-op if already running. See AGENTS.md
  /// §8.3.
  /// </summary>
  public void Start()
  {
    if (IsRunning)
    {
      return;
    }

    if (!IsPaused)
    {
      _laps.Clear();
      _lastLapElapsed = 0;
      _lastLapTimestamp = 0;
      RestoredPausedAtMs = 0;
      ElapsedMs = 0;
      _sessionStartMs = NowMs();
    }

    _startTime = NowMs() - ElapsedMs;
    _nextAutosaveElapsedMs = NextAutosaveAfter(ElapsedMs);
    IsRunning = true;
    IsPaused = false;
    OnStart?.Invoke(ElapsedMs);
  }

  /// <summary>
  /// Pauses the running session and persists a snapshot so it can be restored later. No-op if not
  /// running. See AGENTS.md §8.3.
  /// </summary>
  public async Task PauseAsync()
  {
    if (!IsRunning)
    {
      return;
    }

    IsRunning = false;
    IsPaused = true;

    await SaveSnapshotAsync().ConfigureAwait(false);

    OnPause?.Invoke(ElapsedMs);
  }

  /// <summary>
  /// Records a lap as the interval since the previous lap (not a cumulative total). No-op if not
  /// running. See AGENTS.md §8.3.
  /// </summary>
  public void Lap()
  {
    if (!IsRunning)
    {
      return;
    }

    long splitMs = ElapsedMs - _lastLapElapsed;
    Lap newLap = new(
      _laps.Count + 1,
      _lastLapTimestamp != 0 ? _lastLapTimestamp : _sessionStartMs,
      NowMs(),
      splitMs / 60000
    );
    _laps.Insert(0, newLap);
    _lastLapElapsed = ElapsedMs;
    _lastLapTimestamp = newLap.EndTimestamp;
  }

  /// <summary>
  /// Stops the current session (a harmless no-op if idle), appends a final partial lap when one
  /// is owed, and — unless it would duplicate the most recent record — persists the session and
  /// reloads <see cref="Records"/>. See AGENTS.md §8.3.
  /// </summary>
  public async Task StopAsync()
  {
    IsRunning = false;
    IsPaused = false;
    RestoredPausedAtMs = 0;
    await ClearSnapshotAsync().ConfigureAwait(false);
    long endTimestamp = NowMs();

    if (_laps.Count > 0 && ElapsedMs > _lastLapElapsed)
    {
      long splitMs = ElapsedMs - _lastLapElapsed;
      Lap finalLap = new(
        _laps.Count + 1,
        _lastLapTimestamp != 0 ? _lastLapTimestamp : _sessionStartMs,
        endTimestamp,
        splitMs / 60000
      );
      _laps.Insert(0, finalLap);
      _lastLapElapsed = ElapsedMs;
      _lastLapTimestamp = endTimestamp;
    }

    if (ElapsedMs > 0 && _sessionStartMs > 0)
    {
      OnStop?.Invoke(ElapsedMs, _sessionStartMs, endTimestamp);

      bool isDuplicate = Records.Count > 0 && Records[0].StartTimestamp == _sessionStartMs;
      if (!isDuplicate)
      {
        await _store
          .SaveRecordAsync(_sessionStartMs, endTimestamp, ElapsedMs, [.. _laps])
          .ConfigureAwait(false);
        await ReloadRecordsAsync().ConfigureAwait(false);
      }
    }

    _sessionStartMs = 0;
  }

  /// <summary>
  /// Clears every persisted record and reloads the timer-owned records list. The resulting
  /// <see cref="RecordsChanged"/> event lets UI owners refresh their rendered records without
  /// reaching into the store directly.
  /// </summary>
  public async Task ClearRecordsAsync()
  {
    await _store.ClearAllRecordsAsync().ConfigureAwait(false);
    await ReloadRecordsAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Deletes one persisted record and reloads the timer-owned records list. A missing identifier is
  /// harmless; storage failures follow the store's deliberate swallow-and-reload behavior.
  /// </summary>
  /// <param name="id">The primary-key identifier of the record to delete.</param>
  public async Task DeleteRecordAsync(long id)
  {
    await _store.DeleteRecordAsync(id).ConfigureAwait(false);
    await ReloadRecordsAsync().ConfigureAwait(false);
  }

  /// <summary>
  /// Loads the laps saved for one persisted record, newest first. Routes through the timer-owned
  /// store boundary so UI components never call <see cref="IStopwatchStore"/> directly (AGENTS.md
  /// §3.1).
  /// </summary>
  /// <param name="recordId">The record's id, as it appears in <see cref="Records"/>.</param>
  public Task<IReadOnlyList<Lap>> GetRecordLapsAsync(long recordId) =>
    _store.GetLapsAsync(recordId);

  /// <summary>
  /// The tick body: recomputes <see cref="ElapsedMs"/> from the wall clock and raises
  /// <see cref="OnTick"/> at 5-second boundaries. No-op while not running. Called once a second by
  /// the UI-side <see cref="System.Windows.Forms.Timer"/> (owned by <c>StopwatchControl</c>, not
  /// this class). See AGENTS.md §8.2.
  /// </summary>
  public void Tick()
  {
    if (!IsRunning)
    {
      return;
    }

    ElapsedMs = NowMs() - _startTime;
    long totalSeconds = ElapsedMs / 1000;
    if (totalSeconds > 0 && totalSeconds % 5 == 0)
    {
      OnTick?.Invoke(ElapsedMs);
    }
  }

  /// <summary>
  /// Saves the current running session into the single recovery slot when its configured
  /// accumulated-running-time checkpoint is due. This does not pause the stopwatch. Call after
  /// each <see cref="Tick"/> from the UI timer.
  /// </summary>
  public async Task SaveAutosaveIfDueAsync()
  {
    await _snapshotGate.WaitAsync().ConfigureAwait(false);
    try
    {
      if (!IsRunning || ElapsedMs < _nextAutosaveElapsedMs)
      {
        return;
      }

      await SaveSnapshotCoreAsync().ConfigureAwait(false);
      _nextAutosaveElapsedMs = NextAutosaveAfter(ElapsedMs);
    }
    finally
    {
      _snapshotGate.Release();
    }
  }

  /// <summary>
  /// Loads <see cref="Records"/> and, if one exists, restores a saved paused session (frozen
  /// elapsed time, laps, and <see cref="RestoredPausedAtMs"/>) so the UI can show a
  /// <c>Continue</c> button. Call once at startup, after the database connection opens. See
  /// AGENTS.md §9.
  /// </summary>
  public async Task RestoreAsync()
  {
    await ReloadRecordsAsync().ConfigureAwait(false);

    PausedSession? session = await _store.LoadPausedSessionAsync().ConfigureAwait(false);
    if (session is null)
    {
      return;
    }

    _laps.Clear();
    _laps.AddRange(session.Laps);
    ElapsedMs = session.ElapsedTime;
    _sessionStartMs = session.SessionStartTime;
    _lastLapElapsed = session.LastLapElapsed;
    _lastLapTimestamp = session.LastLapTimestamp;
    RestoredPausedAtMs = session.PausedAt;
    IsRunning = false;
    IsPaused = true;
  }

  /// <summary>Releases the synchronization primitive used to serialize recovery-slot writes.</summary>
  public void Dispose() => _snapshotGate.Dispose();

  private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

  private async Task SaveSnapshotAsync()
  {
    await _snapshotGate.WaitAsync().ConfigureAwait(false);
    try
    {
      await SaveSnapshotCoreAsync().ConfigureAwait(false);
    }
    finally
    {
      _snapshotGate.Release();
    }
  }

  private Task SaveSnapshotCoreAsync()
  {
    PausedSession snapshot = new(
      ElapsedMs,
      _sessionStartMs,
      [.. _laps],
      _lastLapElapsed,
      _lastLapTimestamp,
      NowMs()
    );
    return _store.SavePausedSessionAsync(snapshot);
  }

  private async Task ClearSnapshotAsync()
  {
    await _snapshotGate.WaitAsync().ConfigureAwait(false);
    try
    {
      await _store.ClearPausedSessionAsync().ConfigureAwait(false);
    }
    finally
    {
      _snapshotGate.Release();
    }
  }

  private long NextAutosaveAfter(long elapsedMs) =>
    ((elapsedMs / _autosaveIntervalMs) + 1) * _autosaveIntervalMs;

  private async Task ReloadRecordsAsync()
  {
    _records = await _store.GetAllRecordsAsync().ConfigureAwait(false);
    RecordsChanged?.Invoke();
  }
}
