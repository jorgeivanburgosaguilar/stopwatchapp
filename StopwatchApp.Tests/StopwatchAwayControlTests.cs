using Microsoft.Extensions.Time.Testing;
using StopwatchApp.Controls;
using StopwatchApp.Formatting;
using StopwatchApp.Models;

namespace StopwatchApp.Tests;

public sealed class StopwatchAwayControlTests
{
  [Theory]
  [InlineData(AwayReason.Locked, "the computer was locked")]
  [InlineData(AwayReason.ScreenSaver, "the screen saver started")]
  [InlineData(AwayReason.DisplayOff, "the display turned off")]
  public void FormatAwayNote_NamesTheCauseAndLocalTime(AwayReason reason, string cause)
  {
    long pausedAt = new DateTimeOffset(
      2026,
      11,
      24,
      14,
      32,
      5,
      TimeSpan.Zero
    ).ToUnixTimeMilliseconds();

    string note = StopwatchControl.FormatAwayNote(reason, pausedAt);

    Assert.Equal($"Paused when {cause} at {TimeFormat.FormatTimeOnly(pausedAt)}", note);
  }

  [Fact]
  public Task PauseForAway_RunningSessionPausesSavesSnapshotAndShowsNote() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(30));
      control.Timer.Tick();
      long pausedAt = time.GetUtcNow().ToUnixTimeMilliseconds();

      await control.PauseForAwayAsync(AwayReason.Locked);

      Assert.False(control.Timer.IsRunning);
      Assert.True(control.Timer.IsPaused);
      Assert.Equal(30_000, control.Timer.ElapsedMs);
      Assert.NotNull(await store.LoadPausedSessionAsync());
      Assert.True(control.PendingAwayReturn);
      Label note = NoteLabel(control);
      Assert.True(note.Visible);
      Assert.Equal(StopwatchControl.FormatAwayNote(AwayReason.Locked, pausedAt), note.Text);
    });

  [Fact]
  public Task PauseForAway_ExcludesAwayTimeAfterContinue() =>
    RunOnStaAsync(async () =>
    {
      FakeTimeProvider time = new();
      using StopwatchControl timed = new(new FakeStopwatchStore(), time, 5, 0);
      timed.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      timed.Timer.Tick();
      await timed.PauseForAwayAsync(AwayReason.Locked);

      time.Advance(TimeSpan.FromMinutes(20));
      timed.StartTimer();
      time.Advance(TimeSpan.FromSeconds(5));
      timed.Timer.Tick();

      Assert.Equal(15_000, timed.Timer.ElapsedMs);
      Assert.False(NoteLabel(timed).Visible);
      Assert.False(timed.PendingAwayReturn);
    });

  [Fact]
  public Task PauseForAway_IdleSessionIsUntouched() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      using StopwatchControl control = new(store, new FakeTimeProvider(), 5, 0);
      int changes = 0;
      control.StateChanged += () => changes++;

      await control.PauseForAwayAsync(AwayReason.Locked);

      Assert.False(control.Timer.IsPaused);
      Assert.False(control.PendingAwayReturn);
      Assert.False(NoteLabel(control).Visible);
      Assert.Equal(0, changes);
      Assert.Null(await store.LoadPausedSessionAsync());
    });

  [Fact]
  public Task PauseForAway_ManuallyPausedSessionGetsNoNoteOrReturn() =>
    RunOnStaAsync(async () =>
    {
      FakeTimeProvider time = new();
      using StopwatchControl control = new(new FakeStopwatchStore(), time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      await control.PauseTimerAsync();

      await control.PauseForAwayAsync(AwayReason.DisplayOff);

      Assert.True(control.Timer.IsPaused);
      Assert.False(control.PendingAwayReturn);
      Assert.False(NoteLabel(control).Visible);
    });

  [Fact]
  public Task PauseForAway_FirstReasonWinsWhenSignalsArriveTogether() =>
    RunOnStaAsync(async () =>
    {
      FakeTimeProvider time = new();
      using StopwatchControl control = new(new FakeStopwatchStore(), time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();

      await control.PauseForAwayAsync(AwayReason.Locked);
      await control.PauseForAwayAsync(AwayReason.DisplayOff);

      Assert.Contains("locked", NoteLabel(control).Text, StringComparison.Ordinal);
    });

  [Fact]
  public Task TryConsumeAwayReturn_SucceedsOncePerAwayPause() =>
    RunOnStaAsync(async () =>
    {
      FakeTimeProvider time = new();
      using StopwatchControl control = new(new FakeStopwatchStore(), time, 5, 0);
      Assert.False(control.TryConsumeAwayReturn());
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      await control.PauseForAwayAsync(AwayReason.ScreenSaver);

      Assert.True(control.TryConsumeAwayReturn());
      Assert.False(control.TryConsumeAwayReturn());
      // The note stays until the user decides, even though the window was already brought back.
      Assert.True(NoteLabel(control).Visible);
    });

  [Fact]
  public Task Stop_ClearsTheAwayNote() =>
    RunOnStaAsync(async () =>
    {
      FakeTimeProvider time = new();
      using StopwatchControl control = new(new FakeStopwatchStore(), time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      await control.PauseForAwayAsync(AwayReason.Locked);

      await control.StopTimerAsync();

      Assert.False(NoteLabel(control).Visible);
      Assert.False(control.PendingAwayReturn);
    });

  // The note is the only label that is not a clock readout ("00:00:00", "Lap n · 00:00:00"): its
  // text is empty while hidden, otherwise "Paused when…" or "Resumed from…".
  private static Label NoteLabel(Control root) =>
    Descendants(root)
      .OfType<Label>()
      .Single(label =>
        label.Text.Length == 0
        || label.Text.StartsWith("Paused when", StringComparison.Ordinal)
        || label.Text.StartsWith("Resumed from", StringComparison.Ordinal)
      );

  private static IEnumerable<Control> Descendants(Control parent)
  {
    foreach (Control child in parent.Controls)
    {
      yield return child;
      foreach (Control descendant in Descendants(child))
      {
        yield return descendant;
      }
    }
  }

  private static Task RunOnStaAsync(Func<Task> test)
  {
    TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Thread thread = new(async () =>
    {
      try
      {
        // Fake store tasks complete inline: no live desktop or message pump is required.
        await test();
        completion.SetResult();
      }
      catch (Exception exception)
      {
        completion.SetException(exception);
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
  }
}
