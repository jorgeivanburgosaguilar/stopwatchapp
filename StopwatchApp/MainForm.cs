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
    _recordsListControl.ClearAllRequested += ClearRecordsAsync;
    Load += InitializeAsync;
    FormClosed += DisposeDatabaseAsync;
  }

  private async void InitializeAsync(object? sender, EventArgs e)
  {
    await _database.InitializeAsync();
    await _stopwatchControl.RestoreAsync();
    RefreshLists();
  }

  private async void ClearRecordsAsync()
  {
    if (ClearRecordsDialog.ShowConfirm(this) != DialogResult.Yes)
    {
      return;
    }

    await _stopwatchControl.Timer.ClearRecordsAsync();
  }

  private async void DisposeDatabaseAsync(object? sender, FormClosedEventArgs e)
  {
    await _database.DisposeAsync();
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
