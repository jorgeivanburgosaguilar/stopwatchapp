using StopwatchApp.Controls;
using StopwatchApp.Formatting;
using StopwatchApp.Models;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="RecordsListControl"/>'s pure row-formatting logic — the only UI-free part of
/// this stage (AGENTS.md §13); everything else is a manual/visual check per the plan's S6 "Done
/// when".
/// </summary>
public sealed class RecordsListControlTests
{
  [Fact]
  public void FormatRecordRow_UsesExactTemplateFromSpec()
  {
    StopwatchRecord record = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);

    string row = RecordsListControl.FormatRecordRow(record);

    string expectedDate = TimeFormat.FormatDate(record.StartTimestamp);
    string expectedStart = TimeFormat.FormatTimeOnly(record.StartTimestamp);
    string expectedEnd = TimeFormat.FormatTimeOnly(record.EndTimestamp);
    string expectedElapsed = TimeFormat.FormatElapsed(record.ElapsedMinutes);
    Assert.Equal(
      $"📅 {expectedDate} ⏱ {expectedStart}-{expectedEnd} ⏳ Duration: {expectedElapsed}",
      row
    );
  }

  [Fact]
  public void FormatLapRow_UsesExactTemplateFromSpec()
  {
    Lap lap = new(Id: 2, StartTimestamp: 0, EndTimestamp: 61_000, ElapsedMinutes: 1);

    string row = RecordsListControl.FormatLapRow(lap);

    string expectedDate = TimeFormat.FormatDate(lap.StartTimestamp);
    string expectedStart = TimeFormat.FormatTimeOnly(lap.StartTimestamp);
    string expectedEnd = TimeFormat.FormatTimeOnly(lap.EndTimestamp);
    string expectedElapsed = TimeFormat.FormatElapsed(lap.ElapsedMinutes);
    Assert.Equal(
      $"📅 {expectedDate} ⏱ {expectedStart}-{expectedEnd} ⏳ Lap {lap.Id}: {expectedElapsed}",
      row
    );
  }
}
