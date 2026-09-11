using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="TrayIconService.SelectLayout"/> and <see cref="TrayIconService.FormatHourText"/>,
/// the two pure rules S11c factored out of the icon-drawing code (AGENTS.md §10.1/§17: which layout
/// applies, and how the stacked layout's hours row is formatted). The rest of
/// <see cref="TrayIconService"/> renders raw GDI+ pixel output via a real <c>NotifyIcon</c>/HICON and
/// is not unit tested, per AGENTS.md §13/§17.
/// </summary>
public class TrayIconServiceTests
{
  // InlineData can't carry the internal TrayIconLayout enum as a public-method parameter type (the
  // accessibility mismatch is a compile error, CS0051) even with InternalsVisibleTo, so the expected
  // layout is passed as a bool ("is the large-minutes layout expected") and compared against the
  // enum value inside the test body instead.
  [Theory]
  [InlineData(0, true)]
  [InlineData(1, false)]
  [InlineData(2, false)]
  [InlineData(23, false)]
  [InlineData(100, false)]
  public void SelectLayout_ByElapsedHours_MatchesSpec(int hours, bool expectLargeMinutes)
  {
    TrayIconService.TrayIconLayout expected = expectLargeMinutes
      ? TrayIconService.TrayIconLayout.LargeMinutes
      : TrayIconService.TrayIconLayout.StackedHoursMinutes;

    Assert.Equal(expected, TrayIconService.SelectLayout(hours));
  }

  // Covers TrayIconService.FormatHourText, the pure formatting rule behind the S11c follow-up: the
  // stacked layout's hours row is deliberately not zero-padded, unlike every other digit display in
  // this app (AGENTS.md §17). The single-digit case (2h -> "2", not "02") is the one the repo owner
  // specifically called out.
  [Theory]
  [InlineData(0, "0")]
  [InlineData(1, "1")]
  [InlineData(2, "2")]
  [InlineData(9, "9")]
  [InlineData(10, "10")]
  [InlineData(23, "23")]
  [InlineData(100, "100")]
  public void FormatHourText_NeverZeroPads(int hours, string expected)
  {
    Assert.Equal(expected, TrayIconService.FormatHourText(hours));
  }
}
