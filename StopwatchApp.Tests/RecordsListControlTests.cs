using StopwatchApp.Controls;
using StopwatchApp.Formatting;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="RecordsListControl"/>'s pure row-formatting logic — the only UI-free part of
/// this surface (AGENTS.md §13); everything else is a manual/visual check per AGENTS.md §14.
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

  // AGENTS.md §8.5/§13 — MeasureRowHeight is a pure function (no ListBox needed), the same
  // pure-function-over-a-real-control convention MainForm.RequiredClientWidth already uses
  // (AGENTS.md §13), so the word-wrap threshold a row past MainForm.WorstCaseElapsedMinutes/a
  // 4-digit lap id crosses can be tested directly instead of driving a real owner-drawn ListBox.
  [Fact]
  public void MeasureRowHeight_WrapsWhenTextExceedsAvailableWidth()
  {
    using Font font = Typography.CreateMonospaceBodyFont();
    Lap withinWorstCase = new(
      Id: 999,
      StartTimestamp: 0,
      EndTimestamp: 0,
      ElapsedMinutes: MainForm.WorstCaseElapsedMinutes
    );
    Lap pastWorstCase = new(
      Id: 9_999,
      StartTimestamp: 0,
      EndTimestamp: 0,
      ElapsedMinutes: MainForm.WorstCaseElapsedMinutes * 10
    );

    // The available width is the widest the 24-hour worst-case row is actually sized to need — a
    // row past that (a longer session, a 4-digit lap id) should now wrap to a second line instead
    // of clipping.
    int availableWidth = TextRenderer
      .MeasureText(
        RecordsListControl.FormatLapRow(withinWorstCase),
        font,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width;

    int heightAtWorstCase = RecordsListControl.MeasureRowHeight(
      RecordsListControl.FormatLapRow(withinWorstCase),
      font,
      availableWidth
    );
    int heightPastWorstCase = RecordsListControl.MeasureRowHeight(
      RecordsListControl.FormatLapRow(pastWorstCase),
      font,
      availableWidth
    );

    Assert.True(
      heightPastWorstCase > heightAtWorstCase,
      $"A row past the sized-for worst case measured {heightPastWorstCase}px tall, no taller than "
        + $"the worst-case row's {heightAtWorstCase}px — it should have wrapped to a second line "
        + "instead of clipping."
    );
  }
}
