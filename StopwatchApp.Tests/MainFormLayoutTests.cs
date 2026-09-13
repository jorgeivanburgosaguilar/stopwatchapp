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
  public void ManageRecordsWidth_GrowsForLongerCurrentPage()
  {
    StopwatchRecord shortRecord = new(1, 0, 0, 1);
    StopwatchRecord longRecord = new(1, 0, 0, 1000 * 60);

    int shortWidth = ManageRecordsForm.RequiredClientWidth(96, [shortRecord], totalRecordCount: 1);
    int longWidth = ManageRecordsForm.RequiredClientWidth(96, [longRecord], totalRecordCount: 1);

    Assert.True(longWidth > shortWidth);
  }
}
