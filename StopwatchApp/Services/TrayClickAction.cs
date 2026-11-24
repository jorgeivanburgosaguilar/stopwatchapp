namespace StopwatchApp.Services;

/// <summary>What a single left-click on the tray icon does to the main window (AGENTS.md §10.2).</summary>
public enum TrayClickAction
{
  /// <summary>The window is hidden in the tray: restore, center, and activate it.</summary>
  Open,

  /// <summary>The window is open but not in front (or a modal blocks it): activate it only.</summary>
  BringToFront,

  /// <summary>The window is open and in front: hide it back to the tray.</summary>
  Hide,
}
