namespace StopwatchApp.Theme;

/// <summary>
/// Light/dark color tables for surfaces the OS theme (<see cref="Application.SetColorMode"/>)
/// does not reach — anything drawn manually rather than a stock control. Compare <see cref="Color"/>
/// values via <see cref="Color.ToArgb"/>, never <c>==</c>.
/// </summary>
public static class Palette
{
  /// <summary>Extra-small spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingXs = 4;

  /// <summary>Small spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingSm = 8;

  /// <summary>Medium spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingMd = 12;

  /// <summary>Large spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingLg = 16;

  /// <summary>Extra-large spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingXl = 24;

  /// <summary>2x-extra-large spacing unit, in pixels (AGENTS.md §11).</summary>
  public const int SpacingXxl = 32;

  /// <summary>Corner radius for controls such as buttons and chips, in pixels (AGENTS.md §11).</summary>
  public const int ControlCornerRadius = 6;

  /// <summary>Corner radius for cards/panels, in pixels (AGENTS.md §11).</summary>
  public const int CardCornerRadius = 8;

  /// <summary>
  /// Corner radius for dialogs, in pixels (AGENTS.md §11). Informational only: on Windows 11 the
  /// DWM already rounds top-level window frames by default, so <see cref="Controls.ClearRecordsDialog"/>
  /// does not render this radius itself — see AGENTS.md §11.
  /// </summary>
  public const int DialogCornerRadius = 12;

  /// <summary>
  /// Gets the resting-elevation shadow tint used as a thin owner-drawn border/outline on
  /// custom-painted cards and controls — the low-risk GDI+ translation of the comp's faint resting
  /// shadow (approximately <c>rgba(15,23,42,.07)</c> light / <c>rgba(0,0,0,.5)</c> dark). See
  /// AGENTS.md §11 for the owner-drawn elevation treatment.
  /// </summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color ShadowResting(bool dark) =>
    dark ? Color.FromArgb(128, 0x00, 0x00, 0x00) : Color.FromArgb(18, 0x0F, 0x17, 0x2A);

  /// <summary>
  /// Gets the hover/raised-elevation shadow tint (approximately <c>rgba(15,23,42,.14)</c> light /
  /// <c>rgba(0,0,0,.55)</c> dark) — a bit more pronounced than <see cref="ShadowResting"/>.
  /// </summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color ShadowHover(bool dark) =>
    dark ? Color.FromArgb(140, 0x00, 0x00, 0x00) : Color.FromArgb(36, 0x0F, 0x17, 0x2A);

  /// <summary>
  /// Gets the dark-mode "mica-style" 1px lighter top-edge highlight drawn on a hovered/pressed
  /// owner-drawn surface. Light mode has no equivalent comp token, so callers only use this while
  /// dark mode is active.
  /// </summary>
  public static Color TopEdgeHighlight { get; } = Color.FromArgb(20, 0xFF, 0xFF, 0xFF);

  /// <summary>Gets the primary text color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color Text(bool dark) =>
    dark ? Color.FromArgb(0xF3, 0xF4, 0xF6) : Color.FromArgb(0x11, 0x18, 0x27);

  /// <summary>Gets the muted/secondary text color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color MutedText(bool dark) =>
    dark ? Color.FromArgb(0x9C, 0xA3, 0xAF) : Color.FromArgb(0x6B, 0x72, 0x80);

  /// <summary>Gets the card/panel background color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color CardBackground(bool dark) =>
    dark ? Color.FromArgb(0x1F, 0x29, 0x37) : Color.FromArgb(0xF9, 0xFA, 0xFB);

  /// <summary>Gets the row background color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color RowBackground(bool dark) =>
    dark ? Color.FromArgb(0x11, 0x18, 0x27) : Color.FromArgb(0xFF, 0xFF, 0xFF);

  /// <summary>Gets the border color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color Border(bool dark) =>
    dark ? Color.FromArgb(0x37, 0x41, 0x51) : Color.FromArgb(0xE5, 0xE7, 0xEB);

  /// <summary>Gets the accent color (links, durations) for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color Accent(bool dark) =>
    dark ? Color.FromArgb(0x60, 0xA5, 0xFA) : Color.FromArgb(0x25, 0x63, 0xEB);

  /// <summary>Gets the empty-state text color for the given theme.</summary>
  /// <param name="dark">Whether dark mode is active.</param>
  public static Color EmptyStateText(bool dark) =>
    dark ? Color.FromArgb(0x6B, 0x72, 0x80) : Color.FromArgb(0x9C, 0xA3, 0xAF);

  /// <summary>Gets the Start/Continue button's base, hover, and pressed colors (green; same in both themes).</summary>
  public static (Color Base, Color Hover, Color Pressed) StartButton { get; } =
    (
      Color.FromArgb(0x16, 0xA3, 0x4A),
      Color.FromArgb(0x15, 0x80, 0x3D),
      Color.FromArgb(0x16, 0x65, 0x34)
    );

  /// <summary>Gets the Pause button's base, hover, and pressed colors (yellow; same in both themes).</summary>
  public static (Color Base, Color Hover, Color Pressed) PauseButton { get; } =
    (
      Color.FromArgb(0xCA, 0x8A, 0x04),
      Color.FromArgb(0xA1, 0x62, 0x07),
      Color.FromArgb(0x85, 0x4D, 0x0E)
    );

  /// <summary>Gets the Lap button's base, hover, and pressed colors (blue; same in both themes).</summary>
  public static (Color Base, Color Hover, Color Pressed) LapButton { get; } =
    (
      Color.FromArgb(0x25, 0x63, 0xEB),
      Color.FromArgb(0x1D, 0x4E, 0xD8),
      Color.FromArgb(0x1E, 0x40, 0xAF)
    );

  /// <summary>Gets the Stop button's base, hover, and pressed colors (red; same in both themes).</summary>
  public static (Color Base, Color Hover, Color Pressed) StopButton { get; } =
    (
      Color.FromArgb(0xDC, 0x26, 0x26),
      Color.FromArgb(0xB9, 0x1C, 0x1C),
      Color.FromArgb(0x99, 0x1B, 0x1B)
    );

  /// <summary>
  /// Gets the Cancel/dismiss button's base, hover, and pressed colors (dark slate; same in both
  /// themes) — used for a dialog's non-destructive action, paired against a destructive action in
  /// <see cref="StopButton"/> red (AGENTS.md §8.5/§11).
  /// </summary>
  public static (Color Base, Color Hover, Color Pressed) CancelButton { get; } =
    (
      Color.FromArgb(0x33, 0x41, 0x55),
      Color.FromArgb(0x1E, 0x29, 0x3B),
      Color.FromArgb(0x0F, 0x17, 0x2A)
    );
}
