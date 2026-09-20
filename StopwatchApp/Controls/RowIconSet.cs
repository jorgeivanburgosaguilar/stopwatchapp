using System.Drawing.Drawing2D;
using System.Globalization;

namespace StopwatchApp.Controls;

/// <summary>
/// Owns the decoded color emoji bitmaps embedded from <c>Assets/emoji</c> (AGENTS.md §8.5/§17).
/// An instance per owner, not a shared static cache: AGENTS.md §5 forbids static mutable state, and
/// the bitmaps hold native GDI+ memory that must be released with the control that drew them.
/// </summary>
internal sealed class RowIconSet : IDisposable
{
  private readonly Dictionary<RowIcon, Bitmap> _bitmaps = [];
  private readonly Dictionary<(RowIcon Icon, int Size), Bitmap> _scaled = [];

  /// <summary>
  /// Initializes a new instance of the <see cref="RowIconSet"/> class, decoding every icon.
  /// </summary>
  /// <exception cref="InvalidOperationException">An embedded icon resource is missing.</exception>
  public RowIconSet()
  {
    try
    {
      foreach (RowIcon icon in Enum.GetValues<RowIcon>())
      {
        _bitmaps[icon] = Load(icon);
      }
    }
    catch
    {
      Dispose();
      throw;
    }
  }

  /// <summary>Gets the manifest resource name of <paramref name="icon"/>'s embedded PNG.</summary>
  /// <param name="icon">The icon.</param>
  internal static string ResourceName(RowIcon icon) =>
    string.Create(
      CultureInfo.InvariantCulture,
      $"StopwatchApp.Assets.emoji.{icon.ToString().ToLowerInvariant()}.png"
    );

  /// <summary>Gets the decoded bitmap for <paramref name="icon"/>. Owned by this set.</summary>
  /// <param name="icon">The icon.</param>
  internal Bitmap Get(RowIcon icon) => _bitmaps[icon];

  /// <summary>
  /// Gets <paramref name="icon"/> pre-scaled to a <paramref name="size"/>-pixel square, so painting a
  /// row is a plain 1:1 blit. Downscaling the 128 px source with high-quality interpolation on every
  /// paint, for every icon of every row, was what made repainting the records window slow. Scaled
  /// once per size and cached; owned by this set.
  /// </summary>
  /// <param name="icon">The icon.</param>
  /// <param name="size">The target width and height in pixels.</param>
  internal Bitmap GetScaled(RowIcon icon, int size)
  {
    int clamped = Math.Max(1, size);
    if (_scaled.TryGetValue((icon, clamped), out Bitmap? cached))
    {
      return cached;
    }

    Bitmap scaled = new(clamped, clamped);
    using (Graphics graphics = Graphics.FromImage(scaled))
    {
      graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
      graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
      graphics.CompositingQuality = CompositingQuality.HighQuality;
      graphics.DrawImage(Get(icon), new Rectangle(0, 0, clamped, clamped));
    }
    _scaled[(icon, clamped)] = scaled;
    return scaled;
  }

  /// <inheritdoc />
  public void Dispose()
  {
    foreach (Bitmap bitmap in _bitmaps.Values)
    {
      bitmap.Dispose();
    }
    _bitmaps.Clear();
    foreach (Bitmap bitmap in _scaled.Values)
    {
      bitmap.Dispose();
    }
    _scaled.Clear();
  }

  private static Bitmap Load(RowIcon icon)
  {
    string name = ResourceName(icon);
    using Stream stream =
      typeof(RowIconSet).Assembly.GetManifestResourceStream(name)
      ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
    using Bitmap decoded = new(stream);
    // A Bitmap decoded from a stream needs that stream for its whole lifetime; copying it detaches
    // the pixels so the stream can be disposed here.
    return new Bitmap(decoded);
  }
}
