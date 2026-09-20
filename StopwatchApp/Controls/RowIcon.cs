namespace StopwatchApp.Controls;

/// <summary>
/// The three emoji AGENTS.md §8.5's row templates use, each drawn from an embedded color PNG
/// (<see cref="RowIconSet"/>) because GDI text rendering cannot paint color emoji glyphs.
/// </summary>
public enum RowIcon
{
  /// <summary><c>📅</c> (U+1F4C5) — the date.</summary>
  Calendar,

  /// <summary><c>⏱</c> (U+23F1) — the start/end times.</summary>
  Stopwatch,

  /// <summary><c>⏳</c> (U+23F3) — the duration or lap.</summary>
  Hourglass,
}
