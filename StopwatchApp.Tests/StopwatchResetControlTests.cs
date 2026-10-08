using Microsoft.Extensions.Time.Testing;
using StopwatchApp.Controls;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

public sealed class StopwatchResetControlTests
{
  [Theory]
  [InlineData(false, false, 0)]
  [InlineData(true, false, 0)]
  [InlineData(true, true, 0)]
  [InlineData(true, false, 5)]
  public Task Reset_AlwaysConfirmsAndDiscards(bool started, bool paused, int stopThreshold) =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, stopThreshold);
      if (started)
      {
        control.StartTimer();
        time.Advance(TimeSpan.FromSeconds(10));
        control.Timer.Tick();
        control.AddLap();
        if (paused)
        {
          await control.PauseTimerAsync();
        }
      }

      int confirmations = 0;
      control.ConfirmReset = () =>
      {
        confirmations++;
        Assert.False(control.Timer.IsRunning);
        Assert.Equal(started, control.Timer.IsPaused);
        return true;
      };
      int refreshes = 0;
      control.StateChanged += () => refreshes++;

      await control.ResetTimerAsync();

      Assert.Equal(1, confirmations);
      Assert.True(refreshes > 0);
      Assert.False(control.Timer.IsRunning);
      Assert.False(control.Timer.IsPaused);
      Assert.Equal(0, control.Timer.ElapsedMs);
      Assert.Empty(control.Timer.Laps);
      Assert.Empty(await store.GetAllRecordsAsync());
      Assert.Null(await store.LoadPausedSessionAsync());
      Assert.Equal("00:00:00", Descendants(control).OfType<Label>().First().Text);
      string[] expectedButtons = ["Start", "Stop", "Reset"];
      Assert.Equal(
        expectedButtons,
        Descendants(control)
          .OfType<Button>()
          .Where(button => button.Visible)
          .Select(button => button.Text)
      );
    });

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public Task Reset_CancelPreservesSessionAndExcludesDialogTime(bool paused) =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      control.AddLap();
      if (paused)
      {
        await control.PauseTimerAsync();
      }
      Lap lap = Assert.Single(control.Timer.Laps);
      control.ConfirmReset = () =>
      {
        Assert.True(control.Timer.IsPaused);
        time.Advance(TimeSpan.FromHours(1));
        control.Timer.Tick();
        Assert.Equal(10_000, control.Timer.ElapsedMs);
        return false;
      };

      await control.ResetTimerAsync();

      Assert.Equal(!paused, control.Timer.IsRunning);
      Assert.Equal(paused, control.Timer.IsPaused);
      Assert.Equal(lap, Assert.Single(control.Timer.Laps));
      Assert.NotNull(await store.LoadPausedSessionAsync());
      Assert.Empty(await store.GetAllRecordsAsync());
      time.Advance(TimeSpan.FromSeconds(2));
      control.Timer.Tick();
      Assert.Equal(paused ? 10_000 : 12_000, control.Timer.ElapsedMs);
    });

  [Fact]
  public Task Reset_DefaultConfirmationCancels() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      control.AddLap();

      await control.ResetTimerAsync();

      Assert.True(control.Timer.IsRunning);
      Assert.Equal(10_000, control.Timer.ElapsedMs);
      Assert.Single(control.Timer.Laps);
      Assert.NotNull(await store.LoadPausedSessionAsync());
    });

  [Fact]
  public Task Reset_ConflictingAndDuplicateActionsDuringConfirmationAreIgnored() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      control.AddLap();
      int confirmations = 0;
      control.ConfirmReset = () =>
      {
        confirmations++;
        control.StartTimer();
        control.AddLap();
        Assert.True(control.PauseTimerAsync().IsCompletedSuccessfully);
        Assert.True(control.StopTimerAsync().IsCompletedSuccessfully);
        Assert.True(control.ResetTimerAsync().IsCompletedSuccessfully);
        Assert.True(control.Timer.IsPaused);
        Assert.Single(control.Timer.Laps);
        Assert.Equal(10_000, control.Timer.ElapsedMs);
        return false;
      };

      await control.ResetTimerAsync();

      Assert.Equal(1, confirmations);
      Assert.True(control.Timer.IsRunning);
      Assert.Empty(await store.GetAllRecordsAsync());
      // The guard is released after cancellation, so ordinary actions work again.
      control.AddLap();
      Assert.Equal(2, control.Timer.Laps.Count);
    });

  [Fact]
  public Task Reset_RestoredSessionClearsLapClockAndRecoveryNote() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      await store.SavePausedSessionAsync(
        new PausedSession(10_000, 1, [new Lap(1, 1, 5_001, 0)], 5_000, 5_001, 10_001)
      );
      using StopwatchControl control = new(store, new FakeTimeProvider(), 5, 0);
      await control.RestoreAsync();
      Label[] labels = Descendants(control).OfType<Label>().ToArray();
      Assert.True(labels[1].Visible);
      Assert.True(labels[2].Visible);
      control.ConfirmReset = () => true;

      await control.ResetTimerAsync();

      Assert.Equal("00:00:00", labels[0].Text);
      Assert.False(labels[1].Visible);
      Assert.False(labels[2].Visible);
      Assert.Equal(0, control.Timer.RestoredPausedAtMs);
      Assert.Null(await store.LoadPausedSessionAsync());
    });

  [Fact]
  public Task Reset_ButtonFollowsStopAndUsesOrangeInBothThemes() =>
    RunOnStaAsync(() =>
    {
      using StopwatchControl control = new(new FakeStopwatchStore(), new FakeTimeProvider(), 5, 0);
      bool[] themes = [false, true];
      foreach (bool dark in themes)
      {
        control.DarkMode = dark;
        Button[] buttons = Descendants(control).OfType<Button>().ToArray();
        Button reset = Assert.Single(buttons, button => button.Text == "Reset");
        Assert.Equal("Stop", buttons[Array.IndexOf(buttons, reset) - 1].Text);
        Assert.Equal(Color.FromArgb(0xC2, 0x41, 0x0C).ToArgb(), reset.BackColor.ToArgb());
        Assert.Equal(Color.White.ToArgb(), reset.ForeColor.ToArgb());
        Assert.Equal(
          Palette.ResetButton.Hover.ToArgb(),
          reset.FlatAppearance.MouseOverBackColor.ToArgb()
        );
        Assert.Equal(
          Palette.ResetButton.Pressed.ToArgb(),
          reset.FlatAppearance.MouseDownBackColor.ToArgb()
        );
      }
      Assert.Equal("Reset Stopwatch", ResetConfirmationDialog.Title);
      Assert.Equal(
        "Reset the clock to zero and discard this session, all its laps, and its recovery snapshot? Previously saved records will be kept.",
        ResetConfirmationDialog.Message
      );
      return Task.CompletedTask;
    });

  [Theory]
  [InlineData(false, false)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(true, true)]
  public Task Stop_SharedActionGuardPreservesConfirmationBehavior(bool confirm, bool paused) =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 5);
      control.StartTimer();
      time.Advance(TimeSpan.FromMinutes(6));
      control.Timer.Tick();
      if (paused)
      {
        await control.PauseTimerAsync();
      }
      int stopConfirmations = 0;
      int resetConfirmations = 0;
      control.ConfirmReset = () =>
      {
        resetConfirmations++;
        return true;
      };
      control.ConfirmStop = () =>
      {
        stopConfirmations++;
        Assert.True(control.Timer.IsPaused);
        control.StartTimer();
        Assert.True(control.ResetTimerAsync().IsCompletedSuccessfully);
        Assert.True(control.StopTimerAsync().IsCompletedSuccessfully);
        Assert.True(control.Timer.IsPaused);
        time.Advance(TimeSpan.FromHours(1));
        return confirm;
      };

      await control.StopTimerAsync();

      Assert.Equal(1, stopConfirmations);
      Assert.Equal(0, resetConfirmations);
      Assert.Equal(!confirm && !paused, control.Timer.IsRunning);
      Assert.Equal(!confirm && paused, control.Timer.IsPaused);
      Assert.Equal(360_000, control.Timer.ElapsedMs);
      IReadOnlyList<StopwatchRecord> records = await store.GetAllRecordsAsync();
      Assert.Equal(confirm ? 1 : 0, records.Count);
      if (confirm)
      {
        Assert.Null(await store.LoadPausedSessionAsync());
        await control.StopTimerAsync();
        Assert.Single(await store.GetAllRecordsAsync());
      }
      else
      {
        Assert.NotNull(await store.LoadPausedSessionAsync());
        time.Advance(TimeSpan.FromSeconds(2));
        control.Timer.Tick();
        Assert.Equal(paused ? 360_000 : 362_000, control.Timer.ElapsedMs);
      }
    });

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
