using System.Drawing.Imaging;
using Svg;

namespace IconGen;

/// <summary>
/// Generates <c>StopwatchApp/Assets/app.ico</c> (S14, AGENTS.md §17) from the vendored Fluent System
/// Icons "Timer" (filled) SVG — Microsoft's own stopwatch-shaped glyph, MIT-licensed (see
/// <c>vendor/fluent-timer/NOTICE.md</c>) — recolored from its shipped <c>#212121</c> to the app's own
/// accent, <c>Palette.LapButton.Base</c> (<c>#2563EB</c>), and rasterized at every size Explorer and
/// the Windows 11 taskbar actually request. A one-off tool, not part of the shipped app; see the
/// accompanying .csproj comment for why it is excluded from <c>StopwatchApp.slnx</c>.
/// </summary>
internal static class Program
{
  // Palette.LapButton.Base (#2563EB) — duplicated here, not referenced, because this console tool
  // intentionally has no dependency on StopwatchApp itself.
  private static readonly Color AccentColor = Color.FromArgb(0x25, 0x63, 0xEB);

  private static readonly string VendorDirectory = Path.Combine(
    AppContext.BaseDirectory,
    "vendor",
    "fluent-timer"
  );

  // Every icon size Explorer (small/medium/large/extra-large icon views), the taskbar, and Alt+Tab
  // actually request on Windows 11, paired with the vendored Fluent source closest at or above that
  // size. A vector shape scales up cleanly, but Microsoft's own smaller-size variants are
  // simplified/re-hinted by hand for legibility and must not be downscaled from a larger one — so
  // 40 renders from the 32 source (a mild upscale) rather than downscaling the 48 source.
  private static readonly (int TargetSize, int SourceSize)[] IconSources =
  [
    (16, 16),
    (20, 20),
    (24, 24),
    (32, 32),
    (40, 32),
    (48, 48),
    (64, 48),
    (128, 48),
    (256, 48),
  ];

  private static int Main(string[] args)
  {
    if (args.Length != 1)
    {
      Console.Error.WriteLine("Usage: IconGen <output .ico path>");
      return 1;
    }

    string outputPath = args[0];
    string? outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
      Directory.CreateDirectory(outputDirectory);
    }

    List<Bitmap> bitmaps = IconSources
      .Select(entry => RenderRecolored(entry.SourceSize, entry.TargetSize))
      .ToList();
    try
    {
      WriteIcoFile(outputPath, bitmaps);
    }
    finally
    {
      foreach (Bitmap bitmap in bitmaps)
      {
        bitmap.Dispose();
      }
    }

    Console.WriteLine(
      $"Wrote {outputPath} ({string.Join(", ", IconSources.Select(e => e.TargetSize))}px)."
    );
    return 0;
  }

  /// <summary>
  /// Loads the vendored Fluent "Timer" filled SVG at <paramref name="sourceSize"/>, recolors every
  /// paintable node from its shipped <c>#212121</c> to <see cref="AccentColor"/>, and rasterizes it
  /// at <paramref name="targetSize"/>×<paramref name="targetSize"/> pixels.
  /// </summary>
  private static Bitmap RenderRecolored(int sourceSize, int targetSize)
  {
    string svgPath = Path.Combine(VendorDirectory, $"ic_fluent_timer_{sourceSize}_filled.svg");
    SvgDocument document =
      SvgDocument.Open<SvgDocument>(svgPath)
      ?? throw new InvalidOperationException($"Failed to parse \"{svgPath}\".");
    RecolorTree(document.Descendants(), new SvgColourServer(AccentColor));
    return document.Draw(targetSize, targetSize);
  }

  private static void RecolorTree(IEnumerable<SvgElement> nodes, SvgPaintServer color)
  {
    foreach (SvgElement node in nodes)
    {
      if (node.Fill is not null && node.Fill != SvgPaintServer.None)
      {
        node.Fill = color;
      }

      if (node.Stroke is not null && node.Stroke != SvgPaintServer.None)
      {
        node.Stroke = color;
      }

      RecolorTree(node.Descendants(), color);
    }
  }

  /// <summary>
  /// Writes a multi-image .ico container with each bitmap PNG-encoded — supported at every size on
  /// Windows Vista and later (including 16×16), and read back correctly by
  /// <see cref="System.Drawing.Icon"/>. Simpler and more robust than hand-rolling the classic
  /// uncompressed BMP/AND-mask DIB format the ICO spec also allows.
  /// </summary>
  private static void WriteIcoFile(string path, IReadOnlyList<Bitmap> bitmaps)
  {
    List<byte[]> pngEntries = bitmaps
      .Select(bitmap =>
      {
        using MemoryStream memoryStream = new();
        bitmap.Save(memoryStream, ImageFormat.Png);
        return memoryStream.ToArray();
      })
      .ToList();

    using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
    using BinaryWriter writer = new(fileStream);

    // ICONDIR header.
    writer.Write((short)0); // reserved, must be 0
    writer.Write((short)1); // image type: 1 = icon
    writer.Write((short)bitmaps.Count);

    int offset = 6 + (16 * bitmaps.Count); // header + one ICONDIRENTRY per image
    for (int i = 0; i < bitmaps.Count; i++)
    {
      Bitmap bitmap = bitmaps[i];
      byte[] data = pngEntries[i];
      // Width/height 0 is the ICO format's encoding for 256 (a byte cannot hold 256 directly).
      writer.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
      writer.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
      writer.Write((byte)0); // color palette count (0 = no palette, true color)
      writer.Write((byte)0); // reserved, must be 0
      writer.Write((short)1); // color planes
      writer.Write((short)32); // bits per pixel
      writer.Write(data.Length);
      writer.Write(offset);
      offset += data.Length;
    }

    foreach (byte[] data in pngEntries)
    {
      writer.Write(data);
    }
  }
}
