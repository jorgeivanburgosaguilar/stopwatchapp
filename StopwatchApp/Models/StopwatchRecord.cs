namespace StopwatchApp.Models;

/// <summary>
/// A completed, persisted timing session.
/// </summary>
/// <param name="Id">The database row id.</param>
/// <param name="StartTimestamp">The session's start time, in epoch milliseconds (UTC).</param>
/// <param name="EndTimestamp">The session's end time, in epoch milliseconds (UTC).</param>
/// <param name="ElapsedMinutes">The session's duration, floored to whole minutes.</param>
/// <param name="LapCount">
/// The number of laps saved alongside this record. Lets the UI decide whether a record has a
/// detail to expand without a separate query per row.
/// </param>
public sealed record StopwatchRecord(
  long Id,
  long StartTimestamp,
  long EndTimestamp,
  long ElapsedMinutes,
  long LapCount = 0
);
