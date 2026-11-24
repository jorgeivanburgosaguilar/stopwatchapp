namespace StopwatchApp.Models;

/// <summary>Why a running session was paused automatically because the user is away.</summary>
public enum AwayReason
{
  /// <summary>The workstation was locked (Win+L, Dynamic Lock, user switch).</summary>
  Locked,

  /// <summary>The screen saver started.</summary>
  ScreenSaver,

  /// <summary>The display turned off.</summary>
  DisplayOff,
}
