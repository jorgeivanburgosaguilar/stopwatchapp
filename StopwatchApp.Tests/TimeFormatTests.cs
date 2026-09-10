using System.Globalization;
using StopwatchApp.Formatting;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers the four pure formatters in <see cref="TimeFormat"/> against the worked examples and
/// edge cases fixed in AGENTS.md §8.4.
/// </summary>
public class TimeFormatTests
{
  [Theory]
  [InlineData(0L, "00:01")]
  [InlineData(1L, "00:01")]
  [InlineData(60L, "01:00")]
  [InlineData(90L, "01:30")]
  public void FormatElapsed_WorkedExamples_MatchSpec(long minutes, string expected)
  {
    Assert.Equal(expected, TimeFormat.FormatElapsed(minutes));
  }

  [Fact]
  public void FormatTime_HoursBeyondTwentyFour_DoesNotWrap()
  {
    // 100 hours, in milliseconds. TimeSpan.ToString(@"hh\:mm\:ss") would wrap this to "04:00:00";
    // the spec requires the un-clamped "100:00:00".
    const long oneHundredHoursMs = 100L * 60 * 60 * 1000;

    Assert.Equal("100:00:00", TimeFormat.FormatTime(oneHundredHoursMs));
  }

  [Fact]
  public void FormatTime_ZeroMilliseconds_IsAllZeroes()
  {
    Assert.Equal("00:00:00", TimeFormat.FormatTime(0));
  }

  [Fact]
  public void FormatDate_ConvertsFromUtcToLocalTime()
  {
    // A fixed UTC instant. The expected string is derived independently of TimeFormat's own
    // ToLocalTime() call (via TimeZoneInfo.Local's offset) so the test doesn't just restate the
    // implementation, and so it still catches a regression to UTC-only formatting on machines
    // whose local zone differs from UTC.
    DateTimeOffset utc = new(2024, 3, 15, 10, 30, 45, TimeSpan.Zero);
    long unixMs = utc.ToUnixTimeMilliseconds();
    TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(utc.UtcDateTime);
    DateTime expectedLocal = utc.UtcDateTime + offset;

    string expected = expectedLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    Assert.Equal(expected, TimeFormat.FormatDate(unixMs));
  }

  [Fact]
  public void FormatTimeOnly_ConvertsFromUtcToLocalTime()
  {
    DateTimeOffset utc = new(2024, 3, 15, 10, 30, 45, TimeSpan.Zero);
    long unixMs = utc.ToUnixTimeMilliseconds();
    TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(utc.UtcDateTime);
    DateTime expectedLocal = utc.UtcDateTime + offset;

    string expected = expectedLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    Assert.Equal(expected, TimeFormat.FormatTimeOnly(unixMs));
  }
}
