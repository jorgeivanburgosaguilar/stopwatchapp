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
    IReadOnlyList<Lap> savedLaps = await timer.GetRecordLapsAsync(recordId);
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
    Assert.Empty(await timer.GetRecordLapsAsync(recordId));
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
    int lapCountAfterFirstStop = (await timer.GetRecordLapsAsync(recordId)).Count;

    await timer.StopAsync();

    Assert.Equal(lapCountAfterFirstStop, (await timer.GetRecordLapsAsync(recordId)).Count);
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
