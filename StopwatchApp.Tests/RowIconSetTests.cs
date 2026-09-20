using StopwatchApp.Controls;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers the embedded color emoji PNGs (AGENTS.md §8.5) — a guard against a missing or unreadable
/// <c>Assets/emoji</c> file, which would otherwise only surface as a crash when a row first paints.
/// </summary>
public sealed class RowIconSetTests
{
  [Theory]
  [InlineData(RowIcon.Calendar)]
  [InlineData(RowIcon.Stopwatch)]
  [InlineData(RowIcon.Hourglass)]
  public void EmbeddedResource_Exists(RowIcon icon)
  {
    using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(
      RowIconSet.ResourceName(icon)
    );

    Assert.NotNull(stream);
  }

  [Fact]
  public void Constructor_DecodesEveryIconToANonEmptyBitmap()
  {
    using RowIconSet icons = new();

    foreach (RowIcon icon in Enum.GetValues<RowIcon>())
    {
      Bitmap bitmap = icons.Get(icon);
      Assert.True(bitmap.Width > 0 && bitmap.Height > 0, $"{icon} decoded to an empty bitmap.");
    }
  }

  [Fact]
  public void Icons_AreColorNotMonochrome()
  {
    using RowIconSet icons = new();

    foreach (RowIcon icon in Enum.GetValues<RowIcon>())
    {
      Bitmap bitmap = icons.Get(icon);
      HashSet<int> opaqueColors = [];
      for (int y = 0; y < bitmap.Height; y += 4)
      {
        for (int x = 0; x < bitmap.Width; x += 4)
        {
          Color pixel = bitmap.GetPixel(x, y);
          if (pixel.A == 255)
          {
            opaqueColors.Add(pixel.ToArgb());
          }
        }
      }

      Assert.True(opaqueColors.Count > 8, $"{icon} has too few colors to be a color emoji.");
    }
  }
}
