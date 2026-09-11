using Microsoft.Data.Sqlite;
using StopwatchApp.Controls;
using StopwatchApp.Services;

namespace StopwatchApp;

/// <summary>
/// The application's main window. A thin orchestrator that wires controls and services together;
/// it contains no business logic of its own (see AGENTS.md §3).
/// </summary>
public sealed class MainForm : Form
{
  private readonly Database _database;
  private readonly StopwatchControl _stopwatchControl;
  private readonly RecordsListControl _recordsListControl;
  private readonly TrayIconService _trayIconService;

  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  public MainForm()
  {
    Text = "Stopwatch";
    ClientSize = new Size(560, 600);
    MinimumSize = new Size(400, 400);
    StartPosition = FormStartPosition.CenterScreen;

    _database = new Database(Database.DefaultDatabasePath);
    _stopwatchControl = new StopwatchControl(_database, TimeProvider.System)
    {
      Dock = DockStyle.Fill,
    };
    _recordsListControl = new RecordsListControl();
    _trayIconService = new TrayIconService(_stopwatchControl, RestoreWindow, ExitApplication);

    TableLayoutPanel layout = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 2,
      Padding = new Padding(12),
    };
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    layout.Controls.Add(_stopwatchControl, 0, 0);
    layout.Controls.Add(_recordsListControl, 0, 1);
    Controls.Add(layout);

    _stopwatchControl.Timer.RecordsChanged += RefreshRecords;
    _stopwatchControl.StateChanged += RefreshLaps;
    _stopwatchControl.StateChanged += RefreshTray;
    _stopwatchControl.Tick += RefreshTray;
    _recordsListControl.ClearAllRequested += ClearRecordsAsync;
    Load += InitializeAsync;
  }

  /// <inheritdoc />
  protected override void OnFormClosing(FormClosingEventArgs e)
  {
    // A plain Close() call (the DB-failure path in InitializeAsync) reports CloseReason.None, not
    // UserClosing, so it is unaffected by this and falls through to a real close. Only the window
    // chrome's own close button/Alt+F4/system-menu Close reports UserClosing (AGENTS.md §10.3).
    if (e.CloseReason == CloseReason.UserClosing)
    {
      e.Cancel = true;
      HideToTray();
      return;
    }

    base.OnFormClosing(e);
  }

  /// <inheritdoc />
  protected override void OnResize(EventArgs e)
  {
    base.OnResize(e);
    if (WindowState == FormWindowState.Minimized)
    {
      HideToTray();
    }
  }

  private void HideToTray()
  {
    Hide();
    ShowInTaskbar = false;
  }

  private void RestoreWindow()
  {
    Show();
    ShowInTaskbar = true;
    WindowState = FormWindowState.Normal;
    Activate();
  }

  private async void ExitApplication()
  {
    // Dispose the tray icon before Application.Exit() — otherwise a ghost icon lingers in the
    // tray until the user hovers over its former location (AGENTS.md §10.3). Application.Exit()
    // is called only from here, the tray menu's Exit item, per the same section.
    _trayIconService.Dispose();
    await _database.DisposeAsync();
    Application.Exit();
  }

  private async void InitializeAsync(object? sender, EventArgs e)
  {
    try
    {
      await _database.InitializeAsync();
    }
    catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
    {
      // Deliberately not swallowed the way the rest of the storage layer is (AGENTS.md §9 scopes
      // that rule to individual reads/writes, not schema initialization) — a half-migrated schema
      // must not pass silently. Report it and close rather than run against a broken database.
      MessageBox.Show(
        this,
        $"Failed to open the database at \"{Database.DefaultDatabasePath}\":"
          + $"{Environment.NewLine}{Environment.NewLine}{ex.Message}",
        "Stopwatch",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error
      );
      Close();
      return;
    }

    await _stopwatchControl.RestoreAsync();
    RefreshLists();
    RefreshTray();
  }

  private async void ClearRecordsAsync()
  {
    if (ClearRecordsDialog.ShowConfirm(this) != DialogResult.Yes)
    {
      return;
    }

    await _stopwatchControl.Timer.ClearRecordsAsync();
  }

  private void RefreshLists()
  {
    RefreshRecords();
    RefreshLaps();
  }

  private void RefreshRecords() =>
    InvokeOnUiThread(() => _recordsListControl.UpdateRecords(_stopwatchControl.Timer.Records));

  private void RefreshLaps() =>
    InvokeOnUiThread(() => _recordsListControl.UpdateLaps(_stopwatchControl.Timer.Laps));

  private void RefreshTray() =>
    InvokeOnUiThread(() =>
      _trayIconService.UpdateDisplay(
        _stopwatchControl.Timer.ElapsedMs,
        _stopwatchControl.Timer.IsRunning,
        _stopwatchControl.Timer.IsPaused
      )
    );

  private void InvokeOnUiThread(Action action)
  {
    if (IsDisposed || Disposing || !IsHandleCreated)
    {
      return;
    }

    if (InvokeRequired)
    {
      Invoke(action);
      return;
    }

    action();
  }
}
