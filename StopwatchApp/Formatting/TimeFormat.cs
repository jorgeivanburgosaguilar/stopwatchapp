using System.Globalization;

namespace StopwatchApp.Formatting;

/// <summary>
/// Pure, stateless time-formatting functions shared by the timer display, the tray tooltip, and
/// the records/laps lists.
/// </summary>
public static class TimeFormat
{
  /// <summary>
  /// Formats an elapsed duration as <c>HH:mm:ss</c>. Hours are not clamped — a duration of 100
  /// hours renders as <c>"100:00:00"</c> rather than wrapping at 24 hours.
  /// </summary>
  /// <param name="ms">The elapsed duration, in milliseconds.</param>
  /// <returns>The duration formatted as <c>HH:mm:ss</c>.</returns>
  public static string FormatTime(long ms)
  {
    long totalSeconds = ms / 1000;
    long hours = totalSeconds / 3600;
    long minutes = (totalSeconds % 3600) / 60;
    long seconds = totalSeconds % 60;
    return $"{hours.ToString("D2", CultureInfo.InvariantCulture)}:"
      + $"{minutes.ToString("D2", CultureInfo.InvariantCulture)}:"
      + $"{seconds.ToString("D2", CultureInfo.InvariantCulture)}";
  }

  /// <summary>
  /// Formats a Unix epoch-millisecond timestamp as a local-time date, <c>yyyy-MM-dd</c>.
  /// </summary>
  /// <param name="unixMs">The timestamp, in milliseconds since the Unix epoch (UTC).</param>
  /// <returns>The local date formatted as <c>yyyy-MM-dd</c>.</returns>
  public static string FormatDate(long unixMs)
  {
    DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
    return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Formats a Unix epoch-millisecond timestamp as a local 24-hour time, <c>HH:mm:ss</c>.
  /// </summary>
  /// <param name="unixMs">The timestamp, in milliseconds since the Unix epoch (UTC).</param>
  /// <returns>The local time formatted as <c>HH:mm:ss</c>.</returns>
  public static string FormatTimeOnly(long unixMs)
  {
    DateTimeOffset local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
    return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Formats a duration in whole minutes as <c>hh:mm</c>, with a hard 1-minute display floor: an
  /// input of <c>0</c> still renders as <c>"00:01"</c>. This floor is display-only — the stored
  /// value may legitimately be <c>0</c>.
  /// </summary>
  /// <param name="minutes">The duration, in whole minutes.</param>
  /// <returns>The duration formatted as <c>hh:mm</c>, floored to a minimum of one minute.</returns>
  public static string FormatElapsed(long minutes)
  {
    long displayMinutes = minutes < 1 ? 1 : minutes;
    long hours = displayMinutes / 60;
    long mins = displayMinutes % 60;
    return $"{hours.ToString("D2", CultureInfo.InvariantCulture)}:"
      + $"{mins.ToString("D2", CultureInfo.InvariantCulture)}";
  }
}
