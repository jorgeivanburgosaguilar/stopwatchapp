using Microsoft.Extensions.Time.Testing;
using StopwatchApp.Controls;
using StopwatchApp.Models;
using StopwatchApp.Services;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>
/// Control-level tests for the Resume feature and the Stop-last button order (AGENTS.md §8.3/§8.5).
/// Controls are built without a live desktop, like <c>StopwatchResetControlTests</c>.
/// </summary>
public sealed class ResumeUiTests
{
  [Fact]
  public Task ControlRow_KeepsStopFarRightInEveryState() =>
    RunOnStaAsync(async () =>
    {
      using StopwatchControl control = new(new FakeStopwatchStore(), new FakeTimeProvider(), 5, 0);

      Assert.Equal(["Start", "Reset", "Stop"], control.VisibleButtonTexts);

      control.StartTimer();
      Assert.Equal(["Pause", "Lap", "Reset", "Stop"], control.VisibleButtonTexts);

      await control.PauseTimerAsync();
      Assert.Equal(["Continue", "Reset", "Stop"], control.VisibleButtonTexts);
    });

  [Fact]
  public Task ResumeRecord_FromIdle_AsksWithoutALiveSessionAndLoadsTheRecordPaused() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      long id = store.AddLegacyRecord(1_000, 9_000, 7, []);
      using StopwatchControl control = new(store, new FakeTimeProvider(), 5, 0);
      await control.RestoreAsync();
      bool? liveSessionAsked = null;
      control.ConfirmResume = (_, hasLiveSession) =>
      {
        liveSessionAsked = hasLiveSession;
        return true;
      };

      bool resumed = await control.ResumeRecordAsync(control.Timer.Records[0]);

      Assert.True(resumed);
      Assert.False(liveSessionAsked);
      Assert.True(control.Timer.IsPaused);
      Assert.Equal(7 * 60_000, control.Timer.ElapsedMs);
      Assert.Equal(["Continue", "Reset", "Stop"], control.VisibleButtonTexts);
      Assert.DoesNotContain(control.Timer.Records, record => record.Id == id);
    });

  [Fact]
  public Task ResumeRecord_WhileRunning_WarnsAboutTheLiveSessionAndCancelResumesTheClock() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      store.AddLegacyRecord(1_000, 9_000, 7, []);
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      await control.RestoreAsync();
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      bool? liveSessionAsked = null;
      control.ConfirmResume = (_, hasLiveSession) =>
      {
        liveSessionAsked = hasLiveSession;
        Assert.False(control.Timer.IsRunning); // the clock is held while the question is open
        return false;
      };

      bool resumed = await control.ResumeRecordAsync(control.Timer.Records[0]);

      Assert.False(resumed);
      Assert.True(liveSessionAsked);
      Assert.True(control.Timer.IsRunning);
      Assert.Single(control.Timer.Records);
      Assert.Equal(10_000, control.Timer.ElapsedMs);
    });

  [Fact]
  public Task ResumeRecord_WhileRunning_WhenConfirmed_ReplacesTheSession() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      store.AddLegacyRecord(1_000, 9_000, 7, []);
      FakeTimeProvider time = new();
      using StopwatchControl control = new(store, time, 5, 0);
      await control.RestoreAsync();
      control.StartTimer();
      time.Advance(TimeSpan.FromSeconds(10));
      control.Timer.Tick();
      control.AddLap();
      control.ConfirmResume = (_, _) => true;

      bool resumed = await control.ResumeRecordAsync(control.Timer.Records[0]);

      Assert.True(resumed);
      Assert.True(control.Timer.IsPaused);
      Assert.Equal(7 * 60_000, control.Timer.ElapsedMs);
      Assert.Empty(control.Timer.Laps);
    });

  [Fact]
  public Task ResumeRecord_WithAnUnwiredCallback_CancelsByDefault() =>
    RunOnStaAsync(async () =>
    {
      FakeStopwatchStore store = new();
      store.AddLegacyRecord(1_000, 9_000, 7, []);
      using StopwatchControl control = new(store, new FakeTimeProvider(), 5, 0);
      await control.RestoreAsync();

      bool resumed = await control.ResumeRecordAsync(control.Timer.Records[0]);

      Assert.False(resumed);
      Assert.Single(control.Timer.Records);
      Assert.False(control.Timer.IsPaused);
    });

  [Fact]
  public void ResumePill_SitsInsideTheRowCardAtItsRightEdge()
  {
    using Font font = Typography.CreateMonospaceBodyFont();
    Rectangle card = new(0, 10, 600, 40);

    Rectangle pill = RecordsListControl.ResumePillBounds(card, font, 1f);

    Assert.True(card.Contains(pill));
    Assert.True(pill.Right < card.Right);
    Assert.True(pill.Left > card.Width / 2);
    Assert.Equal(card.Top + ((card.Height - pill.Height) / 2), pill.Top);
    Assert.True(RecordsListControl.ResumePillReserve(font, 1f) > pill.Width);
  }

  [Fact]
  public void ResumePill_GrowsWithDpiScale()
  {
    using Font font = Typography.CreateMonospaceBodyFont();

    int atOneX = RecordsListControl.ResumePillWidth(font, 1f);
    int atTwoX = RecordsListControl.ResumePillWidth(font, 2f);

    Assert.True(atTwoX > atOneX);
  }

  [Fact]
  public Task MainWindowRecords_HaveNoClearAllButtonAndKeepManageRecords() =>
    RunOnStaAsync(() =>
    {
      using RecordsListControl control = new();
      control.UpdateRecords([new StopwatchRecord(1, 0, 60_000, 1)]);

      string[] buttonTexts = [.. Descendants(control).OfType<Button>().Select(b => b.Text)];

      Assert.Equal(["Manage Records"], buttonTexts);
      return Task.CompletedTask;
    });

  [Fact]
  public void DialogMessage_NamesTheRecordAndTheResumePoint()
  {
    StopwatchRecord exact = new(1, 0, 60_000, 2, 0, 157_250);

    string text = ResumeRecordDialog.FormatMessage(exact, null);

    Assert.Contains("at 00:02:37", text, StringComparison.Ordinal);
    Assert.Contains("paused", text, StringComparison.Ordinal);
    Assert.DoesNotContain("discarded", text, StringComparison.Ordinal);
  }

  [Fact]
  public void DialogMessage_ForALegacyRecord_UsesWholeMinutes()
  {
    StopwatchRecord legacy = new(1, 0, 60_000, 42);

    string text = ResumeRecordDialog.FormatMessage(legacy, null);

    Assert.Contains("at 00:42:00", text, StringComparison.Ordinal);
  }

  [Fact]
  public void DialogMessage_WithALiveSession_WarnsItWillBeDiscarded()
  {
    StopwatchRecord record = new(1, 0, 60_000, 2, 0, 120_000);

    string text = ResumeRecordDialog.FormatMessage(record, 65_000);

    Assert.Contains(
      "The current session (00:01:05) and its laps will be discarded.",
      text,
      StringComparison.Ordinal
    );
  }

  [Fact]
  public void ResumableRecordCount_MatchesTheMainWindowPreview() =>
    Assert.Equal(5, StopwatchTimer.RecentRecordCount);

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
