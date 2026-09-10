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
    await store.SaveRecordAsync(0, 60_000, 60_000);
    StopwatchTimer timer = new(store, new FakeTimeProvider());

    await timer.RestoreAsync();

    Assert.Single(timer.Records);
  }

  [Fact]
  public async Task ClearRecordsAsync_ClearsTheStoreReloadsRecordsAndFiresRecordsChanged()
  {
    FakeStopwatchStore store = new();
    await store.SaveRecordAsync(0, 60_000, 60_000);
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
