using System.Drawing.Drawing2D;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers the pure rules factored out of <see cref="TrayIconService"/>: the simplified layouts and
/// formatting rules, plus which tray-icon mouse button opens the window.
/// The rest of
/// <see cref="TrayIconService"/> renders raw GDI+ pixel output via a real <c>NotifyIcon</c>/HICON and
/// is not unit tested, per AGENTS.md §13/§14.
/// </summary>
public class TrayIconServiceTests
{
  // expectedLayout is passed by name, not by TrayIconLayout value: that enum is internal, and a
  // public [Theory] method cannot take an internal type as a parameter (CS0051). Parsing by name
  // also avoids depending on the enum's ordinal order, unlike a plain int cast.
  [Theory]
  [InlineData(0, nameof(TrayIconService.TrayIconLayout.LargeMinutes), 0, 0)]
  [InlineData(59, nameof(TrayIconService.TrayIconLayout.LargeMinutes), 59, 0)]
  [InlineData(60, nameof(TrayIconService.TrayIconLayout.HourMinute), 0, 1)]
  [InlineData(61, nameof(TrayIconService.TrayIconLayout.HourMinute), 1, 1)]
  [InlineData(119, nameof(TrayIconService.TrayIconLayout.HourMinute), 59, 1)]
  [InlineData(120, nameof(TrayIconService.TrayIconLayout.HourMinute), 0, 2)]
  [InlineData(599, nameof(TrayIconService.TrayIconLayout.HourMinute), 59, 9)]
  [InlineData(600, nameof(TrayIconService.TrayIconLayout.LargeHours), 0, 10)]
  [InlineData(1439, nameof(TrayIconService.TrayIconLayout.LargeHours), 0, 23)]
  [InlineData(1440, nameof(TrayIconService.TrayIconLayout.LargeDays), 0, 1)]
  [InlineData(2879, nameof(TrayIconService.TrayIconLayout.LargeDays), 0, 1)]
  [InlineData(2880, nameof(TrayIconService.TrayIconLayout.LargeDays), 0, 2)]
  public void GetDisplayValues_UsesMinutesThenHourMinuteThenHoursThenDays(
    long totalMinutes,
    string expectedLayoutName,
    int expectedMinutes,
    long expectedValue
  )
  {
    (long value, int minutes, TrayIconService.TrayIconLayout layout) =
      TrayIconService.GetDisplayValues(totalMinutes * 60000);

    Assert.Equal(expectedValue, value);
    Assert.Equal(expectedMinutes, minutes);
    Assert.Equal(Enum.Parse<TrayIconService.TrayIconLayout>(expectedLayoutName), layout);
  }

  [Theory]
  [InlineData(10, "10H")]
  [InlineData(23, "23H")]
  public void FormatHourLabel_UsesAnUnpaddedUppercaseUnit(long hours, string expected)
  {
    Assert.Equal(expected, TrayIconService.FormatHourLabel(hours));
  }

  [Theory]
  [InlineData(1, 0, "1:00")]
  [InlineData(1, 1, "1:01")]
  [InlineData(9, 59, "9:59")]
  public void FormatHourMinuteLabel_PadsMinutesToTwoDigits(long hours, int minutes, string expected)
  {
    Assert.Equal(expected, TrayIconService.FormatHourMinuteLabel(hours, minutes));
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

  // Regression guard for the bug where hour/day labels rendered far smaller than the minutes
  // readout: "1H"/"1D" measured their ink against a padded line-box measurement instead of actual
  // glyph bounds, so they were rejected down to a fraction of the minutes font size even though
  // their ink fits the canvas just as comfortably as "45"'s. A short unit label must land within a
  // few pixels of a same-length minutes label, not merely "somewhere smaller".
  [Theory]
  [InlineData("1D")]
  [InlineData("9D")]
  public void MeasureLabelFontSize_MatchesMinutesSizeForShortLabels(string text)
  {
    int minutesSize = TrayIconService.MeasureLabelFontSize("45");
    int unitSize = TrayIconService.MeasureLabelFontSize(text);

    Assert.InRange(unitSize, minutesSize - 4, minutesSize);
  }

  // The fit loop must never return a size whose glyph ink overflows the icon canvas, for every
  // label shape the tray actually renders — two-digit minutes, one- and two-digit hours/days, and
  // a pathologically long label to confirm the floor is honored rather than throwing.
  [Theory]
  [InlineData("00")]
  [InlineData("45")]
  [InlineData("59")]
  [InlineData("1:00")]
  [InlineData("1:01")]
  [InlineData("9:59")]
  [InlineData("10H")]
  [InlineData("23H")]
  [InlineData("1D")]
  [InlineData("9D")]
  [InlineData("365D")]
  public void MeasureLabelFontSize_ChosenSizeFitsTheIconCanvas(string text)
  {
    int size = TrayIconService.MeasureLabelFontSize(text);

    using FontFamily family = new("Segoe UI");
    using GraphicsPath path = new();
    path.AddString(
      text,
      family,
      (int)FontStyle.Bold,
      size,
      PointF.Empty,
      StringFormat.GenericTypographic
    );
    RectangleF bounds = path.GetBounds();

    Assert.True(size >= 8, $"Expected the fit floor to be honored, got {size}.");
    Assert.True(
      bounds.Width <= 30f,
      $"Width {bounds.Width} exceeded the fit budget at size {size}."
    );
    Assert.True(
      bounds.Height <= 30f,
      $"Height {bounds.Height} exceeded the fit budget at size {size}."
    );
  }
}
