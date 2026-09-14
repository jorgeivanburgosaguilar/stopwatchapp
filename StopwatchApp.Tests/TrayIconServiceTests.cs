using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers the pure rules factored out of <see cref="TrayIconService"/>: the simplified layouts and
/// formatting rules, plus which tray-icon mouse button opens the window.
/// The rest of
/// <see cref="TrayIconService"/> renders raw GDI+ pixel output via a real <c>NotifyIcon</c>/HICON and
/// is not unit tested, per AGENTS.md §13/§17.
/// </summary>
public class TrayIconServiceTests
{
  [Theory]
  [InlineData(0, 0, 0, 0)]
  [InlineData(59, 0, 59, 0)]
  [InlineData(60, 1, 0, 1)]
  [InlineData(119, 1, 0, 1)]
  [InlineData(120, 1, 0, 2)]
  [InlineData(1439, 1, 0, 23)]
  [InlineData(1440, 2, 0, 1)]
  [InlineData(2879, 2, 0, 1)]
  [InlineData(2880, 2, 0, 2)]
  public void GetDisplayValues_UsesMinutesThenHoursThenDays(
    long totalMinutes,
    int expectedLayout,
    int expectedMinutes,
    long expectedValue
  )
  {
    (long value, int minutes, TrayIconService.TrayIconLayout layout) =
      TrayIconService.GetDisplayValues(totalMinutes * 60000);

    Assert.Equal(expectedValue, value);
    Assert.Equal(expectedMinutes, minutes);
    Assert.Equal((TrayIconService.TrayIconLayout)expectedLayout, layout);
  }

  [Theory]
  [InlineData(1, "1H")]
  [InlineData(9, "9H")]
  [InlineData(10, "10H")]
  [InlineData(23, "23H")]
  public void FormatHourLabel_UsesAnUnpaddedUppercaseUnit(long hours, string expected)
  {
    Assert.Equal(expected, TrayIconService.FormatHourLabel(hours));
  }

  [Theory]
  [InlineData(1, "1D")]
  [InlineData(2, "2D")]
  [InlineData(10, "10D")]
  public void FormatDayLabel_UsesAnUnpaddedUppercaseUnit(long days, string expected)
  {
    Assert.Equal(expected, TrayIconService.FormatDayLabel(days));
  }

  [Theory]
  [InlineData(MouseButtons.Left, true)]
  [InlineData(MouseButtons.Right, false)]
  [InlineData(MouseButtons.Middle, false)]
  [InlineData(MouseButtons.None, false)]
  public void ShouldOpen_OnlyAcceptsLeftClick(MouseButtons button, bool expected)
  {
    Assert.Equal(expected, TrayIconService.ShouldOpen(button));
  }
}
