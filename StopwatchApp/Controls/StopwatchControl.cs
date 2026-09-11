using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
  private bool _darkMode;

  /// <summary>
  /// Initializes a new instance of the <see cref="StopwatchControl"/> class.
  /// </summary>
  /// <param name="store">The persistence surface passed through to the internal <see cref="StopwatchTimer"/>.</param>
  /// <param name="time">The clock source passed through to the internal <see cref="StopwatchTimer"/>.</param>
  public StopwatchControl(IStopwatchStore store, TimeProvider time)
  {
    Timer = new StopwatchTimer(store, time);

    _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    _uiTimer.Tick += (_, _) =>
    {
      // System.Windows.Forms.Timer.Tick always fires on the UI thread, so calling Timer.Tick()
      // (synchronous, no awaits) and refreshing controls here directly is safe.
      Timer.Tick();
      UpdateDisplay();
      Tick?.Invoke();
    };

    // Card-level inset (S11a comp spacing scale); the outer border/fill is drawn in OnPaint below.
    Padding = new Padding(Palette.SpacingLg);

    _elapsedLabel = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Top,
      TextAlign = ContentAlignment.MiddleCenter,
      Font = new Font(ResolveMonospaceFontFamily(), 28f, FontStyle.Bold),
      Padding = new Padding(0, 0, 0, Palette.SpacingMd),
    };

    _resumedNoteLabel = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Top,
      Visible = false,
      Padding = new Padding(0, 0, 0, Palette.SpacingSm),
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
      Dock = DockStyle.Top,
    };
    // Added in the fixed left-to-right order from §2.6/§8.5; hidden buttons take no flow space,
    // so idle/paused render "Start/Continue, Stop" and running renders "Pause, Lap, Stop".
    buttonRow.Controls.Add(_primaryButton);
    buttonRow.Controls.Add(_pauseButton);
    buttonRow.Controls.Add(_lapButton);
    buttonRow.Controls.Add(_stopButton);

    Controls.Add(buttonRow);
    Controls.Add(_resumedNoteLabel);
    Controls.Add(_elapsedLabel);
    AutoSize = true;
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
      _shortcutToolTip.Dispose();
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
    Glyph glyph,
    (Color Base, Color Hover, Color Pressed) colors
  ) => new(text, glyph, colors) { Margin = new Padding(Palette.SpacingXs) };

  private static string ResolveMonospaceFontFamily()
  {
    using InstalledFontCollection installed = new();
    bool hasCascadiaMono = installed.Families.Any(family =>
      string.Equals(family.Name, "Cascadia Mono", StringComparison.Ordinal)
    );
    return hasCascadiaMono ? "Cascadia Mono" : "Consolas";
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
        $"Resumed from a pause on {TimeFormat.FormatDate(Timer.RestoredPausedAtMs)} "
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

  // The four monochrome transport-button glyphs the S11a comp specifies (AGENTS.md §17): Start/
  // Continue draws Play, Pause draws Pause, Lap draws Flag, Stop draws Stop.
  private enum Glyph
  {
    Play,
    Pause,
    Flag,
    Stop,
  }

  /// <summary>
  /// An owner-drawn <see cref="Button"/> with rounded corners (<see cref="Palette.ControlCornerRadius"/>)
  /// and a monochrome glyph beside its text label — the S11a swap-in for the earlier stages' plain
  /// colored-rectangle buttons. Button text, order, and base/hover/pressed colors are unchanged
  /// (AGENTS.md §8.5/§11); only the paint routine and corner treatment differ.
  /// </summary>
  private sealed class GlyphButton : Button
  {
    private readonly Glyph _glyph;
    private readonly (Color Base, Color Hover, Color Pressed) _colors;
    private bool _hovered;
    private bool _pressed;

    internal GlyphButton(string text, Glyph glyph, (Color Base, Color Hover, Color Pressed) colors)
    {
      _glyph = glyph;
      _colors = colors;
      Text = text;
      AutoSize = true;
      AutoSizeMode = AutoSizeMode.GrowAndShrink;
      Padding = new Padding(
        Palette.SpacingMd,
        Palette.SpacingSm,
        Palette.SpacingMd,
        Palette.SpacingSm
      );
      FlatStyle = FlatStyle.Flat;
      FlatAppearance.BorderSize = 0;
      ForeColor = Color.White;
      SetStyle(
        ControlStyles.UserPaint
          | ControlStyles.AllPaintingInWmPaint
          | ControlStyles.OptimizedDoubleBuffer
          | ControlStyles.ResizeRedraw,
        true
      );
    }

    /// <summary>Whether to draw the dark-mode-only mica-style top-edge highlight on hover/press.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool DarkMode { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
      Size textSize = TextRenderer.MeasureText(Text, Font);
      int glyphBoxSize = textSize.Height;
      int width = Padding.Left + glyphBoxSize + Palette.SpacingXs + textSize.Width + Padding.Right;
      int height = Padding.Top + Math.Max(textSize.Height, glyphBoxSize) + Padding.Bottom;
      return new Size(width, height);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
      _hovered = true;
      Invalidate();
      base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
      _hovered = false;
      _pressed = false;
      Invalidate();
      base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
      _pressed = true;
      Invalidate();
      base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
      _pressed = false;
      Invalidate();
      base.OnMouseUp(mevent);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
      // Fills the area outside the rounded fill path (below) with the ambient BackColor, which
      // Control resolves from this button's parent chain up to StopwatchControl's own
      // palette-driven BackColor, so the corners always match the surrounding card.
      pevent.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
      Graphics graphics = pevent.Graphics;
      graphics.SmoothingMode = SmoothingMode.AntiAlias;

      Color fill =
        _pressed ? _colors.Pressed
        : _hovered ? _colors.Hover
        : _colors.Base;
      Rectangle bounds = new(0, 0, Width - 1, Height - 1);
      using GraphicsPath path = RoundedRectangle.Path(bounds, Palette.ControlCornerRadius);
      using (SolidBrush fillBrush = new(fill))
      {
        graphics.FillPath(fillBrush, path);
      }

      // Dark-mode-only mica-style top-edge highlight on hover/press (AGENTS.md §11/§17); light
      // mode has no equivalent comp token, so it draws nothing there.
      if (DarkMode && (_hovered || _pressed))
      {
        using Pen highlightPen = new(Palette.TopEdgeHighlight, 1f);
        graphics.DrawLine(
          highlightPen,
          bounds.Left + Palette.ControlCornerRadius,
          bounds.Top + 1,
          bounds.Right - Palette.ControlCornerRadius,
          bounds.Top + 1
        );
      }

      int glyphSize = TextRenderer.MeasureText(Text, Font).Height;
      Rectangle glyphRect = new(Padding.Left, (Height - glyphSize) / 2, glyphSize, glyphSize);
      DrawGlyph(graphics, _glyph, glyphRect, ForeColor);

      Rectangle textRect = new(
        glyphRect.Right + Palette.SpacingXs,
        0,
        Math.Max(0, Width - glyphRect.Right - Palette.SpacingXs - Padding.Right),
        Height
      );
      TextRenderer.DrawText(
        graphics,
        Text,
        Font,
        textRect,
        ForeColor,
        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding
      );
    }

    private static void DrawGlyph(Graphics graphics, Glyph glyph, Rectangle rect, Color color)
    {
      using SolidBrush brush = new(color);
      int pad = Math.Max(2, rect.Width / 5);
      Rectangle inner = Rectangle.Inflate(rect, -pad, -pad);
      switch (glyph)
      {
        case Glyph.Play:
          graphics.FillPolygon(
            brush,
            [
              new Point(inner.Left, inner.Top),
              new Point(inner.Left, inner.Bottom),
              new Point(inner.Right, inner.Top + (inner.Height / 2)),
            ]
          );
          break;
        case Glyph.Pause:
          int barWidth = Math.Max(2, inner.Width / 3);
          graphics.FillRectangle(brush, inner.Left, inner.Top, barWidth, inner.Height);
          graphics.FillRectangle(brush, inner.Right - barWidth, inner.Top, barWidth, inner.Height);
          break;
        case Glyph.Flag:
          int poleWidth = Math.Max(1, inner.Width / 8);
          graphics.FillRectangle(brush, inner.Left, inner.Top, poleWidth, inner.Height);
          graphics.FillPolygon(
            brush,
            [
              new Point(inner.Left + poleWidth, inner.Top),
              new Point(inner.Right, inner.Top + (inner.Height / 4)),
              new Point(inner.Left + poleWidth, inner.Top + (inner.Height / 2)),
            ]
          );
          break;
        case Glyph.Stop:
          graphics.FillRectangle(brush, inner);
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(glyph), glyph, message: null);
      }
    }
  }
}
