using Microsoft.Extensions.Time.Testing;
using StopwatchApp.Models;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Tests for <see cref="StopwatchTimer.ResumeRecordAsync"/> (AGENTS.md §8.3): moving a recent
/// stopped record back into the clock, paused, at its exact stopped time. Driven by
/// <see cref="FakeTimeProvider"/> and <see cref="FakeStopwatchStore"/> — no sleeping (§13).
/// </summary>
public sealed class ResumeRecordTimerTests
{
  [Fact]
  public async Task Resume_RestoresTheExactTimeAndDropsStopsSyntheticFinalLap()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(90));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(37));
    timer.Tick();
    await timer.StopAsync();
    StopwatchRecord record = Assert.Single(timer.Records);
    Assert.Equal(2, (await store.GetLapsAsync(record.Id)).Count); // real lap + Stop's final lap

    bool resumed = await timer.ResumeRecordAsync(record.Id);

    Assert.True(resumed);
    Assert.False(timer.IsRunning);
    Assert.True(timer.IsPaused);
    Assert.Equal(127_000, timer.ElapsedMs);
    Assert.Single(timer.Laps);
    Assert.True(timer.HasActiveLapSplit);
    Assert.Equal(37_000, timer.LapElapsedMs);
    Assert.Equal(2, timer.CurrentLapNumber);
    Assert.Equal(record.EndTimestamp, timer.RestoredPausedAtMs);
    Assert.Empty(timer.Records);
    Assert.NotNull(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task Resume_ThenContinueAndStop_SavesOneRecordWithTheCombinedTimeAndNoDuplicateLaps()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(90));
    timer.Tick();
    timer.Lap();
    time.Advance(TimeSpan.FromSeconds(37));
    timer.Tick();
    await timer.StopAsync();
    long resumedStart = timer.Records[0].StartTimestamp;
    await timer.ResumeRecordAsync(timer.Records[0].Id);

    timer.Start();
    time.Advance(TimeSpan.FromSeconds(3));
    timer.Tick();
    await timer.StopAsync();

    StopwatchRecord record = Assert.Single(timer.Records);
    Assert.Equal(130_000, record.ElapsedMs);
    Assert.Equal(resumedStart, record.StartTimestamp);
    IReadOnlyList<Lap> laps = await store.GetLapsAsync(record.Id);
    Assert.Equal([2L, 1L], laps.Select(lap => lap.Id));
    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task Resume_OfARecordWithNoLaps_RestoresTheTimeWithoutALapClock()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(75));
    timer.Tick();
    await timer.StopAsync();

    bool resumed = await timer.ResumeRecordAsync(timer.Records[0].Id);

    Assert.True(resumed);
    Assert.Equal(75_000, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
    Assert.False(timer.HasActiveLapSplit);
    Assert.True(timer.IsPaused);
  }

  [Fact]
  public async Task Resume_WhenLapWasPressedOnTheLastTick_KeepsEveryLapAndStartsAFreshSplit()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(60));
    timer.Tick();
    timer.Lap();
    await timer.StopAsync(); // nothing left over, so Stop appends no final lap
    StopwatchRecord record = Assert.Single(timer.Records);
    Assert.Equal(1, record.LapCount);

    await timer.ResumeRecordAsync(record.Id);

    Assert.Single(timer.Laps);
    Assert.Equal(0, timer.LapElapsedMs);
    Assert.Equal(2, timer.CurrentLapNumber);
  }

  [Fact]
  public async Task Resume_OfALegacyMinutePrecisionRecord_UsesWholeMinutesAndKeepsAllItsLaps()
  {
    FakeStopwatchStore store = new();
    long id = store.AddLegacyRecord(
      1_000,
      9_000_000,
      42,
      [new Lap(2, 5_000, 9_000_000, 12), new Lap(1, 1_000, 5_000, 30)]
    );
    using StopwatchTimer timer = new(store, new FakeTimeProvider());
    await timer.RestoreAsync();

    bool resumed = await timer.ResumeRecordAsync(id);

    Assert.True(resumed);
    Assert.Equal(42 * 60_000, timer.ElapsedMs);
    Assert.Equal(2, timer.Laps.Count);
    Assert.Equal(0, timer.LapElapsedMs);
    Assert.Equal(3, timer.CurrentLapNumber);
    Assert.Equal(1_000, (await store.LoadPausedSessionAsync())?.SessionStartTime);
  }

  [Fact]
  public async Task Resume_OfARecordOutsideTheNewestFive_IsRejectedWithoutChangingAnything()
  {
    FakeStopwatchStore store = new();
    for (int i = 0; i < 6; i++)
    {
      await store.SaveRecordAsync(i, i + 1_000, 60_000, []);
    }

    using StopwatchTimer timer = new(store, new FakeTimeProvider());
    await timer.RestoreAsync();
    long oldestId = timer.Records[^1].Id;

    bool resumed = await timer.ResumeRecordAsync(oldestId);

    Assert.False(resumed);
    Assert.Equal(6, timer.Records.Count);
    Assert.False(timer.IsPaused);
    Assert.Equal(0, timer.ElapsedMs);
  }

  [Fact]
  public async Task Resume_WhenTheStoreFails_ChangesNothing()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(75));
    timer.Tick();
    await timer.StopAsync();
    store.FailResumes = true;

    bool resumed = await timer.ResumeRecordAsync(timer.Records[0].Id);

    Assert.False(resumed);
    Assert.Single(timer.Records);
    Assert.False(timer.IsPaused);
    Assert.Equal(75_000, timer.ElapsedMs); // Stop never zeroes it, but nothing was replaced
    Assert.Null(await store.LoadPausedSessionAsync());
  }

  [Fact]
  public async Task Resume_WhenTheLapsCannotBeRead_IsRejected()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(75));
    timer.Tick();
    await timer.StopAsync();
    store.FailReads = true;

    bool resumed = await timer.ResumeRecordAsync(timer.Records[0].Id);

    Assert.False(resumed);
    Assert.Single(timer.Records);
  }

  [Fact]
  public async Task Resume_ReplacesARunningSessionAndItsLaps()
  {
    FakeStopwatchStore store = new();
    long id = store.AddLegacyRecord(500, 9_000, 10, []);
    FakeTimeProvider time = new();
    using StopwatchTimer timer = new(store, time);
    await timer.RestoreAsync();
    timer.Start();
    time.Advance(TimeSpan.FromSeconds(30));
    timer.Tick();
    timer.Lap();

    bool resumed = await timer.ResumeRecordAsync(id);

    Assert.True(resumed);
    Assert.False(timer.IsRunning);
    Assert.True(timer.IsPaused);
    Assert.Equal(10 * 60_000, timer.ElapsedMs);
    Assert.Empty(timer.Laps);
  }

  [Fact]
  public async Task Resume_SurvivesARestartAsAPausedSession()
  {
    FakeStopwatchStore store = new();
    FakeTimeProvider time = new();
    using (StopwatchTimer first = new(store, time))
    {
      first.Start();
      time.Advance(TimeSpan.FromSeconds(90));
      first.Tick();
      first.Lap();
      time.Advance(TimeSpan.FromSeconds(37));
      first.Tick();
      await first.StopAsync();
      await first.ResumeRecordAsync(first.Records[0].Id);
    }

    using StopwatchTimer restarted = new(store, time);
    await restarted.RestoreAsync();

    Assert.True(restarted.IsPaused);
    Assert.Equal(127_000, restarted.ElapsedMs);
    Assert.Equal(37_000, restarted.LapElapsedMs);
    Assert.Empty(restarted.Records);
  }
}
