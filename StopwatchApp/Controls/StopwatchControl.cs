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
  private readonly GlyphButton _primaryButton; // Start / Continue — never shown alongside Pause/Lap
  private readonly GlyphButton _pauseButton;
  private readonly GlyphButton _lapButton;
  private readonly GlyphButton _stopButton;
  private readonly Label _elapsedLabel;
  private readonly Label _resumedNoteLabel;
  private readonly Font _elapsedFont;
  private bool _darkMode;

  /// <summary>
  /// Initializes a new instance of the <see cref="StopwatchControl"/> class.
  /// </summary>
  /// <param name="store">The persistence surface passed through to the internal <see cref="StopwatchTimer"/>.</param>
  /// <param name="time">The clock source passed through to the internal <see cref="StopwatchTimer"/>.</param>
  /// <param name="autosaveIntervalMinutes">The positive running-time checkpoint interval, in minutes.</param>
  public StopwatchControl(IStopwatchStore store, TimeProvider time, int autosaveIntervalMinutes)
  {
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

    // Card-level inset (S11a comp spacing scale); the outer border/fill is drawn in OnPaint below.
    Padding = new Padding(Palette.SpacingLg);
    // S14 (AGENTS.md §17) — ResizeRedraw forces a full repaint on every size change instead of only
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
      // S14 (AGENTS.md §17) — Anchor=None (not Dock+TextAlign) is this repo's centering idiom for
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

    _primaryButton = CreateButton("Start", Glyph.Play, Palette.StartButton);
    _primaryButton.Click += (_, _) => StartTimer();

    _pauseButton = CreateButton("Pause", Glyph.Pause, Palette.PauseButton);
    // The outer await here (no ConfigureAwait(false)) captures the UI SynchronizationContext, so
    // PauseTimerAsync's own UpdateDisplay() call is guaranteed to run back on the UI thread
    // regardless of what thread PauseAsync's internals (which do use ConfigureAwait(false))
    // complete on.
    _pauseButton.Click += async (_, _) => await PauseTimerAsync();

    _lapButton = CreateButton("Lap", Glyph.Flag, Palette.LapButton);
    _lapButton.Click += (_, _) => AddLap();

    _stopButton = CreateButton("Stop", Glyph.Stop, Palette.StopButton);
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
    // Added in the fixed left-to-right order from §2.6/§8.5; hidden buttons take no flow space,
    // so idle/paused render "Start/Continue, Stop" and running renders "Pause, Lap, Stop". The
    // FlowLayoutPanel's own AutoSize shrinks to fit whichever set is visible, and re-centers via its
    // TableLayoutPanel cell's Anchor=None below.
    buttonRow.Controls.Add(_primaryButton);
    buttonRow.Controls.Add(_pauseButton);
    buttonRow.Controls.Add(_lapButton);
    buttonRow.Controls.Add(_stopButton);

    // S14 (AGENTS.md §17) — an interior TableLayoutPanel with Anchor=None cells is this repo's
    // centering idiom: a plain Controls collection never re-centers a child on its own, only a
    // TableLayoutPanel cell does. Dock.Top (not Fill, see the S14a correction in §17) still spans
    // this card's full content width, so each AutoSize row centers across that width — but Dock.Top
    // also contributes a real preferred height, which is required for the AutoSize/GrowAndShrink
    // pairing below to size the card correctly.
    TableLayoutPanel contentLayout = new()
    {
      // S14a (AGENTS.md §17) — Dock.Fill here collapsed the whole card to its own Padding: an
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
    // S14 (AGENTS.md §17) — GrowOnly (the AutoSize default when AutoSizeMode is left unset) never
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
  /// Gets or sets whether the control renders its palette-driven surfaces in dark mode. Defaults
  /// to <see langword="false"/>; wiring this to the OS setting is S12's job (AGENTS.md §11/§17).
  /// </summary>
  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  public bool DarkMode
  {
    get => _darkMode;
    set
    {
      _darkMode = value;
      ApplyPaletteColors();
      _primaryButton.DarkMode = value;
      _pauseButton.DarkMode = value;
      _lapButton.DarkMode = value;
      _stopButton.DarkMode = value;
      // Buttons resolve their corner-fill background from this control's BackColor on every paint
      // (GlyphButton.OnPaintBackground), so a theme flip needs an explicit repaint to pick it up.
      Invalidate(invalidateChildren: true);
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
  /// display stale (AGENTS.md §17).
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
  /// performs — see <see cref="StartTimer"/> for why this is exposed.
  /// </summary>
  public async Task StopTimerAsync()
  {
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
    // S11a card treatment: a thin rounded outline is the low-risk GDI+ stand-in for the comp's
    // resting-elevation shadow (AGENTS.md §17) — the flat BackColor fill already covers the rest.
    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    Rectangle bounds = new(0, 0, Width - 1, Height - 1);
    using GraphicsPath path = RoundedRectangle.Path(bounds, Palette.CardCornerRadius);
    using Pen borderPen = new(Palette.ShadowResting(_darkMode), 1f);
    e.Graphics.DrawPath(borderPen, path);
  }

  private static GlyphButton CreateButton(
    string text,
    Glyph? glyph,
    (Color Base, Color Hover, Color Pressed) colors
  ) => new(text, glyph, colors) { Margin = new Padding(Palette.SpacingXs) };

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
