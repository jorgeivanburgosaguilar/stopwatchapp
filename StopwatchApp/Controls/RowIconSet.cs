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

  /// <inheritdoc />
  public void Dispose()
  {
    foreach (Bitmap bitmap in _bitmaps.Values)
    {
      bitmap.Dispose();
    }
    _bitmaps.Clear();
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
