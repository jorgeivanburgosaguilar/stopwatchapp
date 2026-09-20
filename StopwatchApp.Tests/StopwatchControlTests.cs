using StopwatchApp.Controls;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="StopwatchControl.MapShortcut"/> — the one UI-free part of this stage
/// (AGENTS.md §13); everything else (button dispatch, tooltips, ProcessCmdKey) is a manual/visual
/// check per AGENTS.md §14.
/// </summary>
public sealed class StopwatchControlTests
{
  [Fact]
  public void MapShortcut_BareSpace_ReturnsToggle()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.Space);

    Assert.Equal(StopwatchShortcut.Toggle, shortcut);
  }

  [Fact]
  public void MapShortcut_ShiftSpace_ReturnsLap()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.Shift | Keys.Space);

    Assert.Equal(StopwatchShortcut.Lap, shortcut);
  }

  [Fact]
  public void MapShortcut_BareEnter_ReturnsStop()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.Enter);

    Assert.Equal(StopwatchShortcut.Stop, shortcut);
  }

  [Fact]
  public void MapShortcut_CtrlSpace_ReturnsNull()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.Control | Keys.Space);

    Assert.Null(shortcut);
  }

  [Fact]
  public void MapShortcut_ShiftEnter_ReturnsNull()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.Shift | Keys.Enter);

    Assert.Null(shortcut);
  }

  [Fact]
  public void MapShortcut_PlainLetterKey_ReturnsNull()
  {
    StopwatchShortcut? shortcut = StopwatchControl.MapShortcut(Keys.A);

    Assert.Null(shortcut);
  }

  [Theory]
  [InlineData(299_999, true, false, 5, false)] // just under the threshold
  [InlineData(300_000, true, false, 5, true)] // exactly at the threshold
  [InlineData(900_000, true, false, 5, true)] // well above
  [InlineData(900_000, false, true, 5, true)] // already paused
  [InlineData(900_000, false, false, 5, false)] // idle
  [InlineData(900_000, true, false, 0, false)] // disabled
  [InlineData(900_000, false, true, 0, false)] // disabled while paused
  public void RequiresStopConfirmation_ReflectsThresholdAndState(
    long elapsedMs,
    bool isRunning,
    bool isPaused,
    int thresholdMinutes,
    bool expected
  )
  {
    bool actual = StopwatchControl.RequiresStopConfirmation(
      elapsedMs,
      isRunning,
      isPaused,
      thresholdMinutes
    );

    Assert.Equal(expected, actual);
  }

  [Fact]
  public void StopConfirmationDialog_FormatMessage_ShowsElapsedTime()
  {
    string message = StopConfirmationDialog.FormatMessage(325_000);

    Assert.Equal(
      "The stopwatch is at 00:05:25. Stop it and save this session as a record?",
      message
    );
  }
}
