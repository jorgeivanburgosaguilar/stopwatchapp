using StopwatchApp.Formatting;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// Renders the laps panel (shown only while there is at least one lap) and the records panel
/// (always shown, with a "No records yet" empty state), per AGENTS.md §8.5. Data is pushed in via
/// <see cref="UpdateRecords"/>/<see cref="UpdateLaps"/> — this control never reads
/// <c>IStopwatchStore</c> itself; it only raises <see cref="ClearAllRequested"/> and
/// lets its owner (<c>MainForm</c>, S7) perform the actual clear.
/// </summary>
public sealed class RecordsListControl : UserControl
{
  private readonly Label _lapsHeader;
  private readonly ListBox _lapsListBox;
  private readonly ListBox _recordsListBox;
  private readonly Label _emptyStateLabel;
  private readonly Button _clearAllButton;
  private bool _dark;

  /// <summary>
  /// Initializes a new instance of the <see cref="RecordsListControl"/> class.
  /// </summary>
  public RecordsListControl()
  {
    Dock = DockStyle.Fill;

    _lapsHeader = new Label
    {
      Text = "Laps",
      AutoSize = true,
      Margin = new Padding(0, 0, 0, 4),
    };
    _lapsListBox = new ListBox
    {
      Dock = DockStyle.Top,
      Height = 120,
      IntegralHeight = false,
      Margin = new Padding(0, 0, 0, 8),
    };

    Label recordsHeader = new()
    {
      Text = "Records",
      AutoSize = true,
      Margin = new Padding(0, 0, 0, 4),
    };
    _recordsListBox = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    _emptyStateLabel = new Label
    {
      Text = "No records yet",
      Dock = DockStyle.Fill,
      TextAlign = ContentAlignment.MiddleCenter,
      Visible = false,
    };

    _clearAllButton = new Button
    {
      Text = "Clear All Records",
      AutoSize = true,
      Margin = new Padding(0, 8, 0, 8),
      Visible = false,
    };
    _clearAllButton.Click += (_, _) => ClearAllRequested?.Invoke();

    Panel recordsHost = new() { Dock = DockStyle.Fill };
    recordsHost.Controls.Add(_recordsListBox);
    recordsHost.Controls.Add(_emptyStateLabel);

    TableLayoutPanel layout = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 5,
    };
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    // Rows, top to bottom: laps header, laps list, records header, clear-all button, records host.
    layout.Controls.Add(_lapsHeader, 0, 0);
    layout.Controls.Add(_lapsListBox, 0, 1);
    layout.Controls.Add(recordsHeader, 0, 2);
    layout.Controls.Add(_clearAllButton, 0, 3);
    layout.Controls.Add(recordsHost, 0, 4);

    Controls.Add(layout);

    UpdateRecords([]);
    UpdateLaps([]);
    ApplyTheme();
  }

  /// <summary>Fires when the user clicks the "Clear All Records" button.</summary>
  public event Action? ClearAllRequested;

  /// <summary>
  /// Gets or sets whether dark-theme colors should be used for the surfaces
  /// <see cref="Theme.Palette"/> covers (e.g. the empty-state text). Stock controls already follow
  /// <c>Application.SetColorMode</c> on their own; this only affects the colors Palette supplies.
  /// Defaults to <see langword="false"/> — wiring it to the live OS setting is <c>S12</c>'s job.
  /// </summary>
  [System.ComponentModel.DesignerSerializationVisibility(
    System.ComponentModel.DesignerSerializationVisibility.Hidden
  )]
  public bool Dark
  {
    get => _dark;
    set
    {
      if (_dark == value)
      {
        return;
      }

      _dark = value;
      ApplyTheme();
    }
  }

  /// <summary>
  /// Replaces the displayed records, expected newest first (per <c>IStopwatchStore.GetAllRecordsAsync</c>).
  /// Toggles the empty state and the "Clear All Records" button's visibility.
  /// </summary>
  /// <param name="records">The records to display.</param>
  public void UpdateRecords(IReadOnlyList<StopwatchRecord> records)
  {
    _recordsListBox.BeginUpdate();
    _recordsListBox.Items.Clear();
    foreach (StopwatchRecord record in records)
    {
      _recordsListBox.Items.Add(FormatRecordRow(record));
    }
    _recordsListBox.EndUpdate();

    bool hasRecords = records.Count > 0;
    _recordsListBox.Visible = hasRecords;
    _emptyStateLabel.Visible = !hasRecords;
    _clearAllButton.Visible = hasRecords;
  }

  /// <summary>
  /// Replaces the displayed laps, expected newest first. Toggles the laps panel's visibility.
  /// </summary>
  /// <param name="laps">The laps to display.</param>
  public void UpdateLaps(IReadOnlyList<Lap> laps)
  {
    _lapsListBox.BeginUpdate();
    _lapsListBox.Items.Clear();
    foreach (Lap lap in laps)
    {
      _lapsListBox.Items.Add(FormatLapRow(lap));
    }
    _lapsListBox.EndUpdate();

    bool hasLaps = laps.Count > 0;
    _lapsHeader.Visible = hasLaps;
    _lapsListBox.Visible = hasLaps;
  }

  /// <summary>
  /// Formats a lap row exactly as AGENTS.md §8.5 specifies:
  /// <c>📅 {date} ⏱ {start}-{end} ⏳ Lap {id}: {formatElapsed}</c>. Internal (not part of §3.1's
  /// fixed contracts) and covered directly by <c>RecordsListControlTests</c>.
  /// </summary>
  /// <param name="lap">The lap to format.</param>
  internal static string FormatLapRow(Lap lap) =>
    $"📅 {TimeFormat.FormatDate(lap.StartTimestamp)} "
    + $"⏱ {TimeFormat.FormatTimeOnly(lap.StartTimestamp)}-{TimeFormat.FormatTimeOnly(lap.EndTimestamp)} "
    + $"⏳ Lap {lap.Id}: {TimeFormat.FormatElapsed(lap.ElapsedMinutes)}";

  /// <summary>
  /// Formats a record row exactly as AGENTS.md §8.5 specifies:
  /// <c>📅 {date} ⏱ {start}-{end} ⏳ Duration: {formatElapsed}</c>. Internal (not part of §3.1's
  /// fixed contracts) and covered directly by <c>RecordsListControlTests</c>.
  /// </summary>
  /// <param name="record">The record to format.</param>
  internal static string FormatRecordRow(StopwatchRecord record) =>
    $"📅 {TimeFormat.FormatDate(record.StartTimestamp)} "
    + $"⏱ {TimeFormat.FormatTimeOnly(record.StartTimestamp)}-{TimeFormat.FormatTimeOnly(record.EndTimestamp)} "
    + $"⏳ Duration: {TimeFormat.FormatElapsed(record.ElapsedMinutes)}";

  private void ApplyTheme()
  {
    _emptyStateLabel.ForeColor = Palette.EmptyStateText(_dark);
  }
}
