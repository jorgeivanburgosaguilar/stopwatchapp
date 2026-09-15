using System.Drawing.Drawing2D;

namespace StopwatchApp.Theme;

/// <summary>
/// Builds the rounded-rectangle <see cref="GraphicsPath"/> shared by every owner-drawn surface this
/// app paints (buttons, the stopwatch card, list rows, dialog buttons) so the corner-radius math
/// lives in exactly one place (AGENTS.md §11). Internal — not part of §3.1's fixed
/// contracts, so it carries no <c>GenerateDocumentationFile</c> obligation, but is documented anyway
/// to match the project's style for internal helpers.
/// </summary>
internal static class RoundedRectangle
{
  /// <summary>
  /// Builds a closed rounded-rectangle path for <paramref name="bounds"/> with corner radius
  /// <paramref name="radius"/>. When <paramref name="radius"/> is zero or negative, or the rectangle
  /// is too small to fit the requested radius, falls back to a plain rectangle path.
  /// </summary>
  /// <param name="bounds">The rectangle to round.</param>
  /// <param name="radius">The corner radius, in pixels.</param>
  internal static GraphicsPath Path(Rectangle bounds, int radius)
  {
    GraphicsPath path = new();
    int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
    if (diameter <= 0)
    {
      path.AddRectangle(bounds);
      return path;
    }

    Rectangle corner = new(bounds.Location, new Size(diameter, diameter));
    path.AddArc(corner, 180, 90);
    corner.X = bounds.Right - diameter;
    path.AddArc(corner, 270, 90);
    corner.Y = bounds.Bottom - diameter;
    path.AddArc(corner, 0, 90);
    corner.X = bounds.Left;
    path.AddArc(corner, 90, 90);
    path.CloseFigure();
    return path;
  }
}
