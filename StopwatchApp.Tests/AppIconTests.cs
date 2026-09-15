namespace StopwatchApp.Tests;

/// <summary>
/// Covers the embedded <c>Assets/app.ico</c> resource (AGENTS.md §6/§10.3) — a cheap guard against a
/// truncated or accidentally single-size ICO being committed, which would still load via
/// <see cref="MainForm"/>'s <c>LoadAppIcon</c> but render badly at taskbar/title-bar size. Parses the
/// ICONDIR/ICONDIRENTRY header directly rather than
/// going through <see cref="Icon"/>, whose size-matching constructors silently rescale to the
/// requested size instead of reporting what is actually present.
/// </summary>
public sealed class AppIconTests
{
  private const string ResourceName = "StopwatchApp.Assets.app.ico";

  [Fact]
  public void AppIcon_EmbeddedResourceExists()
  {
    using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(ResourceName);

    Assert.NotNull(stream);
  }

  [Theory]
  [InlineData(16)]
  [InlineData(32)]
  [InlineData(256)]
  public void AppIcon_ContainsSizeVariant(int size)
  {
    List<(int Width, int Height)> declaredSizes = ReadDeclaredIconSizes();

    Assert.Contains(declaredSizes, entry => entry.Width == size && entry.Height == size);
  }

  [Fact]
  public void AppIcon_LoadsAsValidMultiSizeIcon()
  {
    using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(ResourceName);
    Assert.NotNull(stream);

    using Icon icon = new(stream);

    Assert.True(icon.Width > 0);
    Assert.True(icon.Height > 0);
  }

  /// <summary>
  /// Reads the ICONDIR header and each ICONDIRENTRY's declared width/height, without going through
  /// <see cref="Icon"/>'s fuzzy size matching. Byte value <c>0</c> encodes <c>256</c> per the ICO
  /// format (a single byte cannot hold 256 directly).
  /// </summary>
  private static List<(int Width, int Height)> ReadDeclaredIconSizes()
  {
    using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(ResourceName);
    Assert.NotNull(stream);
    using BinaryReader reader = new(stream);

    reader.ReadInt16(); // reserved
    reader.ReadInt16(); // image type
    short imageCount = reader.ReadInt16();

    List<(int Width, int Height)> sizes = new(imageCount);
    for (int i = 0; i < imageCount; i++)
    {
      int width = reader.ReadByte();
      int height = reader.ReadByte();
      reader.ReadByte(); // color palette count
      reader.ReadByte(); // reserved
      reader.ReadInt16(); // color planes
      reader.ReadInt16(); // bits per pixel
      reader.ReadInt32(); // data size
      reader.ReadInt32(); // data offset
      sizes.Add((width == 0 ? 256 : width, height == 0 ? 256 : height));
    }

    return sizes;
  }
}
