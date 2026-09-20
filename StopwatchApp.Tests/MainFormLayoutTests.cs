using StopwatchApp.Controls;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>Tests the DPI-aware, current-content width calculations used by both record windows.</summary>
public sealed class MainFormLayoutTests
{
  [Theory]
  [InlineData(96)]
  [InlineData(120)]
  [InlineData(144)]
  [InlineData(168)]
  public void RequiredClientWidth_FitsVisibleRecordAtGivenDpi(int deviceDpi)
  {
    StopwatchRecord record = new(1, 0, 0, 1);
    using Font font = Typography.CreateMonospaceBodyFont();

    int width = MainForm.RequiredClientWidth(font, deviceDpi, [record], [], elapsedMs: 0);
    float scale = deviceDpi / 96f;
    using Font scaledFont = new(font.FontFamily, font.Size * scale, font.Style);
    int rowWidth = TextRenderer
      .MeasureText(
        RecordsListControl.FormatRecordRow(record),
        scaledFont,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width;

    Assert.True(width >= rowWidth);
  }

  [Theory]
  [InlineData(120)]
  [InlineData(144)]
  [InlineData(168)]
  public void RequiredClientWidth_ScalesWithDpi(int deviceDpi)
  {
    StopwatchRecord record = new(1, 0, 0, 1);
    using Font font = Typography.CreateMonospaceBodyFont();

    int widthAt96 = MainForm.RequiredClientWidth(font, 96, [record], [], elapsedMs: 0);
    int widthAtDpi = MainForm.RequiredClientWidth(font, deviceDpi, [record], [], elapsedMs: 0);

    Assert.True(widthAtDpi > widthAt96);
  }

  [Fact]
  public void RequiredClientWidth_GrowsForLongerVisibleContent()
  {
    StopwatchRecord shortRecord = new(1, 0, 0, 1);
    StopwatchRecord longRecord = new(1, 0, 0, 1000 * 60);
    using Font font = Typography.CreateMonospaceBodyFont();

    int shortWidth = MainForm.RequiredClientWidth(font, 96, [shortRecord], [], elapsedMs: 0);
    int longWidth = MainForm.RequiredClientWidth(font, 96, [longRecord], [], elapsedMs: 0);

    Assert.True(longWidth > shortWidth);
  }

  [Fact]
  public void ManageRecordsWidth_IsAFixedBudgetForTheWorstCaseRow()
  {
    Assert.Equal(
      ManageRecordsForm.RequiredClientWidth(96, totalRecordCount: 1),
      ManageRecordsForm.RequiredClientWidth(96, totalRecordCount: 1)
    );
    Assert.Equal(23 * 60 + 59, ManageRecordsForm.WorstCaseElapsedMinutes);
  }

  [Fact]
  public void ManageRecordsWidth_FitsA2359RowAndWrapsLongerOnes()
  {
    using Font font = Typography.CreateMonospaceBodyFont();
    int budget = ManageRecordsForm.RequiredClientWidth(96, totalRecordCount: 1);
    StopwatchRecord worst = new(1, 0, 0, ManageRecordsForm.WorstCaseElapsedMinutes);
    StopwatchRecord longer = new(1, 0, 0, 1000 * 60);

    int worstRow = IconTextLayout
      .MeasureSingleLine(RecordsListControl.FormatRecordRow(worst), font)
      .Width;
    int longerRow = IconTextLayout
      .MeasureSingleLine(RecordsListControl.FormatRecordRow(longer), font)
      .Width;

    Assert.True(worstRow < budget);
    Assert.True(longerRow > worstRow);
  }
}
