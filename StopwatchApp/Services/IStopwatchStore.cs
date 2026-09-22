using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// The persistence surface for completed records, their laps, and the single saved-session slot.
/// Deliberately minimal — no update or range query. Every implementation must wrap each
/// method body in try/catch and swallow errors: a corrupt or unreadable saved session returns
/// <see langword="null"/> rather than throwing, and a failed write must never surface an exception
/// to the UI (AGENTS.md §9).
/// </summary>
public interface IStopwatchStore
{
  /// <summary>
  /// Persists a completed session as a new record together with its laps, atomically.
  /// </summary>
  /// <param name="startTimestamp">The session's start time, in epoch milliseconds (UTC).</param>
  /// <param name="endTimestamp">The session's end time, in epoch milliseconds (UTC).</param>
  /// <param name="elapsedMs">The session's elapsed time, in milliseconds. Floored to whole minutes internally before storing.</param>
  /// <param name="laps">The session's laps, in any order; each lap's own elapsed minutes are stored as already floored.</param>
  /// <returns>The new row's id.</returns>
  Task<long> SaveRecordAsync(
    long startTimestamp,
    long endTimestamp,
    long elapsedMs,
    IReadOnlyList<Lap> laps
  );

  /// <summary>
  /// Loads every persisted record, newest first.
  /// </summary>
  Task<IReadOnlyList<StopwatchRecord>> GetAllRecordsAsync();

  /// <summary>
  /// Loads the laps saved for one record, newest lap first.
  /// </summary>
  /// <param name="recordId">The owning record's id.</param>
  /// <returns>The record's laps, newest first, or an empty list if none exist or the record is unknown.</returns>
  Task<IReadOnlyList<Lap>> GetLapsAsync(long recordId);

  /// <summary>
  /// Deletes one persisted record, and its laps, by its identifier. A missing identifier is a
  /// harmless no-op.
  /// </summary>
  /// <param name="id">The primary-key identifier of the record to delete.</param>
  Task DeleteRecordAsync(long id);

  /// <summary>
  /// Deletes every persisted record and every persisted lap.
  /// </summary>
  Task ClearAllRecordsAsync();

  /// <summary>
  /// Saves (upserting into the single slot) a paused or autosaved session snapshot.
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
}
