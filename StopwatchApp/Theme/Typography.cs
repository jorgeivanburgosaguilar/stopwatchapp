using System.Drawing.Text;

namespace StopwatchApp.Theme;

/// <summary>
/// The app's type scale (S14, AGENTS.md §17): three roles — display, body, and caption — each a
/// fixed point size, plus the monospace family resolution shared by the elapsed-time display and
/// the records/laps rows. Point sizes are chosen so the rendered pixel size (at the standard 96 dpi
/// design baseline, where <c>1pt = 4/3px</c>) matches the sizes specified for this app: 96px display,
/// 16px body, 12px caption. Actual on-screen pixels still scale with the OS DPI setting via
/// <see cref="AutoScaleMode.Dpi"/> (<c>MainForm</c>), as is correct for a WinForms point-sized font.
/// </summary>
public static class Typography
{
  /// <summary>The elapsed-time display's point size — 72pt, 96px at 96 dpi.</summary>
  public const float DisplayPointSize = 72f;

  /// <summary>The body text point size (buttons, headers, dialogs, list rows) — 12pt, 16px at 96 dpi.</summary>
  public const float BodyPointSize = 12f;

  /// <summary>The caption point size (the version footer) — 9pt, 12px at 96 dpi.</summary>
  public const float CaptionPointSize = 9f;

  /// <summary>
  /// Gets the name of the installed monospace, tabular-figure font family used for the elapsed-time
  /// display and the records/laps rows (AGENTS.md §8.5): <c>Cascadia Mono</c> when installed (ships
  /// with Windows 11), else <c>Consolas</c> (ships with every Windows since Vista). Resolved once per
  /// process — the underlying <see cref="InstalledFontCollection"/> probe is not free enough to repeat
  /// per control.
  /// </summary>
  public static string MonospaceFamilyName { get; } = ResolveMonospaceFamilyName();

  /// <summary>
  /// Creates the elapsed-time display font: <see cref="MonospaceFamilyName"/>, bold, at
  /// <see cref="DisplayPointSize"/>. The caller owns the returned <see cref="Font"/> and must dispose
  /// it.
  /// </summary>
  public static Font CreateDisplayFont() =>
    new(MonospaceFamilyName, DisplayPointSize, FontStyle.Bold);

  /// <summary>
  /// Creates the body font: the OS message-box UI face at <see cref="BodyPointSize"/>, so it follows
  /// whatever font the user's Windows theme specifies rather than hard-coding "Segoe UI". The caller
  /// owns the returned <see cref="Font"/> and must dispose it.
  /// </summary>
  public static Font CreateBodyFont() =>
    new(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, BodyPointSize);

  /// <summary>
  /// Creates the monospace body font used for records/laps rows: <see cref="MonospaceFamilyName"/> at
  /// <see cref="BodyPointSize"/>, so dates and times column-align down the list. The caller owns the
  /// returned <see cref="Font"/> and must dispose it.
  /// </summary>
  public static Font CreateMonospaceBodyFont() => new(MonospaceFamilyName, BodyPointSize);

  /// <summary>
  /// Creates the caption font used for the version footer: the OS message-box UI face at
  /// <see cref="CaptionPointSize"/>. The caller owns the returned <see cref="Font"/> and must dispose
  /// it.
  /// </summary>
  public static Font CreateCaptionFont() =>
    new(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, CaptionPointSize);

  private static string ResolveMonospaceFamilyName()
  {
    using InstalledFontCollection installed = new();
    bool hasCascadiaMono = installed.Families.Any(family =>
      string.Equals(family.Name, "Cascadia Mono", StringComparison.Ordinal)
    );
    return hasCascadiaMono ? "Cascadia Mono" : "Consolas";
  }
}
