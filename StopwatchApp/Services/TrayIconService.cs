using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using StopwatchApp.Controls;
using StopwatchApp.Formatting;
using StopwatchApp.Theme;

namespace StopwatchApp.Services;

/// <summary>
/// Owns the <see cref="NotifyIcon"/>: a GDI+-rendered 32×32 icon (large minute-only digits during
/// the first hour, then a large <c>h:mm</c> label through the ninth hour, then large whole-hour or
/// whole-day labels), its tooltip, and its context menu (<c>Open</c>, the state-appropriate
/// transition(s), <c>Exit</c>). See AGENTS.md §10.1/§10.2. Takes callbacks rather than a
/// <c>MainForm</c> reference — parent/child communication happens through delegates, never a shared
/// mutable reference (AGENTS.md §3).
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
  // The index in _contextMenu.Items where the state-dependent action item(s) live, between the
  // "Open" separator and the "Exit" separator. Fixed by the skeleton BuildBaseMenu constructs.
  private const int StateSectionIndex = 2;
  private const long MinutesPerHour = 60;
  private const long MinutesPerDay = 24 * MinutesPerHour;
  private const long HourMinuteMaxHours = 10;

  // Tray icon label geometry (§10.1): every layout (minutes, hours, days) is auto-fit against the
  // same 32×32 canvas by the same rule, so a short label like "1H" reads as large as "45" instead
  // of being penalized for sharing a code path with wider siblings like "23H".
  private const int IconSize = 32;
  private const int MaxLabelFontSize = 34; // above any size that can fit; the loop's start point
  private const int MinLabelFontSize = 8; // floor — guarantees the loop always terminates
  private const float LabelFitBudget = 30f; // 1px breathing room per side inside IconSize

  private readonly StopwatchControl _control;
  private readonly Action _onOpen;
  private readonly Action _onExit;
  private readonly NotifyIcon _notifyIcon;
  private readonly ContextMenuStrip _contextMenu;
  private readonly Dictionary<TrayMenuAction, Bitmap> _menuImages;
  private int _stateItemCount;
  private bool _darkMode;
  private IntPtr _currentIconHandle;
  private Icon? _currentIcon;

  // The active layout is tracked explicitly beside the rendered value/minute/state. Unit boundaries
  // can change the layout without changing the numeric value, so keeping it in the dirty-check
  // prevents a future presentation change from being skipped.
  private (long Value, int Minutes, TrayState State, TrayIconLayout Layout) _lastRendered = (
    -1,
    -1,
    TrayState.Idle,
    TrayIconLayout.LargeMinutes
  );

  /// <summary>
  /// Initializes a new instance of the <see cref="TrayIconService"/> class and makes the tray icon
  /// visible immediately, showing the idle state.
  /// </summary>
  /// <param name="control">
  /// The stopwatch control the tray menu's transition items act on and whose state
  /// <see cref="UpdateDisplay"/> renders.
  /// </param>
  /// <param name="onOpen">Invoked when the user opens the tray icon (single left-click or the
  /// <c>Open</c> menu item).</param>
  /// <param name="onExit">Invoked when the user selects the tray menu's <c>Exit</c> item.</param>
  public TrayIconService(StopwatchControl control, Action onOpen, Action onExit)
  {
    _control = control;
    _onOpen = onOpen;
    _onExit = onExit;

    _menuImages = CreateMenuImages();
    _contextMenu = BuildBaseMenu();
    _notifyIcon = new NotifyIcon { ContextMenuStrip = _contextMenu, Visible = true };
    _notifyIcon.MouseClick += (_, e) =>
    {
      if (ShouldOpen(e.Button))
      {
        _onOpen();
      }
    };

    RebuildStateMenuItems(TrayState.Idle);
    UpdateDisplay(elapsedMs: 0, running: false, paused: false);
  }

  /// <summary>
  /// Gets or sets whether the tray icon renders its state tint from the dark-mode palette. Defaults
  /// to <see langword="false"/>; <c>MainForm</c> wires this to the live OS setting (AGENTS.md §7/§11),
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
      RenderIcon(
        _lastRendered.Value,
        _lastRendered.Minutes,
        _lastRendered.State,
        _lastRendered.Layout
      );
    }
  }

  /// <summary>
  /// Refreshes the tooltip and, at most once per second and only when the simplified display actually
  /// changes, the rendered icon and the state-dependent menu items (AGENTS.md §10.1).
  /// </summary>
  /// <param name="elapsedMs">The current elapsed time, in milliseconds.</param>
  /// <param name="running">Whether the stopwatch is currently running.</param>
  /// <param name="paused">Whether the stopwatch is currently paused.</param>
  public void UpdateDisplay(long elapsedMs, bool running, bool paused)
  {
    // The tooltip carries the authoritative, unrounded HH:MM:SS value and updates every call —
    // only the once-per-second icon bitmap and menu are throttled below.
    _notifyIcon.Text = TimeFormat.FormatTime(elapsedMs);

    (long value, int minutes, TrayIconLayout layout) = GetDisplayValues(elapsedMs);
    TrayState state =
      running ? TrayState.Running
      : paused ? TrayState.Paused
      : TrayState.Idle;
    (long Value, int Minutes, TrayState State, TrayIconLayout Layout) rendered = (
      value,
      minutes,
      state,
      layout
    );
    if (rendered == _lastRendered)
    {
      return;
    }

    bool stateChanged = state != _lastRendered.State;
    _lastRendered = rendered;
    RenderIcon(value, minutes, state, layout);
    if (stateChanged)
    {
      RebuildStateMenuItems(state);
    }
  }

  /// <summary>
  /// Forces the tray icon bitmap to be re-rendered from its current display values, bypassing
  /// <see cref="UpdateDisplay"/>'s once-per-second dirty-check. Used by <c>MainForm</c> on
  /// <see cref="Form.DpiChanged"/> (AGENTS.md §7/§10.1): a DPI change alone never changes the
  /// displayed hour/minute/state/layout, so <see cref="UpdateDisplay"/> would otherwise skip the
  /// redraw entirely.
  /// </summary>
  public void RefreshIcon() =>
    RenderIcon(
      _lastRendered.Value,
      _lastRendered.Minutes,
      _lastRendered.State,
      _lastRendered.Layout
    );

  /// <summary>
  /// Derives the simplified icon value and layout from an unbounded elapsed duration. The icon moves
  /// from minutes to an <c>h:mm</c> label to whole hours to whole days while the tooltip remains the
  /// full duration. Internal so the unit-boundary rule can be unit tested without constructing a
  /// real <see cref="NotifyIcon"/>/HICON.
  /// </summary>
  internal static (long Value, int Minutes, TrayIconLayout Layout) GetDisplayValues(long elapsedMs)
  {
    long totalMinutes = elapsedMs / 60000;
    if (totalMinutes < MinutesPerHour)
    {
      return (0, (int)totalMinutes, TrayIconLayout.LargeMinutes);
    }

    long totalHours = totalMinutes / MinutesPerHour;
    if (totalHours < HourMinuteMaxHours)
    {
      return (totalHours, (int)(totalMinutes % MinutesPerHour), TrayIconLayout.HourMinute);
    }

    if (totalHours < 24)
    {
      return (totalHours, 0, TrayIconLayout.LargeHours);
    }

    return (totalMinutes / MinutesPerDay, 0, TrayIconLayout.LargeDays);
  }

  /// <summary>
  /// Formats a simplified whole-hours label in invariant culture. Internal so the tray's compact
  /// presentation can be unit tested without GDI+.
  /// </summary>
  internal static string FormatHourLabel(long hours) =>
    hours.ToString(CultureInfo.InvariantCulture) + "H";

  /// <summary>
  /// Formats a simplified <c>h:mm</c> label (unpadded hour, zero-padded minutes) in invariant
  /// culture. Internal so the tray's compact presentation can be unit tested without GDI+.
  /// </summary>
  internal static string FormatHourMinuteLabel(long hours, int minutes) =>
    hours.ToString(CultureInfo.InvariantCulture)
    + ":"
    + minutes.ToString("D2", CultureInfo.InvariantCulture);

  /// <summary>
  /// Formats a simplified whole-days label in invariant culture. Internal so the tray's compact
  /// presentation can be unit tested without GDI+.
  /// </summary>
  internal static string FormatDayLabel(long days) =>
    days.ToString(CultureInfo.InvariantCulture) + "D";

  /// <summary>
  /// Returns whether a tray-icon mouse click should open the main window. Only a single left-click
  /// opens it; right-click remains exclusively available for the context menu. Internal so the
  /// input rule can be tested without constructing a native <see cref="NotifyIcon"/>.
  /// </summary>
  /// <param name="button">The mouse button reported by <see cref="NotifyIcon.MouseClick"/>.</param>
  internal static bool ShouldOpen(MouseButtons button) => button == MouseButtons.Left;

  /// <inheritdoc />
  public void Dispose()
  {
    // Hide before disposing — otherwise a ghost icon lingers in the tray until the user hovers
    // over its former location (AGENTS.md §10.3).
    _notifyIcon.Visible = false;
    _notifyIcon.Dispose();
    _contextMenu.Dispose();
    foreach (Bitmap image in _menuImages.Values)
    {
      image.Dispose();
    }

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
    ToolStripMenuItem openItem = CreateMenuItem("Open", TrayMenuAction.Open, _onOpen);
    menu.Items.Add(openItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(new ToolStripSeparator()); // the "before Exit" separator — index StateSectionIndex
    ToolStripMenuItem exitItem = CreateMenuItem("Exit", TrayMenuAction.Exit, _onExit);
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
        CreateMenuItem("Pause", TrayMenuAction.Pause, () => _control.PauseTimerAsync()),
        CreateMenuItem("Lap", TrayMenuAction.Lap, _control.AddLap),
        CreateMenuItem("Stop", TrayMenuAction.Stop, () => _control.StopTimerAsync()),
      ],
      TrayState.Paused =>
      [
        CreateMenuItem("Continue", TrayMenuAction.Continue, _control.StartTimer),
        CreateMenuItem("Stop", TrayMenuAction.Stop, () => _control.StopTimerAsync()),
      ],
      _ =>
      [
        CreateMenuItem("Start", TrayMenuAction.Start, _control.StartTimer),
        CreateMenuItem("Stop", TrayMenuAction.Stop, () => _control.StopTimerAsync()),
      ],
    };

  private ToolStripMenuItem CreateMenuItem(string text, TrayMenuAction action, Action onClick)
  {
    ToolStripMenuItem item = new(text) { Image = _menuImages[action] };
    item.Click += (_, _) => onClick();
    return item;
  }

  private ToolStripMenuItem CreateMenuItem(string text, TrayMenuAction action, Func<Task> onClick)
  {
    ToolStripMenuItem item = new(text) { Image = _menuImages[action] };
    item.Click += async (_, _) => await onClick();
    return item;
  }

  private static Dictionary<TrayMenuAction, Bitmap> CreateMenuImages()
  {
    Dictionary<TrayMenuAction, Bitmap> images = [];
    foreach (TrayMenuAction action in Enum.GetValues<TrayMenuAction>())
    {
      images.Add(action, RenderMenuImage(action));
    }

    return images;
  }

  private static Bitmap RenderMenuImage(TrayMenuAction action)
  {
    Bitmap bitmap = new(16, 16);
    using Graphics graphics = Graphics.FromImage(bitmap);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.Clear(Color.Transparent);

    Color color = action switch
    {
      TrayMenuAction.Start or TrayMenuAction.Continue => Palette.StartButton.Base,
      TrayMenuAction.Pause => Palette.PauseButton.Base,
      TrayMenuAction.Lap or TrayMenuAction.Open => Palette.LapButton.Base,
      _ => Palette.StopButton.Base,
    };
    using SolidBrush brush = new(color);
    using Pen pen = new(color, 2f) { LineJoin = LineJoin.Round };
    Rectangle bounds = new(2, 2, 11, 11);

    switch (action)
    {
      case TrayMenuAction.Open:
        graphics.DrawRectangle(pen, bounds);
        graphics.DrawLine(pen, 7, 10, 13, 4);
        graphics.DrawLine(pen, 9, 4, 13, 4);
        graphics.DrawLine(pen, 13, 4, 13, 8);
        break;
      case TrayMenuAction.Start:
      case TrayMenuAction.Continue:
        graphics.FillPolygon(brush, [new Point(5, 3), new Point(5, 13), new Point(13, 8)]);
        break;
      case TrayMenuAction.Pause:
        graphics.FillRectangle(brush, 4, 3, 3, 10);
        graphics.FillRectangle(brush, 10, 3, 3, 10);
        break;
      case TrayMenuAction.Lap:
        graphics.FillRectangle(brush, 4, 2, 2, 12);
        graphics.FillPolygon(brush, [new Point(6, 3), new Point(13, 5), new Point(6, 8)]);
        break;
      case TrayMenuAction.Stop:
        graphics.FillRectangle(brush, 3, 3, 10, 10);
        break;
      case TrayMenuAction.Exit:
        graphics.DrawRectangle(pen, 3, 2, 7, 12);
        graphics.DrawLine(pen, 7, 8, 14, 8);
        graphics.DrawLine(pen, 11, 5, 14, 8);
        graphics.DrawLine(pen, 11, 11, 14, 8);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(action), action, message: null);
    }

    return bitmap;
  }

  private void RenderIcon(long value, int minutes, TrayState state, TrayIconLayout layout)
  {
    Color tint = state switch
    {
      TrayState.Running => Palette.Accent(_darkMode),
      TrayState.Paused => Palette.PauseButton.Base,
      _ => Palette.MutedText(_darkMode),
    };

    using Bitmap bitmap = new(IconSize, IconSize);
    using (Graphics graphics = Graphics.FromImage(bitmap))
    {
      graphics.Clear(Color.Transparent);
      graphics.SmoothingMode = SmoothingMode.AntiAlias;
      using SolidBrush brush = new(tint);
      string label = layout switch
      {
        TrayIconLayout.LargeMinutes => (minutes % 100).ToString("D2", CultureInfo.InvariantCulture),
        TrayIconLayout.HourMinute => FormatHourMinuteLabel(value, minutes),
        TrayIconLayout.LargeHours => FormatHourLabel(value),
        _ => FormatDayLabel(value),
      };
      DrawLargeLabel(graphics, brush, label);
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

  // Every tray label — minutes, hours, days — is drawn at the largest font size whose actual glyph
  // ink fits the 32×32 canvas (AGENTS.md §10.1). A fixed size sized for the widest label (e.g. a
  // size that fits `23H`) made the common `1H`/`45` unnecessarily small; measuring `MeasureString`
  // output against a fit budget (the previous approach) was worse still, because
  // `StringFormat.GenericDefault` pads the width with side bearing and reports the full line-box
  // height rather than glyph height, so it rejected sizes that would actually have fit — that
  // measurement gap, not the unit label being wider, is why `1H` used to render far smaller than
  // `45`. Building a `GraphicsPath` and reading `GetBounds()` measures the real ink instead.
  //
  // Filling an explicitly translated path also sidesteps a second, previously separate issue:
  // handing `DrawString` a `RectangleF` with `StringFormat` alignment was observed (empirically,
  // rendering to a Bitmap and inspecting pixel alpha) to silently drop a trailing character
  // whenever the measured width approached the bounds' width. A path translated by hand has no
  // such fit box to overflow.
  private static void DrawLargeLabel(Graphics graphics, Brush brush, string text)
  {
    int size = MeasureLabelFontSize(text);
    using FontFamily family = new("Segoe UI");
    using GraphicsPath path = new();
    path.AddString(
      text,
      family,
      (int)FontStyle.Bold,
      size,
      PointF.Empty,
      StringFormat.GenericTypographic
    );

    RectangleF bounds = path.GetBounds();
    using Matrix translation = new();
    translation.Translate(
      -bounds.X + (IconSize - bounds.Width) / 2f,
      -bounds.Y + (IconSize - bounds.Height) / 2f
    );
    path.Transform(translation);
    graphics.FillPath(brush, path);
  }

  /// <summary>
  /// Finds the largest Segoe UI Bold em size, from <see cref="MaxLabelFontSize"/> down to
  /// <see cref="MinLabelFontSize"/>, whose glyph ink for <paramref name="text"/> fits within
  /// <see cref="LabelFitBudget"/> on both axes. Internal so the fit rule — and specifically that it
  /// no longer undersizes hour/day labels versus the minutes readout — can be unit tested without
  /// a <see cref="Graphics"/> surface.
  /// </summary>
  internal static int MeasureLabelFontSize(string text)
  {
    using FontFamily family = new("Segoe UI");
    for (int size = MaxLabelFontSize; size > MinLabelFontSize; size--)
    {
      using GraphicsPath path = new();
      path.AddString(
        text,
        family,
        (int)FontStyle.Bold,
        size,
        PointF.Empty,
        StringFormat.GenericTypographic
      );
      RectangleF bounds = path.GetBounds();
      if (bounds.Width <= LabelFitBudget && bounds.Height <= LabelFitBudget)
      {
        return size;
      }
    }

    return MinLabelFontSize;
  }

  [LibraryImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool DestroyIcon(IntPtr hIcon);

  /// <summary>
  /// Which tray-icon presentation is active (AGENTS.md §10.1): large minutes during the first
  /// hour, then a large <c>h:mm</c> label through the ninth hour, then a large whole-hours or
  /// whole-days label.
  /// </summary>
  internal enum TrayIconLayout
  {
    LargeMinutes,
    HourMinute,
    LargeHours,
    LargeDays,
  }

  private enum TrayState
  {
    Idle,
    Running,
    Paused,
  }

  private enum TrayMenuAction
  {
    Open,
    Start,
    Continue,
    Pause,
    Lap,
    Stop,
    Exit,
  }
}
