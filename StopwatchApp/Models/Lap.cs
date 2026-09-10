namespace StopwatchApp.Models;

/// <summary>
/// An in-memory or restored split within a running session. Each lap is the interval since the
/// previous lap, not a cumulative total.
/// </summary>
/// <param name="Id">The lap's 1-based sequence number within its session.</param>
/// <param name="StartTimestamp">The lap's start time, in epoch milliseconds (UTC).</param>
/// <param name="EndTimestamp">The lap's end time, in epoch milliseconds (UTC).</param>
/// <param name="ElapsedMinutes">The lap's split duration, floored to whole minutes.</param>
public sealed record Lap(long Id, long StartTimestamp, long EndTimestamp, long ElapsedMinutes);
