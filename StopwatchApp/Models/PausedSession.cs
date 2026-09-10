namespace StopwatchApp.Models;

/// <summary>
/// A snapshot of a running session taken at the moment it was paused, persisted so it can be
/// restored after an app restart. There is exactly one saved-session slot — pausing a second time
/// overwrites the first snapshot.
/// </summary>
/// <param name="ElapsedTime">The session's elapsed time at the moment of pausing, in milliseconds.</param>
/// <param name="SessionStartTime">The session's wall-clock start time, in epoch milliseconds (UTC).</param>
/// <param name="Laps">The session's laps at the moment of pausing, newest first.</param>
/// <param name="LastLapElapsed">The <c>ElapsedTime</c> value recorded at the last lap.</param>
/// <param name="LastLapTimestamp">The wall-clock time of the last lap, in epoch milliseconds (UTC).</param>
/// <param name="PausedAt">The wall-clock time the session was paused, in epoch milliseconds (UTC).</param>
public sealed record PausedSession(
  long ElapsedTime,
  long SessionStartTime,
  IReadOnlyList<Lap> Laps,
  long LastLapElapsed,
  long LastLapTimestamp,
  long PausedAt
);
