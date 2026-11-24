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
/// <param name="ElapsedMs">
/// The session's exact elapsed time in milliseconds, or <see langword="null"/> for a record saved
/// before exact times were stored (it can then only be resumed at minute precision).
/// </param>
/// <param name="LastLapElapsedMs">
/// The session's elapsed time at its last real lap press, before Stop appended its final partial
/// lap, or <see langword="null"/> when unknown. Lets Resume drop that synthetic final lap and keep
/// the lap clock exact.
/// </param>
public sealed record StopwatchRecord(
  long Id,
  long StartTimestamp,
  long EndTimestamp,
  long ElapsedMinutes,
  long LapCount = 0,
  long? ElapsedMs = null,
  long? LastLapElapsedMs = null
);
