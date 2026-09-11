using StopwatchApp.Controls;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="StopwatchControl.MapShortcut"/> — the one UI-free part of this stage
/// (AGENTS.md §13); everything else (button dispatch, tooltips, ProcessCmdKey) is a manual/visual
/// check per the S10 plan's "Done when".
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
}
