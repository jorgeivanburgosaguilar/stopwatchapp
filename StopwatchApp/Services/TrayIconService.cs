using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using StopwatchApp.Controls;
using StopwatchApp.Formatting;
using StopwatchApp.Theme;

namespace StopwatchApp.Services;

/// <summary>
/// Owns the <see cref="NotifyIcon"/>: a GDI+-rendered 32×32 icon showing hours over minutes, its
/// tooltip, and its context menu (<c>Open</c>, the state-appropriate transition(s), <c>Exit</c>).
/// See AGENTS.md §10.1/§10.2. Takes callbacks rather than a <c>MainForm</c> reference — parent/child
/// communication happens through delegates, never a shared mutable reference (AGENTS.md §3).
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
  // The index in _contextMenu.Items where the state-dependent action item(s) live, between the
  // "Open" separator and the "Exit" separator. Fixed by the skeleton BuildBaseMenu constructs.
  private const int StateSectionIndex = 2;

  private readonly StopwatchControl _control;
  private readonly Action _onOpen;
  private readonly Action _onExit;
  private readonly NotifyIcon _notifyIcon;
  private readonly ContextMenuStrip _contextMenu;
  private int _stateItemCount;
  private bool _darkMode;
  private IntPtr _currentIconHandle;
  private Icon? _currentIcon;
  private (int Hours, int Minutes, TrayState State) _lastRendered = (-1, -1, TrayState.Idle);

  /// <summary>
  /// Initializes a new instance of the <see cref="TrayIconService"/> class and makes the tray icon
  /// visible immediately, showing the idle state.
  /// </summary>
  /// <param name="control">
  /// The stopwatch control the tray menu's transition items act on and whose state
  /// <see cref="UpdateDisplay"/> renders.
  /// </param>
  /// <param name="onOpen">Invoked when the user opens the tray icon (double-click or the <c>Open</c> menu item).</param>
  /// <param name="onExit">Invoked when the user selects the tray menu's <c>Exit</c> item.</param>
  public TrayIconService(StopwatchControl control, Action onOpen, Action onExit)
  {
    _control = control;
    _onOpen = onOpen;
    _onExit = onExit;

    _contextMenu = BuildBaseMenu();
    _notifyIcon = new NotifyIcon { ContextMenuStrip = _contextMenu, Visible = true };
    _notifyIcon.DoubleClick += (_, _) => _onOpen();

    RebuildStateMenuItems(TrayState.Idle);
    UpdateDisplay(elapsedMs: 0, running: false, paused: false);
  }

  /// <summary>
  /// Gets or sets whether the tray icon renders its state tint from the dark-mode palette. Defaults
  /// to <see langword="false"/>; wiring this to the live OS setting is S12's job (AGENTS.md §11/§17),
  /// matching the precedent set by <see cref="StopwatchControl.DarkMode"/>.
  /// </summary>
  public bool DarkMode
  {
    get => _darkMode;
    set
    {
      if (_darkMode == value)
      {
        return;
      }

      _darkMode = value;
      RenderIcon(_lastRendered.Hours, _lastRendered.Minutes, _lastRendered.State);
    }
  }

  /// <summary>
  /// Refreshes the tooltip and, at most once per second and only when the displayed hour/minute
  /// actually changes, the rendered icon and the state-dependent menu items (AGENTS.md §10.1).
  /// </summary>
  /// <param name="elapsedMs">The current elapsed time, in milliseconds.</param>
  /// <param name="running">Whether the stopwatch is currently running.</param>
  /// <param name="paused">Whether the stopwatch is currently paused.</param>
  public void UpdateDisplay(long elapsedMs, bool running, bool paused)
  {
    // The tooltip carries the authoritative, unrounded HH:MM:SS value and updates every call —
    // only the once-per-second icon bitmap and menu are throttled below.
    _notifyIcon.Text = TimeFormat.FormatTime(elapsedMs);

    long totalMinutes = elapsedMs / 60000;
    int hours = (int)(totalMinutes / 60);
    int minutes = (int)(totalMinutes % 60);
    TrayState state =
      running ? TrayState.Running
      : paused ? TrayState.Paused
      : TrayState.Idle;
    (int Hours, int Minutes, TrayState State) rendered = (hours, minutes, state);
    if (rendered == _lastRendered)
    {
      return;
    }

    bool stateChanged = state != _lastRendered.State;
    _lastRendered = rendered;
    RenderIcon(hours, minutes, state);
    if (stateChanged)
    {
      RebuildStateMenuItems(state);
    }
  }

  /// <inheritdoc />
  public void Dispose()
  {
    // Hide before disposing — otherwise a ghost icon lingers in the tray until the user hovers
    // over its former location (AGENTS.md §10.3).
    _notifyIcon.Visible = false;
    _notifyIcon.Dispose();
    _contextMenu.Dispose();
    _currentIcon?.Dispose();
    if (_currentIconHandle != IntPtr.Zero)
    {
      DestroyIcon(_currentIconHandle);
      _currentIconHandle = IntPtr.Zero;
    }
  }

  private ContextMenuStrip BuildBaseMenu()
  {
    ContextMenuStrip menu = new();
    ToolStripMenuItem openItem = new("Open");
    openItem.Click += (_, _) => _onOpen();
    menu.Items.Add(openItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(new ToolStripSeparator()); // the "before Exit" separator — index StateSectionIndex
    ToolStripMenuItem exitItem = new("Exit");
    exitItem.Click += (_, _) => _onExit();
    menu.Items.Add(exitItem);
    return menu;
  }

  private void RebuildStateMenuItems(TrayState state)
  {
    // ToolStripItem.Dispose() removes itself from its owning collection, so repeatedly disposing
    // the item now sitting at StateSectionIndex shrinks the run without a separate RemoveAt.
    for (int i = 0; i < _stateItemCount; i++)
    {
      _contextMenu.Items[StateSectionIndex].Dispose();
    }

    ToolStripItem[] items = BuildStateItems(state);
    for (int i = 0; i < items.Length; i++)
    {
      _contextMenu.Items.Insert(StateSectionIndex + i, items[i]);
    }

    _stateItemCount = items.Length;
  }

  private ToolStripItem[] BuildStateItems(TrayState state) =>
    state switch
    {
      TrayState.Running =>
      [
        CreateMenuItem("Pause", () => _control.PauseTimerAsync()),
        CreateMenuItem("Lap", _control.AddLap),
        CreateMenuItem("Stop", () => _control.StopTimerAsync()),
      ],
      TrayState.Paused =>
      [
        CreateMenuItem("Continue", _control.StartTimer),
        CreateMenuItem("Stop", () => _control.StopTimerAsync()),
      ],
      _ =>
      [
        CreateMenuItem("Start", _control.StartTimer),
        CreateMenuItem("Stop", () => _control.StopTimerAsync()),
      ],
    };

  private static ToolStripMenuItem CreateMenuItem(string text, Action onClick)
  {
    ToolStripMenuItem item = new(text);
    item.Click += (_, _) => onClick();
    return item;
  }

  private static ToolStripMenuItem CreateMenuItem(string text, Func<Task> onClick)
  {
    ToolStripMenuItem item = new(text);
    item.Click += async (_, _) => await onClick();
    return item;
  }

  private void RenderIcon(int hours, int minutes, TrayState state)
  {
    Color tint = state switch
    {
      TrayState.Running => Palette.Accent(_darkMode),
      TrayState.Paused => Palette.PauseButton.Base,
      _ => Palette.MutedText(_darkMode),
    };
    // Two digits per row is the fixed layout (AGENTS.md §10.1); an hour count of 100+ wraps at two
    // digits here, but the tooltip set in UpdateDisplay carries the true, unrounded value.
    string hourText = (hours % 100).ToString("D2", CultureInfo.InvariantCulture);
    string minuteText = (minutes % 100).ToString("D2", CultureInfo.InvariantCulture);

    using Bitmap bitmap = new(32, 32);
    using (Graphics graphics = Graphics.FromImage(bitmap))
    {
      graphics.Clear(Color.Transparent);
      graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
      using Font font = new("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
      using SolidBrush brush = new(tint);
      using StringFormat format = new()
      {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
      };
      graphics.DrawString(hourText, font, brush, new RectangleF(0, 0, 32, 16), format);
      graphics.DrawString(minuteText, font, brush, new RectangleF(0, 16, 32, 16), format);
    }

    IntPtr newHandle = bitmap.GetHicon();
    Icon newIcon = Icon.FromHandle(newHandle);
    // Point the shell at the new icon BEFORE destroying the previous handle — swapping the other
    // way briefly leaves the shell rendering a freed handle.
    _notifyIcon.Icon = newIcon;

    IntPtr oldHandle = _currentIconHandle;
    Icon? oldIcon = _currentIcon;
    _currentIconHandle = newHandle;
    _currentIcon = newIcon;
    // Icon.FromHandle does not take ownership of the HICON — disposing the managed wrapper alone
    // never releases it, which is why DestroyIcon must be called explicitly on every replace
    // (AGENTS.md §10.1: forgetting this is the single most common bug in this pattern).
    oldIcon?.Dispose();
    if (oldHandle != IntPtr.Zero)
    {
      DestroyIcon(oldHandle);
    }
  }

  [LibraryImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool DestroyIcon(IntPtr hIcon);

  private enum TrayState
  {
    Idle,
    Running,
    Paused,
  }
}
