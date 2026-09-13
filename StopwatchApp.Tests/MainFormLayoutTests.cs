using StopwatchApp.Controls;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>
/// Pins <see cref="MainForm.RequiredClientWidth"/> — the DPI-aware floor under
/// <see cref="MainForm.FixedClientSize"/>'s width (S14a, AGENTS.md §17) — to the actual §8.5 row
/// templates, across every DPI Windows actually ships. The window cannot be widened by the user at
/// any DPI, so if a future change to those templates, the mono body font, or the DPI-scaling math
/// makes the worst-case row wider than what this method returns, it must fail here instead of
/// silently clipping in a window nobody can resize.
///
/// No <see cref="MainForm"/> instance is constructed (it opens the real database and a
/// <c>NotifyIcon</c>) — <see cref="MainForm.RequiredClientWidth"/> is a pure function of a font and
/// a DPI, tested directly. This matters because the original (S14) version of this test measured
/// text with the bare, DPI-unaware <see cref="TextRenderer.MeasureText(string, Font)"/> pattern,
/// which is calibrated to a fixed 96dpi baseline regardless of the host's actual display — it
/// passed at 96dpi while the shipped app clipped rows at the repo owner's real 125% scaling. Every
/// theory here runs the *same* live-DPI scaling technique <see cref="MainForm.RequiredClientWidth"/>
/// itself uses, so a regression to the old DPI-blind assumption fails at every non-96 data point.
/// </summary>
public sealed class MainFormLayoutTests
{
  [Theory]
  [InlineData(96)] // 100% — the one DPI a DPI-unaware test host can measure unassisted
  [InlineData(120)] // 125% — the repo owner's actual display (S14a, AGENTS.md §17)
  [InlineData(144)] // 150%
  [InlineData(168)] // 175%
  public void RequiredClientWidth_FitsWorstCaseLapRowAtGivenDpi(int deviceDpi)
  {
    Lap worstCaseLap = new(
      Id: 99_999,
      StartTimestamp: 0,
      EndTimestamp: 60_000,
      ElapsedMinutes: 9_999 * 60
    );
    string row = RecordsListControl.FormatLapRow(worstCaseLap);

    using Font unscaledMonoFont = Typography.CreateMonospaceBodyFont();
    int actualRowWidth = MeasureAtDpi(row, unscaledMonoFont, deviceDpi);
    int requiredWidth = MainForm.RequiredClientWidth(unscaledMonoFont, deviceDpi);

    Assert.True(
      requiredWidth >= actualRowWidth,
      $"MainForm.RequiredClientWidth({deviceDpi}) returned {requiredWidth}px, but the worst-case "
        + $"lap row \"{row}\" alone measures {actualRowWidth}px at that DPI — the returned width "
        + "must always be at least the row's own text width, or the window would clip it."
    );
  }

  [Theory]
  [InlineData(96)]
  [InlineData(120)]
  [InlineData(144)]
  [InlineData(168)]
  public void RequiredClientWidth_FitsWorstCaseRecordRowAtGivenDpi(int deviceDpi)
  {
    StopwatchRecord worstCaseRecord = new(
      Id: 99_999,
      StartTimestamp: 0,
      EndTimestamp: 60_000,
      ElapsedMinutes: 9_999 * 60
    );
    string row = RecordsListControl.FormatRecordRow(worstCaseRecord);

    using Font unscaledMonoFont = Typography.CreateMonospaceBodyFont();
    int actualRowWidth = MeasureAtDpi(row, unscaledMonoFont, deviceDpi);
    int requiredWidth = MainForm.RequiredClientWidth(unscaledMonoFont, deviceDpi);

    Assert.True(
      requiredWidth >= actualRowWidth,
      $"MainForm.RequiredClientWidth({deviceDpi}) returned {requiredWidth}px, but the worst-case "
        + $"record row \"{row}\" alone measures {actualRowWidth}px at that DPI."
    );
  }

  [Theory]
  [InlineData(120)]
  [InlineData(144)]
  [InlineData(168)]
  public void RequiredClientWidth_GrowsWithDpi(int deviceDpi)
  {
    // The S14a root cause was precisely this property being false: MainForm.FixedClientSize used
    // to be assigned straight to ClientSize with no DPI scaling at all, so the required window
    // width never grew even though the DPI-scaled text did.
    using Font monoFont = Typography.CreateMonospaceBodyFont();
    int widthAt96 = MainForm.RequiredClientWidth(monoFont, 96);
    int widthAtDpi = MainForm.RequiredClientWidth(monoFont, deviceDpi);

    Assert.True(
      widthAtDpi > widthAt96,
      $"RequiredClientWidth(96) = {widthAt96}, RequiredClientWidth({deviceDpi}) = {widthAtDpi} — "
        + "the required width must strictly increase with DPI."
    );
  }

  /// <summary>
  /// Measures <paramref name="text"/> as it will actually render at <paramref name="deviceDpi"/>,
  /// using the same technique as <see cref="MainForm.RequiredClientWidth"/>: rebuilding
  /// <paramref name="unscaledFont"/> at an equivalent, pre-scaled point size before measuring,
  /// since <see cref="TextRenderer.MeasureText(string, Font)"/> otherwise measures a <see cref="Font"/>'s
  /// point size against a fixed 96dpi baseline regardless of the target DPI (S14a, AGENTS.md §17).
  /// </summary>
  private static int MeasureAtDpi(string text, Font unscaledFont, int deviceDpi)
  {
    float scale = deviceDpi / 96f;
    using Font scaledFont = new(
      unscaledFont.FontFamily,
      unscaledFont.Size * scale,
      unscaledFont.Style
    );
    return TextRenderer
      .MeasureText(
        text,
        scaledFont,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width;
  }
}
