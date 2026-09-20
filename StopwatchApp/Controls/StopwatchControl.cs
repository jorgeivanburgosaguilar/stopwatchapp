using System.ComponentModel;
using System.Drawing.Drawing2D;
using StopwatchApp.Formatting;
using StopwatchApp.Services;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// The stopwatch display and control row: the elapsed-time readout, the state-dependent button
/// row, and the "resumed from a pause" note (AGENTS.md §8.5). Owns a <see cref="StopwatchTimer"/>
/// and the UI-side <see cref="System.Windows.Forms.Timer"/> that drives its
/// <see cref="StopwatchTimer.Tick"/> once a second while running.
/// </summary>
public sealed class StopwatchControl : UserControl
{
  private readonly System.Windows.Forms.Timer _uiTimer;
  private readonly ToolTip _shortcutToolTip;
  private readonly Button _primaryButton; // Start / Continue — never shown alongside Pause/Lap
  private readonly Button _pauseButton;
  private readonly Button _lapButton;
  private readonly Button _stopButton;
  private readonly Label _elapsedLabel;
  private readonly Label _resumedNoteLabel;
  private readonly Font _elapsedFont;
  private readonly int _stopConfirmationAfterMinutes;
  private bool _darkMode;

  /// <summary>
  /// Initializes a new instance of the <see cref="StopwatchControl"/> class.
  /// </summary>
  /// <param name="store">The persistence surface passed through to the internal <see cref="StopwatchTimer"/>.</param>
  /// <param name="time">The clock source passed through to the internal <see cref="StopwatchTimer"/>.</param>
  /// <param name="autosaveIntervalMinutes">The positive running-time checkpoint interval, in minutes.</param>
  /// <param name="stopConfirmationAfterMinutes">
  /// The elapsed minutes after which Stop asks for confirmation; <c>0</c> disables it.
  /// </param>
  public StopwatchControl(
    IStopwatchStore store,
    TimeProvider time,
    int autosaveIntervalMinutes,
    int stopConfirmationAfterMinutes
  )
  {
    _stopConfirmationAfterMinutes = stopConfirmationAfterMinutes;
    Timer = new StopwatchTimer(store, time, autosaveIntervalMinutes);

    _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    _uiTimer.Tick += async (_, _) =>
    {
      // System.Windows.Forms.Timer.Tick always fires on the UI thread, so calling Timer.Tick()
      // (synchronous, no awaits) and refreshing controls here directly is safe.
      Timer.Tick();
      await Timer.SaveAutosaveIfDueAsync();
      UpdateDisplay();
      Tick?.Invoke();
    };

    // Card-level inset from AGENTS.md §11; the outer border/fill is drawn in OnPaint below.
    Padding = new Padding(Palette.SpacingLg);
    // AGENTS.md §10.3 — ResizeRedraw forces a full repaint on every size change instead of only
    // the newly-exposed strip; without it, the rounded border OnPaint draws at Width-1/Height-1
    // stays visible at its old position after a resize — the ghosting behind the button row.
    SetStyle(
      ControlStyles.ResizeRedraw
        | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.AllPaintingInWmPaint,
      true
    );

    _elapsedFont = Typography.CreateDisplayFont();
    _elapsedLabel = new Label
    {
      AutoSize = true,
      // AGENTS.md §8.5 — Anchor=None (not Dock+TextAlign) is this repo's centering idiom for
      // an AutoSize label: it shrink-wraps to its text, so TextAlign has no box left to center
      // inside. Centering instead comes from this label's TableLayoutPanel cell, below.
      Anchor = AnchorStyles.None,
      Font = _elapsedFont,
      Margin = new Padding(0, 0, 0, Palette.SpacingMd),
    };

    _resumedNoteLabel = new Label
    {
      AutoSize = true,
      Anchor = AnchorStyles.None,
      Visible = false,
      Margin = new Padding(0, 0, 0, Palette.SpacingSm),
    };

    _primaryButton = ButtonFactory.Create("Start", Palette.StartButton);
    _primaryButton.Click += (_, _) => StartTimer();

    _pauseButton = ButtonFactory.Create("Pause", Palette.PauseButton);
    // The outer await here (no ConfigureAwait(false)) captures the UI SynchronizationContext, so
    // PauseTimerAsync's own UpdateDisplay() call is guaranteed to run back on the UI thread
    // regardless of what thread PauseAsync's internals (which do use ConfigureAwait(false))
    // complete on.
    _pauseButton.Click += async (_, _) => await PauseTimerAsync();

    _lapButton = ButtonFactory.Create("Lap", Palette.LapButton);
    _lapButton.Click += (_, _) => AddLap();

    _stopButton = ButtonFactory.Create("Stop", Palette.StopButton);
    _stopButton.Click += async (_, _) => await StopTimerAsync();

    _shortcutToolTip = new ToolTip();
    _shortcutToolTip.SetToolTip(_pauseButton, "Pause (Space)");
    _shortcutToolTip.SetToolTip(_lapButton, "Lap (Shift+Space)");
    _shortcutToolTip.SetToolTip(_stopButton, "Stop (Enter)");

    FlowLayoutPanel buttonRow = new()
    {
      FlowDirection = FlowDirection.LeftToRight,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Anchor = AnchorStyles.None,
    };
    // Added in the fixed left-to-right order from AGENTS.md §8.5; hidden buttons take no flow space,
    // so idle/paused render "Start/Continue, Stop" and running renders "Pause, Lap, Stop". The
    // FlowLayoutPanel's own AutoSize shrinks to fit whichever set is visible, and re-centers via its
    // TableLayoutPanel cell's Anchor=None below.
    buttonRow.Controls.Add(_primaryButton);
    buttonRow.Controls.Add(_pauseButton);
    buttonRow.Controls.Add(_lapButton);
    buttonRow.Controls.Add(_stopButton);

    // AGENTS.md §8.5/§10.3 — an interior TableLayoutPanel with Anchor=None cells is this repo's
    // centering idiom: a plain Controls collection never re-centers a child on its own, only a
    // TableLayoutPanel cell does. Dock.Top rather than Fill (see AGENTS.md §10.3) still spans
    // this card's full content width, so each AutoSize row centers across that width — but Dock.Top
    // also contributes a real preferred height, which is required for the AutoSize/GrowAndShrink
    // pairing below to size the card correctly.
    TableLayoutPanel contentLayout = new()
    {
      // AGENTS.md §10.3 — Dock.Fill here collapses the whole card to its own Padding: an
      // AutoSize parent asks its children for their preferred size, but a Dock.Fill child instead
      // takes whatever size the parent gives it, so WinForms breaks that circular dependency by
      // never letting a Fill child contribute to its AutoSize parent's preferred size. Dock.Top
      // does contribute a real preferred height while still stretching to the parent's full width.
      Dock = DockStyle.Top,
      AutoSize = true,
      // GrowOnly (the AutoSize default) would never shrink this panel back down after the resumed
      // note is cleared by Stop — the same pitfall already documented for the card itself, below.
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      RowCount = 3,
    };
    contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    contentLayout.Controls.Add(_elapsedLabel, 0, 0);
    contentLayout.Controls.Add(_resumedNoteLabel, 0, 1);
    contentLayout.Controls.Add(buttonRow, 0, 2);

    Controls.Add(contentLayout);
    AutoSize = true;
    // AGENTS.md §10.3 — GrowOnly (the AutoSize default when AutoSizeMode is left unset) never
    // shrinks the card back down once it has grown to fit the "Resumed from a pause" note; explicit
    // GrowAndShrink is required for the card to return to its normal height after Stop clears it.
    AutoSizeMode = AutoSizeMode.GrowAndShrink;
    DoubleBuffered = true;

    ApplyPaletteColors();
    UpdateDisplay();
  }

  /// <summary>
  /// Gets the underlying state machine. A caller wiring up <c>RecordsListControl</c> reads
  /// <c>Timer.Laps</c>/<c>Timer.Records</c>; <c>Timer.RestoredPausedAtMs &gt; 0</c> reports whether
  /// a paused session was restored.
  /// </summary>
  public StopwatchTimer Timer { get; }

  /// <summary>
  /// Fires after a user action or restoration changes stopwatch state that a parent control may
  /// render elsewhere, such as the current laps list.
  /// </summary>
  public event Action? StateChanged;

  /// <summary>
  /// Fires once a second, at the end of the internal UI timer's <c>Tick</c> handler (so always on
  /// the UI thread) — the same heartbeat that drives <see cref="StopwatchTimer.Tick"/> and
  /// <see cref="UpdateDisplay"/>. Only fires while running; a parent needing to refresh during idle
  /// or paused states should also subscribe to <see cref="StateChanged"/>.
  /// </summary>
  public event Action? Tick;

  /// <summary>
  /// Gets or sets the callback asked to confirm a Stop that passed the configured threshold; it
  /// returns <see langword="true"/> to proceed. Defaults to always confirming so the control works
  /// standalone; <c>MainForm</c> supplies the real dialog.
  /// </summary>
  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  public Func<bool> ConfirmStop { get; set; } = () => true;

  /// <summary>
  /// Gets or sets whether the control renders its palette-driven surfaces in dark mode. Defaults
  /// to <see langword="false"/>; <c>MainForm</c> wires it to the OS setting (AGENTS.md §7/§11).
  /// </summary>
  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  public bool DarkMode
  {
    get => _darkMode;
    set
    {
      _darkMode = value;
      ApplyPaletteColors();
      // The transport buttons are stock FlatStyle.Flat Buttons with a fixed BackColor/
      // FlatAppearance set from Palette at construction (ButtonFactory.Create) — they carry no
      // dark-mode state of their own, so only this control's own OnPaint (the card border) needs
      // an explicit repaint here.
      Invalidate();
    }
  }

  /// <summary>
  /// Restores a saved paused session, if any, and refreshes the display. Call once at startup,
  /// after the database connection opens (AGENTS.md §9).
  /// </summary>
  public async Task RestoreAsync()
  {
    await Timer.RestoreAsync();
    UpdateDisplay();
    StateChanged?.Invoke();
  }

  /// <summary>
  /// Starts a fresh session, or resumes a paused one, and refreshes the display. The same action
  /// the Start/Continue button performs — exposed so a caller outside this control's own buttons
  /// (the tray menu, a global hotkey) can drive the same transition without leaving the window's
  /// display stale (AGENTS.md §3.1/§8.5/§10.4).
  /// </summary>
  public void StartTimer()
  {
    Timer.Start();
    UpdateDisplay();
    StateChanged?.Invoke();
  }

  /// <summary>
  /// Pauses the running session and refreshes the display. The same action the Pause button
  /// performs — see <see cref="StartTimer"/> for why this is exposed.
  /// </summary>
  public async Task PauseTimerAsync()
  {
    await Timer.PauseAsync();
    UpdateDisplay();
    StateChanged?.Invoke();
  }

  /// <summary>
  /// Records a lap and refreshes the display. The same action the Lap button performs — see
  /// <see cref="StartTimer"/> for why this is exposed.
  /// </summary>
  public void AddLap()
  {
    Timer.Lap();
    UpdateDisplay();
    StateChanged?.Invoke();
  }

  /// <summary>
  /// Stops the current session and refreshes the display. The same action the Stop button
  /// performs — see <see cref="StartTimer"/> for why this is exposed. Past the configured
  /// threshold (AGENTS.md §8.3) a running clock is first paused, then <see cref="ConfirmStop"/> is
  /// asked; cancelling resumes a clock that was running and leaves an already-paused one paused.
  /// </summary>
  public async Task StopTimerAsync()
  {
    if (
      RequiresStopConfirmation(
        Timer.ElapsedMs,
        Timer.IsRunning,
        Timer.IsPaused,
        _stopConfirmationAfterMinutes
      )
    )
    {
      bool wasRunning = Timer.IsRunning;
      if (wasRunning)
      {
        await PauseTimerAsync();
      }

      if (!ConfirmStop())
      {
        if (wasRunning)
        {
          StartTimer();
        }

        return;
      }
    }

    await Timer.StopAsync();
    UpdateDisplay();
    StateChanged?.Invoke();
  }

  /// <summary>
  /// Maps a key combination to the <see cref="StopwatchShortcut"/> it triggers, or
  /// <see langword="null"/> if the combination is not one of the window-scoped shortcuts
  /// (AGENTS.md §10.4): bare Space toggles Start/Pause/Continue, Shift+Space records a lap, and
  /// Enter stops. Any other modifier on these keys (e.g. Ctrl+Space) is deliberately unmapped.
  /// </summary>
  /// <param name="keyData">The key combination, as passed to <c>Form.ProcessCmdKey</c>.</param>
  public static StopwatchShortcut? MapShortcut(Keys keyData) =>
    keyData switch
    {
      Keys.Space => StopwatchShortcut.Toggle,
      Keys.Shift | Keys.Space => StopwatchShortcut.Lap,
      Keys.Enter => StopwatchShortcut.Stop,
      _ => null,
    };

  /// <summary>
  /// Reports whether pressing Stop must first ask for confirmation (AGENTS.md §8.3): the
  /// confirmation is enabled (<paramref name="thresholdMinutes"/> above zero), a session exists
  /// (running or paused), and its elapsed time has reached the threshold.
  /// </summary>
  /// <param name="elapsedMs">The current elapsed time, in milliseconds.</param>
  /// <param name="isRunning">Whether the timer is running.</param>
  /// <param name="isPaused">Whether the timer is paused.</param>
  /// <param name="thresholdMinutes">The configured threshold; <c>0</c> disables confirmation.</param>
  public static bool RequiresStopConfirmation(
    long elapsedMs,
    bool isRunning,
    bool isPaused,
    int thresholdMinutes
  ) => thresholdMinutes > 0 && (isRunning || isPaused) && elapsedMs >= thresholdMinutes * 60_000L;

  /// <inheritdoc />
  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _uiTimer.Stop();
      _uiTimer.Dispose();
      Timer.Dispose();
      _shortcutToolTip.Dispose();
      _elapsedFont.Dispose();
    }
    base.Dispose(disposing);
  }

  /// <inheritdoc />
  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    // AGENTS.md §11: a thin rounded outline is the low-risk GDI+ stand-in for a resting-elevation
    // shadow; the flat BackColor fill already covers the rest.
    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    Rectangle bounds = new(0, 0, Width - 1, Height - 1);
    using GraphicsPath path = RoundedRectangle.Path(bounds, Palette.CardCornerRadius);
    using Pen borderPen = new(Palette.ShadowResting(_darkMode), 1f);
    e.Graphics.DrawPath(borderPen, path);
  }

  private void ApplyPaletteColors()
  {
    BackColor = Palette.CardBackground(_darkMode);
    _elapsedLabel.ForeColor = Palette.Text(_darkMode);
    _resumedNoteLabel.ForeColor = Palette.MutedText(_darkMode);
  }

  private void UpdateDisplay()
  {
    _elapsedLabel.Text = TimeFormat.FormatTime(Timer.ElapsedMs);

    bool running = Timer.IsRunning;
    _primaryButton.Text = Timer.IsPaused ? "Continue" : "Start";
    _primaryButton.Visible = !running;
    _shortcutToolTip.SetToolTip(_primaryButton, $"{_primaryButton.Text} (Space)");
    _pauseButton.Visible = running;
    _lapButton.Visible = running;
    _stopButton.Visible = true;

    if (Timer.RestoredPausedAtMs > 0)
    {
      _resumedNoteLabel.Text =
        $"Resumed from a saved point on {TimeFormat.FormatDate(Timer.RestoredPausedAtMs)} "
        + $"at {TimeFormat.FormatTimeOnly(Timer.RestoredPausedAtMs)}";
      _resumedNoteLabel.Visible = true;
    }
    else
    {
      _resumedNoteLabel.Visible = false;
    }

    if (running != _uiTimer.Enabled)
    {
      if (running)
      {
        _uiTimer.Start();
      }
      else
      {
        _uiTimer.Stop();
      }
    }
  }
}
