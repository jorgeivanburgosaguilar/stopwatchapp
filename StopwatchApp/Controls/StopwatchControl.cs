using System.ComponentModel;
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
  private readonly Button _primaryButton; // Start / Continue — never shown alongside Pause/Lap
  private readonly Button _pauseButton;
  private readonly Button _lapButton;
  private readonly Button _stopButton;
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
    };

    _elapsedLabel = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Top,
      TextAlign = ContentAlignment.MiddleCenter,
      Font = new Font(ResolveMonospaceFontFamily(), 28f, FontStyle.Bold),
    };

    _resumedNoteLabel = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Top,
      Visible = false,
    };

    _primaryButton = CreateButton("Start", Palette.StartButton);
    _primaryButton.Click += (_, _) =>
    {
      Timer.Start();
      UpdateDisplay();
    };

    _pauseButton = CreateButton("Pause", Palette.PauseButton);
    _pauseButton.Click += async (_, _) =>
    {
      // The outer await here (no ConfigureAwait(false)) captures the UI SynchronizationContext,
      // so UpdateDisplay() below is guaranteed to run back on the UI thread regardless of what
      // thread PauseAsync's own internals (which do use ConfigureAwait(false)) complete on.
      await Timer.PauseAsync();
      UpdateDisplay();
    };

    _lapButton = CreateButton("Lap", Palette.LapButton);
    _lapButton.Click += (_, _) =>
    {
      Timer.Lap();
      UpdateDisplay();
    };

    _stopButton = CreateButton("Stop", Palette.StopButton);
    _stopButton.Click += async (_, _) =>
    {
      await Timer.StopAsync();
      UpdateDisplay();
    };

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
  }

  /// <inheritdoc />
  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _uiTimer.Stop();
      _uiTimer.Dispose();
    }
    base.Dispose(disposing);
  }

  private static Button CreateButton(string text, (Color Base, Color Hover, Color Pressed) colors)
  {
    Button button = new()
    {
      Text = text,
      AutoSize = true,
      Margin = new Padding(4),
      FlatStyle = FlatStyle.Flat,
      ForeColor = Color.White,
      BackColor = colors.Base,
    };
    button.FlatAppearance.BorderSize = 0;
    button.FlatAppearance.MouseOverBackColor = colors.Hover;
    button.FlatAppearance.MouseDownBackColor = colors.Pressed;
    return button;
  }

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
}
