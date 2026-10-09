using Microsoft.Extensions.Time.Testing;
using StopwatchApp.Models;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Transition tests for <see cref="StopwatchTimer"/> against <see cref="FakeStopwatchStore"/> and
/// <see cref="FakeTimeProvider"/> — never <c>Thread.Sleep</c>/<c>Task.Delay</c> (AGENTS.md §13).
/// Covers every case in AGENTS.md §8.3/§12.
/// </summary>
public sealed class StopwatchTimerTests
{
  [Fact]
  public void InitialState_IsIdleWithZeroElapsedAndNoLaps()
  {
    StopwatchTimer timer = new(new FakeStopwatchStore(), new FakeTimeProvider());

    Assert.False(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(0, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
  }

  [Fact]
  public void Start_FromIdle_TransitionsToRunningAndFiresOnStart()
  {
    StopwatchTimer timer = new(new FakeStopwatchStore(), new FakeTimeProvider());
    long? firedWith = null;
    timer.OnStart += ms => firedWith = ms;

    timer.Start();

    Assert.True(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(0, firedWith);
  }

  [Fact]
  public void Start_WhenAlreadyRunning_IsNoOp()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    long elapsedBeforeSecondStart = timer.ElapsedMs;
    int startCount = 0;
    timer.OnStart += _ => startCount++;

    timer.Start();

    Assert.Equal(0, startCount);
    Assert.Equal(elapsedBeforeSecondStart, timer.ElapsedMs);
  }

  [Fact]
  public void Lap_WhenNotRunning_IsNoOp()
  {
    StopwatchTimer timer = new(new FakeStopwatchStore(), new FakeTimeProvider());

    timer.Lap();

    Assert.Empty(timer.Laps);
  }

  [Fact]
  public async Task PauseAsync_WhenNotRunning_IsNoOp()
  {
    FakeStopwatchStore store = new();
    StopwatchTimer timer = new(store, new FakeTimeProvider());

    await timer.PauseAsync();

    Assert.False(timer.IsPaused);
    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public void Lap_RecordsSplitSinceLastLap_NotCumulativeTotal()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    time.Advance(TimeSpan.FromSeconds(61));
    timer.Tick();
    timer.Lap();

    time.Advance(TimeSpan.FromSeconds(121));
    timer.Tick();
    timer.Lap();

    Assert.Equal(2, timer.Laps.Count);
    Lap secondLap = timer.Laps[0]; // newest first
    Lap firstLap = timer.Laps[1];
    Assert.Equal(1, firstLap.Id);
    Assert.Equal(1, firstLap.ElapsedMinutes);
    Assert.Equal(2, secondLap.Id);
    Assert.Equal(2, secondLap.ElapsedMinutes); // split since the first lap, not the 3-minute cumulative total
  }

  [Fact]
  public async Task Start_AfterStop_ClearsPreviousLaps()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();
    timer.Lap();
    await timer.StopAsync();

    timer.Start();

    Assert.Empty(timer.Laps);
  }

  [Fact]
  public async Task Start_AfterPause_ResumesAndPreservesLapsAndElapsed()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();
    timer.Lap();
    await timer.PauseAsync();
    long elapsedAtPause = timer.ElapsedMs;
    int lapsAtPause = timer.Laps.Count;

    timer.Start();

    Assert.True(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(lapsAtPause, timer.Laps.Count);
    Assert.Equal(elapsedAtPause, timer.ElapsedMs); // resumes from the frozen value, no jump
  }

  [Fact]
  public async Task StopAsync_AfterLap_AppendsFinalPartialLapOnceNotTwice()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(20));
    timer.Tick();

    await timer.StopAsync();
    int lapCountAfterFirstStop = timer.Laps.Count;
    await timer.StopAsync();

    Assert.Equal(2, lapCountAfterFirstStop); // the manual lap + the final partial lap
    Assert.Equal(lapCountAfterFirstStop, timer.Laps.Count); // the second Stop appended nothing
  }

  [Fact]
  public async Task StopAsync_CalledTwiceWithoutRestart_YieldsExactlyOneRecord()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();

    await timer.StopAsync();
    await timer.StopAsync();

    Assert.Single(await store.GetAllRecordsAsync());
  }

  [Fact]
  public async Task StopAsync_PersistsTheSessionsLapsNewestFirstIncludingTheFinalPartialLap()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(20));
    timer.Tick();

    await timer.StopAsync();

    long recordId = (await store.GetAllRecordsAsync())[0].Id;
    IReadOnlyList<Lap>? savedLaps = await timer.GetRecordLapsAsync(recordId);
    Assert.NotNull(savedLaps);
    Assert.Equal([2, 1], savedLaps.Select(lap => lap.Id));
  }

  [Fact]
  public async Task StopAsync_WithNoLaps_PersistsNoLaps()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();

    await timer.StopAsync();

    long recordId = (await store.GetAllRecordsAsync())[0].Id;
    Assert.Empty(await store.GetLapsAsync(recordId));
  }

  [Fact]
  public async Task StopAsync_CalledTwiceWithoutRestart_PersistsLapsOnce()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(20));
    timer.Tick();

    await timer.StopAsync();
    long recordId = (await store.GetAllRecordsAsync())[0].Id;
    int lapCountAfterFirstStop = (await store.GetLapsAsync(recordId)).Count;

    await timer.StopAsync();

    Assert.Equal(lapCountAfterFirstStop, (await store.GetLapsAsync(recordId)).Count);
  }

  [Fact]
  public async Task StopAsync_BeforeFirstTick_SavesNoRecordAndDoesNotFireOnStop()
  {
    FakeStopwatchStore store = new();
    StopwatchTimer timer = new(store, new FakeTimeProvider());
    bool onStopFired = false;
    timer.OnStop += (_, _, _) => onStopFired = true;
    timer.Start();

    await timer.StopAsync();

    Assert.False(onStopFired);
    Assert.Empty(await store.GetAllRecordsAsync());
  }

  [Fact]
  public async Task StopAsync_Success_SavesOneRecordAndLeavesNoSnapshot()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    await timer.PauseAsync();
    Assert.NotNull(await store.LoadPausedSessionAsync());

    await timer.StopAsync();

    Assert.Single(await store.GetAllRecordsAsync());
    Assert.Null(await store.LoadPausedSessionAsync());
    Assert.False(timer.IsPaused);
  }

  [Fact]
  public async Task StopAsync_WhenTheSaveFails_KeepsTheSessionPausedAndRecoverable()
  {
    FakeStopwatchStore store = new() { FailRecordSaves = true };
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();

    await timer.StopAsync();

    Assert.Empty(await store.GetAllRecordsAsync());
    Assert.True(timer.IsPaused);
    Assert.False(timer.IsRunning);
    Assert.Equal(40_000, timer.ElapsedMs);
    PausedSession? snapshot = await store.LoadPausedSessionAsync();
    Assert.NotNull(snapshot);
    Assert.Equal(40_000, snapshot.ElapsedTime);

    using StopwatchTimer restarted = new(store, time);
    await restarted.RestoreAsync();
    Assert.True(restarted.IsPaused);
    Assert.Equal(40_000, restarted.ElapsedMs);
  }

  [Fact]
  public async Task StopAsync_RetriedAfterAFailedSave_SavesOneRecordWithoutDuplicatingTheFinalLap()
  {
    FakeStopwatchStore store = new() { FailRecordSaves = true };
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();
    await timer.StopAsync();

    store.FailRecordSaves = false;
    await timer.StopAsync();

    StopwatchRecord record = Assert.Single(await store.GetAllRecordsAsync());
    Assert.Equal([2, 1], (await store.GetLapsAsync(record.Id)).Select(lap => lap.Id));
    Assert.Null(await store.LoadPausedSessionAsync());
    Assert.False(timer.IsPaused);
    Assert.Single(timer.Records);
  }

  [Fact]
  public async Task RecordsReload_WhenTheReadFails_KeepsTheLastKnownRecords()
  {
    FakeStopwatchStore store = new();
    await store.SaveRecordAsync(0, 60_000, 60_000, []);
    using StopwatchTimer timer = new(store, new FakeTimeProvider());
    await timer.RestoreAsync();
    Assert.Single(timer.Records);

    store.FailReads = true;
    await timer.ClearRecordsAsync();

    Assert.Single(timer.Records);
    Assert.Null(await timer.GetRecordLapsAsync(1));
  }

  [Fact]
  public async Task RestoreAsync_AfterPauseThenStop_ShowsStartNotContinue()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer original = new(store, time);
    original.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    original.Tick();
    await original.PauseAsync();
    await original.StopAsync();

    StopwatchTimer restored = new(store, time);
    await restored.RestoreAsync();

    Assert.False(restored.IsPaused);
    Assert.Equal(0, restored.RestoredPausedAtMs);
  }

  [Fact]
  public async Task RestoreAsync_AfterPauseOnly_RestoresFrozenElapsedLapsAndPausedState()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer original = new(store, time);
    original.Start();
    time.Advance(TimeSpan.FromSeconds(10));
    original.Tick();
    original.Lap();
    await original.PauseAsync();

    StopwatchTimer restored = new(store, time);
    await restored.RestoreAsync();

    Assert.True(restored.IsPaused);
    Assert.False(restored.IsRunning);
    Assert.Equal(original.ElapsedMs, restored.ElapsedMs);
    Assert.Equal(original.Laps.Count, restored.Laps.Count);
    Assert.True(restored.RestoredPausedAtMs > 0);
  }

  [Fact]
  public void Tick_FiresOnTickOnlyAtFiveSecondBoundaries()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    List<long> ticks = [];
    timer.OnTick += ms => ticks.Add(ms);
    timer.Start();

    for (int i = 0; i < 12; i++)
    {
      time.Advance(TimeSpan.FromSeconds(1));
      timer.Tick();
    }

    Assert.Equal(2, ticks.Count); // fires at 5s and 10s within a 12s run, not at 12s
  }

  [Fact]
  public async Task SaveAutosaveIfDueAsync_AtRunningTimeMilestone_PersistsWithoutPausing()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();

    await timer.SaveAutosaveIfDueAsync();

    PausedSession snapshot = Assert.IsType<PausedSession>(await store.LoadPausedSessionAsync());
    Assert.True(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(60_000, snapshot.ElapsedTime);
    Assert.Single(snapshot.Laps);
  }

  [Fact]
  public async Task SaveAutosaveIfDueAsync_UsesNextRunningTimeMilestoneAfterResume()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    await timer.PauseAsync();
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(29));
    timer.Tick();

    await timer.SaveAutosaveIfDueAsync();

    PausedSession beforeMilestone = Assert.IsType<PausedSession>(
      await store.LoadPausedSessionAsync()
    );
    Assert.Equal(30_000, beforeMilestone.ElapsedTime);

    time.Advance(TimeSpan.FromSeconds(1));
    timer.Tick();
    await timer.SaveAutosaveIfDueAsync();

    PausedSession atMilestone = Assert.IsType<PausedSession>(await store.LoadPausedSessionAsync());
    Assert.Equal(60_000, atMilestone.ElapsedTime);
  }

  [Fact]
  public async Task RestoreAsync_AfterAutosave_RestoresPausedSnapshot()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer original = new(store, time, autosaveIntervalMinutes: 1);
    original.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    original.Tick();
    original.Lap();
    await original.SaveAutosaveIfDueAsync();

    StopwatchTimer restored = new(store, time, autosaveIntervalMinutes: 1);
    await restored.RestoreAsync();

    Assert.True(restored.IsPaused);
    Assert.False(restored.IsRunning);
    Assert.Equal(60_000, restored.ElapsedMs);
    Assert.Single(restored.Laps);
  }

  [Fact]
  public async Task StopAsync_AfterAutosave_ClearsSavedSnapshot()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    timer.Tick();
    await timer.SaveAutosaveIfDueAsync();

    await timer.StopAsync();

    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task StopAsync_WhileAutosaveIsInFlight_LeavesNoSavedSnapshot()
  {
    TaskCompletionSource saveStarted = new();
    TaskCompletionSource releaseSave = new();
    FakeStopwatchStore store = new()
    {
      PausedSessionSaveStarted = saveStarted,
      PausedSessionSaveGate = releaseSave,
    };
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    timer.Tick();

    Task autosave = timer.SaveAutosaveIfDueAsync();
    await saveStarted.Task;
    Task stop = timer.StopAsync();
    releaseSave.SetResult();
    await autosave;
    await stop;

    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Theory]
  [InlineData(false, false, false)]
  [InlineData(false, false, true)]
  [InlineData(true, false, false)]
  [InlineData(true, false, true)]
  [InlineData(true, true, false)]
  [InlineData(true, true, true)]
  public async Task ResetAsync_ActiveSession_DiscardsSessionAndPreservesSavedHistory(
    bool paused,
    bool restored,
    bool hasLaps
  )
  {
    FakeStopwatchStore store = new();
    Lap savedLap = new(1, 1, 60_001, 1);
    long savedId = await store.SaveRecordAsync(1, 60_001, 60_000, [savedLap]);
    FakeTimeProvider time = new();
    using StopwatchTimer original = new(store, time, autosaveIntervalMinutes: 1);
    await original.RestoreAsync();
    original.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    original.Tick();
    if (hasLaps)
    {
      original.Lap();
      time.Advance(TimeSpan.FromSeconds(10));
      original.Tick();
    }

    await original.SaveAutosaveIfDueAsync();
    if (paused)
    {
      await original.PauseAsync();
    }

    using StopwatchTimer recovery = new(store, time);
    StopwatchTimer timer = original;
    if (restored)
    {
      await recovery.RestoreAsync();
      timer = recovery;
      Assert.True(timer.RestoredPausedAtMs > 0);
    }

    IReadOnlyList<StopwatchRecord> recordsBefore = timer.Records;
    bool stopped = false;
    bool recordsChanged = false;
    timer.OnStop += (_, _, _) => stopped = true;
    timer.RecordsChanged += () => recordsChanged = true;

    await timer.ResetAsync();
    await timer.ResetAsync();
    time.Advance(TimeSpan.FromMinutes(5));
    timer.Tick();
    await timer.SaveAutosaveIfDueAsync();
    await timer.StopAsync();

    Assert.False(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(0, timer.ElapsedMs);
    Assert.Equal(0, timer.LapElapsedMs);
    Assert.Equal(0, timer.SplitElapsedMs);
    Assert.Equal(0, timer.RestoredPausedAtMs);
    Assert.False(timer.HasActiveLapSplit);
    Assert.Equal(1, timer.CurrentLapNumber);
    Assert.Empty(timer.Laps);
    Assert.False(stopped);
    Assert.False(recordsChanged);
    Assert.Same(recordsBefore, timer.Records);
    Assert.Equal(savedId, Assert.Single(await store.GetAllRecordsAsync()).Id);
    Assert.Equal(savedLap, Assert.Single(await store.GetLapsAsync(savedId)));
    Assert.Null(await store.LoadPausedSessionAsync());

    using StopwatchTimer restarted = new(store, time);
    await restarted.RestoreAsync();
    Assert.False(restarted.IsPaused);
    Assert.False(restarted.IsRunning);
    Assert.Equal(0, restarted.ElapsedMs);
    Assert.Empty(restarted.Laps);
    Assert.Single(restarted.Records);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task ResetAsync_IdleOrBeforeFirstTick_IsHarmless(bool started)
  {
    FakeStopwatchStore store = new();
    using StopwatchTimer timer = new(store, new FakeTimeProvider());
    if (started)
    {
      timer.Start();
      timer.Lap();
    }

    await timer.ResetAsync();
    await timer.ResetAsync();

    Assert.False(timer.IsRunning);
    Assert.False(timer.IsPaused);
    Assert.Equal(0, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
    Assert.Empty(await store.GetAllRecordsAsync());
    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task ResetAsync_AfterStop_ClearsFinishedDisplayAndLapsButKeepsRecord()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    timer.Tick();
    timer.Lap();
    await timer.StopAsync();

    await timer.ResetAsync();

    Assert.Equal(0, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
    Assert.Single(timer.Records);
    Assert.Single(await store.GetAllRecordsAsync());
  }

  [Fact]
  public async Task Start_AfterReset_UsesFreshTimingLapAnchorsAndAutosaveSchedule()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(120));
    timer.Tick();
    timer.Lap();
    await timer.SaveAutosaveIfDueAsync();
    await timer.ResetAsync();
    time.Advance(TimeSpan.FromHours(1));
    long freshStart = time.GetUtcNow().ToUnixTimeMilliseconds();

    timer.Start();
    time.Advance(TimeSpan.FromSeconds(59));
    timer.Tick();
    await timer.SaveAutosaveIfDueAsync();
    Assert.Equal(59_000, timer.ElapsedMs);
    Assert.Null(await store.LoadPausedSessionAsync());

    time.Advance(TimeSpan.FromSeconds(1));
    timer.Tick();
    timer.Lap();
    await timer.SaveAutosaveIfDueAsync();
    PausedSession snapshot = Assert.IsType<PausedSession>(await store.LoadPausedSessionAsync());
    Lap lap = Assert.Single(timer.Laps);
    Assert.Equal(1, lap.Id);
    Assert.Equal(freshStart, lap.StartTimestamp);
    Assert.Equal(1, lap.ElapsedMinutes);
    Assert.Equal(60_000, snapshot.ElapsedTime);
    Assert.Equal(freshStart, snapshot.SessionStartTime);

    await timer.StopAsync();
    StopwatchRecord record = Assert.Single(timer.Records);
    Assert.Equal(freshStart, record.StartTimestamp);
    Assert.Equal(1, record.ElapsedMinutes);
    Assert.Equal(1, record.LapCount);
  }

  [Fact]
  public async Task ResetAsync_WhileAutosaveIsInFlight_DeletesItAndPreventsQueuedAutosave()
  {
    TaskCompletionSource saveStarted = new();
    TaskCompletionSource releaseSave = new();
    FakeStopwatchStore store = new()
    {
      PausedSessionSaveStarted = saveStarted,
      PausedSessionSaveGate = releaseSave,
    };
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time, autosaveIntervalMinutes: 1);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    timer.Tick();
    timer.Lap();

    Task autosave = timer.SaveAutosaveIfDueAsync();
    await saveStarted.Task;
    Task queuedAutosave = timer.SaveAutosaveIfDueAsync();
    Task reset = timer.ResetAsync();
    Assert.False(timer.IsRunning);
    Assert.False(reset.IsCompleted);
    releaseSave.SetResult();
    await autosave;
    await queuedAutosave;
    await reset;
    await timer.SaveAutosaveIfDueAsync();

    Assert.Equal(0, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
    Assert.Empty(await store.GetAllRecordsAsync());
    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task Tick_WhilePaused_IsNoOp()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    await timer.PauseAsync();
    long frozenElapsed = timer.ElapsedMs;
    bool tickFired = false;
    timer.OnTick += _ => tickFired = true;

    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();

    Assert.Equal(frozenElapsed, timer.ElapsedMs);
    Assert.False(tickFired);
  }

  [Fact]
  public async Task StopAsync_WithSavedRecord_FiresRecordsChanged()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    int recordsChangedCount = 0;
    timer.RecordsChanged += () => recordsChangedCount++;
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();

    await timer.StopAsync();

    Assert.Equal(1, recordsChangedCount);
    Assert.Single(timer.Records);
  }

  [Fact]
  public async Task RestoreAsync_LoadsExistingRecords()
  {
    FakeStopwatchStore store = new();
    await store.SaveRecordAsync(0, 60_000, 60_000, []);
    StopwatchTimer timer = new(store, new FakeTimeProvider());

    await timer.RestoreAsync();

    Assert.Single(timer.Records);
  }

  [Fact]
  public async Task ClearRecordsAsync_ClearsTheStoreReloadsRecordsAndFiresRecordsChanged()
  {
    FakeStopwatchStore store = new();
    await store.SaveRecordAsync(0, 60_000, 60_000, []);
    StopwatchTimer timer = new(store, new FakeTimeProvider());
    int recordsChangedCount = 0;
    timer.RecordsChanged += () => recordsChangedCount++;
    await timer.RestoreAsync();

    await timer.ClearRecordsAsync();

    Assert.Empty(await store.GetAllRecordsAsync());
    Assert.Empty(timer.Records);
    Assert.Equal(2, recordsChangedCount);
  }

  [Fact]
  public async Task DeleteRecordAsync_ReloadsRecordsAndFiresRecordsChanged()
  {
    FakeStopwatchStore store = new();
    long firstId = await store.SaveRecordAsync(0, 60_000, 60_000, []);
    long secondId = await store.SaveRecordAsync(1, 120_000, 60_000, []);
    StopwatchTimer timer = new(store, new FakeTimeProvider());
    int recordsChangedCount = 0;
    timer.RecordsChanged += () => recordsChangedCount++;
    await timer.RestoreAsync();

    await timer.DeleteRecordAsync(secondId);

    StopwatchRecord remaining = Assert.Single(timer.Records);
    Assert.Equal(firstId, remaining.Id);
    Assert.Equal(2, recordsChangedCount);
  }

  [Fact]
  public async Task StopAsync_SessionUnderOneMinute_StoresZeroElapsedMinutes()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();

    await timer.StopAsync();

    StopwatchRecord record = Assert.Single(await store.GetAllRecordsAsync());
    Assert.Equal(0, record.ElapsedMinutes);
  }

  [Fact]
  public void LapElapsedMs_BeforeFirstLap_TracksTotalElapsed()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    time.Advance(TimeSpan.FromSeconds(7));
    timer.Tick();

    Assert.Equal(7_000, timer.LapElapsedMs);
    Assert.Equal(timer.ElapsedMs, timer.LapElapsedMs); // no lap yet, so it tracks total elapsed
  }

  [Fact]
  public void LapElapsedMs_ResetsOnEachLapAndTracksIndependentlyOfElapsedMs()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(61));
    timer.Tick();
    timer.Lap();

    Assert.Equal(0, timer.LapElapsedMs);

    time.Advance(TimeSpan.FromSeconds(4));
    timer.Tick();

    Assert.Equal(4_000, timer.LapElapsedMs);
    Assert.Equal(65_000, timer.ElapsedMs); // total keeps counting through the lap boundary
  }

  [Fact]
  public async Task LapElapsedMs_FreezesAcrossPauseAndExcludesPausedTimeAfterResume()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(10));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(3));
    timer.Tick();

    await timer.PauseAsync();
    long lapElapsedAtPause = timer.LapElapsedMs;

    time.Advance(TimeSpan.FromSeconds(30)); // time away while paused
    Assert.Equal(lapElapsedAtPause, timer.LapElapsedMs); // frozen, no drift while paused

    timer.Start();

    Assert.Equal(lapElapsedAtPause, timer.LapElapsedMs); // resumed with no paused time counted
  }

  [Fact]
  public async Task HasActiveLapSplit_TracksSessionLifecycle()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);

    Assert.False(timer.HasActiveLapSplit); // idle

    timer.Start();
    Assert.False(timer.HasActiveLapSplit); // running, no laps yet

    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    timer.Lap();
    Assert.True(timer.HasActiveLapSplit); // running with a lap recorded

    await timer.PauseAsync();
    Assert.True(timer.HasActiveLapSplit); // paused with a lap recorded

    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    await timer.StopAsync();
    Assert.False(timer.HasActiveLapSplit); // stopped
  }

  [Fact]
  public void CurrentLapNumber_IsOneMoreThanLapsRecorded()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    Assert.Equal(1, timer.CurrentLapNumber); // no laps yet: currently timing lap 1

    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    timer.Lap();
    Assert.Equal(2, timer.CurrentLapNumber); // 1 lap recorded: currently timing lap 2

    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    timer.Lap();
    Assert.Equal(3, timer.CurrentLapNumber);
  }

  [Fact]
  public async Task RestoreAsync_AfterPauseWithLap_RestoresFrozenLapSplitAndActiveFlag()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer original = new(store, time);
    original.Start();
    time.Advance(TimeSpan.FromSeconds(20));
    original.Tick();
    original.Lap();
    time.Advance(TimeSpan.FromSeconds(6));
    original.Tick();
    await original.PauseAsync();
    long lapElapsedAtPause = original.LapElapsedMs;
    int lapNumberAtPause = original.CurrentLapNumber;

    StopwatchTimer restored = new(store, time);
    await restored.RestoreAsync();

    Assert.Equal(lapElapsedAtPause, restored.LapElapsedMs);
    Assert.True(restored.HasActiveLapSplit);
    Assert.Equal(lapNumberAtPause, restored.CurrentLapNumber);
  }

  [Fact]
  public void SplitElapsedMs_BeforeFirstLap_EqualsElapsedMs()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    time.Advance(TimeSpan.FromSeconds(7));
    timer.Tick();

    Assert.Equal(timer.ElapsedMs, timer.SplitElapsedMs); // no lap yet: split tracks the whole session
  }

  [Fact]
  public void SplitElapsedMs_AfterLap_ResetsToZeroAndDrivesTheCompactTrayLayoutBackToMinutes()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    time.Advance(TimeSpan.FromMinutes(10));
    timer.Tick();
    timer.Lap();

    Assert.Equal(0, timer.SplitElapsedMs);
    (_, _, TrayIconService.TrayIconLayout layoutAtLap) = TrayIconService.GetDisplayValues(
      timer.SplitElapsedMs
    );
    Assert.Equal(TrayIconService.TrayIconLayout.LargeMinutes, layoutAtLap); // tray icon jumps back to 00

    time.Advance(TimeSpan.FromSeconds(65));
    timer.Tick();

    Assert.Equal(65_000, timer.SplitElapsedMs);
    Assert.Equal(665_000, timer.ElapsedMs); // total elapsed keeps counting through the lap
  }

  [Fact]
  public void SplitElapsedMs_AfterLapPastOneHour_DrivesTheCompactTrayLayoutBackToMinutes()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();

    time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5));
    timer.Tick();
    timer.Lap();

    (_, _, TrayIconService.TrayIconLayout layoutAtLap) = TrayIconService.GetDisplayValues(
      timer.SplitElapsedMs
    );
    Assert.Equal(TrayIconService.TrayIconLayout.LargeMinutes, layoutAtLap); // not HourMinute anymore
  }

  [Fact]
  public async Task SplitElapsedMs_FreezesOnPauseAndSurvivesRestart()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer original = new(store, time);
    original.Start();
    time.Advance(TimeSpan.FromMinutes(10));
    original.Tick();
    original.Lap();
    time.Advance(TimeSpan.FromSeconds(3));
    original.Tick();

    await original.PauseAsync();
    long splitAtPause = original.SplitElapsedMs;

    time.Advance(TimeSpan.FromSeconds(30)); // time away while paused
    Assert.Equal(splitAtPause, original.SplitElapsedMs); // frozen, no drift while paused

    StopwatchTimer restored = new(store, time);
    await restored.RestoreAsync();

    Assert.Equal(splitAtPause, restored.SplitElapsedMs);
  }

  [Fact]
  public async Task SplitElapsedMs_AfterStop_FallsBackToSessionTotal()
  {
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(new FakeStopwatchStore(), time);
    timer.Start();
    time.Advance(TimeSpan.FromMinutes(10));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();

    await timer.StopAsync();

    Assert.Equal(timer.ElapsedMs, timer.SplitElapsedMs); // no active split once stopped
  }

  [Fact]
  public async Task StopAsync_AfterFreshStartFollowingPreviousStop_SavesASecondRecord()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    await timer.StopAsync();

    time.Advance(TimeSpan.FromSeconds(100));
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(5));
    timer.Tick();
    await timer.StopAsync();

    Assert.Equal(2, (await store.GetAllRecordsAsync()).Count);
  }
}
