using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// The persistence surface for completed records and the single saved-session slot. Deliberately
/// minimal — no update, no delete-by-id, no range query. Every implementation must wrap each
/// method body in try/catch and swallow errors: a corrupt or unreadable saved session returns
/// <see langword="null"/> rather than throwing, and a failed write must never surface an exception
/// to the UI (AGENTS.md §9).
/// </summary>
public interface IStopwatchStore
{
  /// <summary>
  /// Persists a completed session as a new record.
  /// </summary>
  /// <param name="startTimestamp">The session's start time, in epoch milliseconds (UTC).</param>
  /// <param name="endTimestamp">The session's end time, in epoch milliseconds (UTC).</param>
  /// <param name="elapsedMs">The session's elapsed time, in milliseconds. Floored to whole minutes internally before storing.</param>
  /// <returns>The new row's id.</returns>
  Task<long> SaveRecordAsync(long startTimestamp, long endTimestamp, long elapsedMs);

  /// <summary>
  /// Loads every persisted record, newest first.
  /// </summary>
  Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync();

  /// <summary>
  /// Deletes every persisted record.
  /// </summary>
  Task ClearAllRecordsAsync();

  /// <summary>
  /// Saves (upserting into the single slot) a snapshot of the currently paused session.
  /// </summary>
  /// <param name="session">The snapshot to save.</param>
  Task SavePausedSessionAsync(PausedSession session);

  /// <summary>
  /// Loads the saved paused-session snapshot, if any.
  /// </summary>
  /// <returns>The saved snapshot, or <see langword="null"/> if none exists or it fails to deserialize.</returns>
  Task<PausedSession?> LoadPausedSessionAsync();

  /// <summary>
  /// Deletes the saved paused-session snapshot, if any.
  /// </summary>
  Task ClearPausedSessionAsync();

  /// <summary>
  /// Saves (upserting into the single slot) the window's last manually-dragged position (AGENTS.md
  /// §10.6).
  /// </summary>
  /// <param name="x">The window's <c>Location.X</c>.</param>
  /// <param name="y">The window's <c>Location.Y</c>.</param>
  Task SaveWindowPositionAsync(int x, int y);

  /// <summary>
  /// Loads the saved window position, if any.
  /// </summary>
  /// <returns>The saved position, or <see langword="null"/> if none exists or it fails to deserialize.</returns>
  Task<(int X, int Y)?> LoadWindowPositionAsync();
}
