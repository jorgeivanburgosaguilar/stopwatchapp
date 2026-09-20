using StopwatchApp.Controls;
using StopwatchApp.Models;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="IconTextLayout"/>'s pure tokenize/measure/wrap logic (AGENTS.md §8.5/§13).
/// </summary>
public sealed class IconTextLayoutTests
{
  [Fact]
  public void Tokenize_RecordRow_SplitsIntoIconsWordsAndSpaces()
  {
    StopwatchRecord record = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);

    List<(string? Text, RowIcon? Icon)> atoms = IconTextLayout.Tokenize(
      RecordsListControl.FormatRecordRow(record)
    );

    Assert.Equal(
      [RowIcon.Calendar, RowIcon.Stopwatch, RowIcon.Hourglass],
      atoms.Where(atom => atom.Icon is not null).Select(atom => atom.Icon!.Value)
    );
    Assert.Contains(atoms, atom => atom.Text == "Duration:");
    Assert.DoesNotContain(
      atoms,
      atom => atom.Text is not null && atom.Text.Contains("📅", StringComparison.Ordinal)
    );
  }

  [Fact]
  public void Tokenize_LapRow_UsesTheSameIconOrder()
  {
    Lap lap = new(Id: 2, StartTimestamp: 0, EndTimestamp: 61_000, ElapsedMinutes: 1);

    List<(string? Text, RowIcon? Icon)> atoms = IconTextLayout.Tokenize(
      RecordsListControl.FormatLapRow(lap)
    );

    Assert.Equal(
      [RowIcon.Calendar, RowIcon.Stopwatch, RowIcon.Hourglass],
      atoms.Where(atom => atom.Icon is not null).Select(atom => atom.Icon!.Value)
    );
    Assert.Contains(atoms, atom => atom.Text == "Lap");
  }

  [Fact]
  public void Tokenize_SwallowsAnEmojiPresentationSelector()
  {
    List<(string? Text, RowIcon? Icon)> atoms = IconTextLayout.Tokenize("⏱️ 10:00");

    Assert.Equal(RowIcon.Stopwatch, atoms[0].Icon);
    Assert.DoesNotContain(atoms, atom => atom.Text is not null && atom.Text.Contains('️'));
  }

  [Fact]
  public void Measure_IsWiderThanTheSameTextWithoutIcons()
  {
    using Font font = StopwatchApp.Theme.Typography.CreateMonospaceBodyFont();
    Lap lap = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);
    string row = RecordsListControl.FormatLapRow(lap);

    int withIcons = IconTextLayout.MeasureSingleLine(row, font).Width;
    int withoutIcons = IconTextLayout
      .MeasureSingleLine(row.Replace("📅", "").Replace("⏱", "").Replace("⏳", ""), font)
      .Width;

    // Three icons, each one line-height square, must add real width over the icon-less text.
    Assert.True(withIcons > withoutIcons + (font.Height * 2));
  }

  [Fact]
  public void Measure_WrapsToMoreLinesWhenNarrower()
  {
    using Font font = StopwatchApp.Theme.Typography.CreateMonospaceBodyFont();
    Lap lap = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);
    string row = RecordsListControl.FormatLapRow(lap);
    int single = IconTextLayout.MeasureSingleLine(row, font).Width;

    Size wide = IconTextLayout.Measure(row, font, single);
    Size narrow = IconTextLayout.Measure(row, font, single / 2);

    Assert.Equal(font.Height, wide.Height);
    Assert.True(narrow.Height > wide.Height);
    Assert.True(narrow.Width < wide.Width);
  }

  [Fact]
  public void Layout_NeverSplitsAnIconAcrossLinesAndKeepsItsFullSize()
  {
    using Font font = StopwatchApp.Theme.Typography.CreateMonospaceBodyFont();
    Lap lap = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);
    string row = RecordsListControl.FormatLapRow(lap);

    for (int width = font.Height; width < 600; width += 7)
    {
      var (items, _) = IconTextLayout.Layout(row, font, width);
      foreach ((Rectangle bounds, string? _, RowIcon? icon) in items)
      {
        if (icon is not null)
        {
          Assert.Equal(font.Height, bounds.Width);
          Assert.Equal(font.Height, bounds.Height);
        }
      }
    }
  }

  [Fact]
  public void Layout_ItemsNeverOverlapWithinALine()
  {
    using Font font = StopwatchApp.Theme.Typography.CreateMonospaceBodyFont();
    Lap lap = new(Id: 1, StartTimestamp: 0, EndTimestamp: 60_000, ElapsedMinutes: 1);
    var (items, _) = IconTextLayout.Layout(RecordsListControl.FormatLapRow(lap), font, 220);

    foreach (var line in items.GroupBy(item => item.Bounds.Y))
    {
      Rectangle[] ordered = [.. line.Select(item => item.Bounds).OrderBy(b => b.X)];
      for (int i = 1; i < ordered.Length; i++)
      {
        Assert.True(ordered[i].Left >= ordered[i - 1].Right, "Items on one line overlap.");
      }
    }
  }

  [Fact]
  public void Draw_PaintsIconPixelsInColorNotJustTextColor()
  {
    using Font font = StopwatchApp.Theme.Typography.CreateMonospaceBodyFont();
    using RowIconSet icons = new();
    using Bitmap canvas = new(200, font.Height * 2);
    using (Graphics graphics = Graphics.FromImage(canvas))
    {
      graphics.Clear(Color.White);
      IconTextLayout.Draw(
        graphics,
        "📅",
        font,
        new Rectangle(0, 0, canvas.Width, canvas.Height),
        Color.Black,
        icons
      );
    }

    HashSet<int> colors = [];
    for (int y = 0; y < canvas.Height; y++)
    {
      for (int x = 0; x < font.Height; x++)
      {
        colors.Add(canvas.GetPixel(x, y).ToArgb());
      }
    }

    // GDI's monochrome fallback would only ever produce black, white, and gray anti-alias blends.
    Assert.Contains(
      colors.Select(Color.FromArgb),
      c => Math.Abs(c.R - c.G) > 40 || Math.Abs(c.G - c.B) > 40
    );
  }
}
