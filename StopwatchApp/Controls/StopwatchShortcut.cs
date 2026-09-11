namespace StopwatchApp.Controls;

/// <summary>
/// A window-scoped keyboard shortcut recognized by <see cref="StopwatchControl.MapShortcut"/>. See
/// AGENTS.md §10.4 — these are ordinary keystrokes handled while the main window has focus, not
/// global (system-wide) hotkeys.
/// </summary>
public enum StopwatchShortcut
{
  /// <summary>Start, pause, or continue — whatever the primary button currently does.</summary>
  Toggle,

  /// <summary>Record a lap.</summary>
  Lap,

  /// <summary>Stop the current session.</summary>
  Stop,
}
