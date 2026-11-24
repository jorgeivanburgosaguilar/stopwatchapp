using System.Runtime.InteropServices;
using Microsoft.Win32;
using StopwatchApp.Models;

namespace StopwatchApp.Services;

/// <summary>
/// Detects that the user has stepped away (AGENTS.md §10.7): the workstation locks, the display
/// turns off, or — through <see cref="IsScreenSaverRunning"/> — the screen saver starts. Lock and
/// unlock arrive through <see cref="SystemEvents.SessionSwitch"/>; the display state arrives as a
/// <c>WM_POWERBROADCAST</c> message on the window passed to
/// <see cref="RegisterDisplayNotifications"/>. The callbacks may run on any thread.
/// </summary>
public sealed partial class AwayMonitor : IDisposable
{
  /// <summary>The <c>WM_POWERBROADCAST</c> window message the owner forwards to <see cref="HandleMessage"/>.</summary>
  public const int WmPowerBroadcast = 0x0218;

  private const int PbtPowerSettingChange = 0x8013;
  private const int DisplayOff = 0;
  private const int DisplayOn = 1;
  private const uint DeviceNotifyWindowHandle = 0;
  private const uint SpiGetScreenSaverRunning = 0x0072;

  // GUID_CONSOLE_DISPLAY_STATE: Data is 0 (off), 1 (on) or 2 (dimmed).
  private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

  private readonly Action<AwayReason> _onAway;
  private readonly Action _onReturn;
  private IntPtr _displayRegistration;
  private volatile bool _sessionLocked;

  /// <summary>
  /// Initializes a new instance of the <see cref="AwayMonitor"/> class and starts listening for
  /// session lock and unlock.
  /// </summary>
  /// <param name="onAway">Called when the user goes away, with the reason; may run on any thread.</param>
  /// <param name="onReturn">Called when the user may be back; may run on any thread.</param>
  public AwayMonitor(Action<AwayReason> onAway, Action onReturn)
  {
    _onAway = onAway;
    _onReturn = onReturn;
    SystemEvents.SessionSwitch += OnSessionSwitch;
  }

  /// <summary>Gets whether the interactive session is currently locked or switched away.</summary>
  public bool IsSessionLocked => _sessionLocked;

  /// <summary>
  /// Asks Windows to send display on/off notifications to <paramref name="hwnd"/>. A window handle
  /// can be recreated (toggling <c>ShowInTaskbar</c> does it), so the owner calls this on every
  /// handle creation after <see cref="UnregisterDisplayNotifications"/> on destruction. A failed
  /// registration is deliberately ignored: the feature then degrades to lock and screen-saver
  /// detection only.
  /// </summary>
  /// <param name="hwnd">The window that will receive the messages.</param>
  public void RegisterDisplayNotifications(IntPtr hwnd)
  {
    UnregisterDisplayNotifications();
    _displayRegistration = RegisterPowerSettingNotification(
      hwnd,
      in ConsoleDisplayState,
      DeviceNotifyWindowHandle
    );
  }

  /// <summary>Stops display notifications for the previously registered window handle.</summary>
  public void UnregisterDisplayNotifications()
  {
    if (_displayRegistration != IntPtr.Zero)
    {
      _ = UnregisterPowerSettingNotification(_displayRegistration);
      _displayRegistration = IntPtr.Zero;
    }
  }

  /// <summary>
  /// Handles a forwarded <see cref="WmPowerBroadcast"/> message; messages that are not a console
  /// display change are ignored. Display-on only counts as a return while the session is unlocked,
  /// so waking the monitor to the lock screen does not pop the window up.
  /// </summary>
  /// <param name="wParam">The message's <c>WParam</c>.</param>
  /// <param name="lParam">The message's <c>LParam</c>.</param>
  public void HandleMessage(IntPtr wParam, IntPtr lParam)
  {
    switch (ReadDisplayState(wParam, lParam))
    {
      case DisplayOff:
        _onAway(AwayReason.DisplayOff);
        break;
      case DisplayOn when !_sessionLocked:
        _onReturn();
        break;
    }
  }

  /// <summary>Reports whether a screen saver is running on the interactive window station.</summary>
  /// <returns><see langword="true"/> if the screen saver is running.</returns>
  public static bool IsScreenSaverRunning()
  {
    int running = 0;
    return SystemParametersInfo(SpiGetScreenSaverRunning, 0, ref running, 0) && running != 0;
  }

  /// <inheritdoc />
  public void Dispose()
  {
    // SystemEvents is a static, process-wide event source: a missed unsubscribe would keep this
    // monitor (and its callbacks' owner) alive past disposal (AGENTS.md §5).
    SystemEvents.SessionSwitch -= OnSessionSwitch;
    UnregisterDisplayNotifications();
  }

  /// <summary>What a session-switch reason means for the stopwatch.</summary>
  public enum Signal
  {
    /// <summary>The reason does not matter to the stopwatch.</summary>
    None,

    /// <summary>The user is away.</summary>
    Away,

    /// <summary>The user may be back.</summary>
    Return,
  }

  /// <summary>Classifies a session-switch reason (AGENTS.md §10.7).</summary>
  /// <param name="reason">The reason Windows reported.</param>
  /// <returns>Whether it means the user left, returned, or neither.</returns>
  internal static Signal Classify(SessionSwitchReason reason) =>
    reason switch
    {
      SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect => Signal.Away,
      SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect => Signal.Return,
      _ => Signal.None,
    };

  /// <summary>
  /// Reads the display state from a <c>PBT_POWERSETTINGCHANGE</c> message: <c>0</c> off, <c>1</c>
  /// on, <c>2</c> dimmed, or <see langword="null"/> for any other message or power setting.
  /// </summary>
  /// <param name="wParam">The message's <c>WParam</c>.</param>
  /// <param name="lParam">The message's <c>LParam</c>, a pointer to a <c>POWERBROADCAST_SETTING</c>.</param>
  /// <returns>The display state, or <see langword="null"/>.</returns>
  internal static int? ReadDisplayState(IntPtr wParam, IntPtr lParam)
  {
    // POWERBROADCAST_SETTING: GUID PowerSetting (16 bytes), DWORD DataLength (4), then Data.
    const int dataLengthOffset = 16;
    const int dataOffset = 20;
    if (wParam != PbtPowerSettingChange || lParam == IntPtr.Zero)
    {
      return null;
    }

    byte[] guidBytes = new byte[16];
    Marshal.Copy(lParam, guidBytes, 0, guidBytes.Length);
    if (new Guid(guidBytes) != ConsoleDisplayState)
    {
      return null;
    }

    return Marshal.ReadInt32(lParam, dataLengthOffset) >= sizeof(int)
      ? Marshal.ReadInt32(lParam, dataOffset)
      : null;
  }

  private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
  {
    switch (Classify(e.Reason))
    {
      case Signal.Away:
        _sessionLocked = true;
        _onAway(AwayReason.Locked);
        break;
      case Signal.Return:
        _sessionLocked = false;
        _onReturn();
        break;
    }
  }

  [LibraryImport("user32.dll", SetLastError = true)]
  private static partial IntPtr RegisterPowerSettingNotification(
    IntPtr hRecipient,
    in Guid powerSettingGuid,
    uint flags
  );

  [LibraryImport("user32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool UnregisterPowerSettingNotification(IntPtr handle);

  [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool SystemParametersInfo(
    uint action,
    uint param,
    ref int value,
    uint winIni
  );
}
