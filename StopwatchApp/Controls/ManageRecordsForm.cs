using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>A modeless, paginated view of every persisted stopwatch record.</summary>
internal sealed class ManageRecordsForm : Form
{
  internal const int PageSize = 10;

  private readonly Func<long, Task> _deleteRecordAsync;
  private readonly Func<Task> _clearRecordsAsync;
  private readonly Font _bodyFont;
  private readonly Font _rowFont;
  private readonly Label _pageLabel;
  private readonly Label _emptyStateLabel;
  private readonly TableLayoutPanel _rowsLayout;
  private readonly GlyphButton _clearAllButton;
  private readonly GlyphButton _previousButton;
  private readonly GlyphButton _nextButton;
  private IReadOnlyList<StopwatchRecord> _records;
  private int _pageIndex;
  private bool _dark;
  private bool _operationInProgress;

  internal ManageRecordsForm(
    IReadOnlyList<StopwatchRecord> records,
    Func<long, Task> deleteRecordAsync,
    Func<Task> clearRecordsAsync
  )
  {
    _records = records;
    _deleteRecordAsync = deleteRecordAsync;
    _clearRecordsAsync = clearRecordsAsync;

    Text = "Manage Records";
    FormBorderStyle = FormBorderStyle.FixedSingle;
    MaximizeBox = false;
    MinimizeBox = false;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.CenterParent;
    AutoScaleMode = AutoScaleMode.Dpi;
    AutoScaleDimensions = new SizeF(96F, 96F);
    ClientSize = new Size(720, 660);
    _bodyFont = Typography.CreateBodyFont();
    _rowFont = Typography.CreateMonospaceBodyFont();
    Font = _bodyFont;

    Label heading = new()
    {
      Text = "Manage Records",
      AutoSize = true,
      Anchor = AnchorStyles.Left,
      Margin = new Padding(0),
    };
    _clearAllButton = new GlyphButton("Clear All Records", glyph: null, Palette.StopButton)
    {
      Anchor = AnchorStyles.Right,
      Margin = new Padding(0),
    };
    _clearAllButton.Click += async (_, _) => await ClearAllRecordsAsync();

    TableLayoutPanel header = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 2,
      RowCount = 1,
      Margin = new Padding(0, 0, 0, Palette.SpacingMd),
    };
    header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    header.Controls.Add(heading, 0, 0);
    header.Controls.Add(_clearAllButton, 1, 0);

    _rowsLayout = new TableLayoutPanel
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      Margin = new Padding(0),
    };
    _rowsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    Panel rowsHost = new()
    {
      Dock = DockStyle.Fill,
      AutoScroll = true,
      Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0),
    };
    rowsHost.Controls.Add(_rowsLayout);
    _emptyStateLabel = new Label
    {
      Text = "No records yet",
      Dock = DockStyle.Fill,
      TextAlign = ContentAlignment.MiddleCenter,
      Visible = false,
    };
    rowsHost.Controls.Add(_emptyStateLabel);

    _previousButton = new GlyphButton("Previous", glyph: null, Palette.CancelButton)
    {
      Margin = new Padding(0, 0, Palette.SpacingSm, 0),
    };
    _previousButton.Click += (_, _) => ChangePage(-1);
    _pageLabel = new Label
    {
      AutoSize = true,
      Anchor = AnchorStyles.None,
      Margin = new Padding(Palette.SpacingSm, 0, Palette.SpacingSm, 0),
    };
    _nextButton = new GlyphButton("Next", glyph: null, Palette.LapButton)
    {
      Margin = new Padding(0),
    };
    _nextButton.Click += (_, _) => ChangePage(1);
    FlowLayoutPanel pagination = new()
    {
      FlowDirection = FlowDirection.LeftToRight,
      AutoSize = true,
      Anchor = AnchorStyles.None,
      WrapContents = false,
    };
    pagination.Controls.Add(_previousButton);
    pagination.Controls.Add(_pageLabel);
    pagination.Controls.Add(_nextButton);

    TableLayoutPanel root = new()
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 3,
      Padding = new Padding(Palette.SpacingLg),
    };
    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    root.Controls.Add(header, 0, 0);
    root.Controls.Add(rowsHost, 0, 1);
    root.Controls.Add(pagination, 0, 2);
    Controls.Add(root);

    UpdateRecords(records);
    ApplyTheme();
  }

  [System.ComponentModel.DesignerSerializationVisibility(
    System.ComponentModel.DesignerSerializationVisibility.Hidden
  )]
  internal bool DarkMode
  {
    get => _dark;
    set
    {
      if (_dark == value)
      {
        return;
      }

      _dark = value;
      RebuildRows();
    }
  }

  internal void UpdateRecords(IReadOnlyList<StopwatchRecord> records)
  {
    _records = records;
    _pageIndex = ClampPageIndex(_pageIndex, records.Count);
    RebuildRows();
  }

  internal static int GetPageCount(int recordCount) =>
    Math.Max(1, (recordCount + PageSize - 1) / PageSize);

  internal static int ClampPageIndex(int pageIndex, int recordCount) =>
    Math.Clamp(pageIndex, 0, GetPageCount(recordCount) - 1);

  internal static IReadOnlyList<StopwatchRecord> GetPage(
    IReadOnlyList<StopwatchRecord> records,
    int pageIndex
  )
  {
    int clampedPageIndex = ClampPageIndex(pageIndex, records.Count);
    return records.Skip(clampedPageIndex * PageSize).Take(PageSize).ToArray();
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _bodyFont.Dispose();
      _rowFont.Dispose();
    }

    base.Dispose(disposing);
  }

  private void ChangePage(int offset)
  {
    _pageIndex = ClampPageIndex(_pageIndex + offset, _records.Count);
    RebuildRows();
  }

  private async Task DeleteRecordAsync(long id)
  {
    if (_operationInProgress || DeleteRecordDialog.ShowConfirm(this) != DialogResult.Yes)
    {
      return;
    }

    SetOperationInProgress(true);
    try
    {
      await _deleteRecordAsync(id);
    }
    finally
    {
      SetOperationInProgress(false);
    }
  }

  private async Task ClearAllRecordsAsync()
  {
    if (_operationInProgress || ClearRecordsDialog.ShowConfirm(this) != DialogResult.Yes)
    {
      return;
    }

    SetOperationInProgress(true);
    try
    {
      await _clearRecordsAsync();
    }
    finally
    {
      SetOperationInProgress(false);
    }
  }

  private void SetOperationInProgress(bool operationInProgress)
  {
    _operationInProgress = operationInProgress;
    RebuildRows();
  }

  private void RebuildRows()
  {
    _rowsLayout.SuspendLayout();
    _rowsLayout.Controls.Clear();
    _rowsLayout.RowStyles.Clear();

    IReadOnlyList<StopwatchRecord> page = GetPage(_records, _pageIndex);
    for (int i = 0; i < page.Count; i++)
    {
      StopwatchRecord record = page[i];
      Panel row = CreateRecordRow(record);
      _rowsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      _rowsLayout.Controls.Add(row, 0, i);
    }

    _rowsLayout.ResumeLayout();
    bool hasRecords = _records.Count > 0;
    _rowsLayout.Visible = hasRecords;
    _emptyStateLabel.Visible = !hasRecords;
    int pageCount = GetPageCount(_records.Count);
    _pageLabel.Text = $"Page {_pageIndex + 1} of {pageCount}";
    _clearAllButton.Enabled = hasRecords && !_operationInProgress;
    _previousButton.Enabled = _pageIndex > 0 && !_operationInProgress;
    _nextButton.Enabled = _pageIndex < pageCount - 1 && !_operationInProgress;
    _clearAllButton.DarkMode = _dark;
    _previousButton.DarkMode = _dark;
    _nextButton.DarkMode = _dark;
    ApplyTheme();
  }

  private Panel CreateRecordRow(StopwatchRecord record)
  {
    Label details = new()
    {
      Text = RecordsListControl.FormatRecordRow(record),
      Dock = DockStyle.Fill,
      Font = _rowFont,
      AutoSize = true,
      Margin = new Padding(0),
    };
    GlyphButton deleteButton = new("Delete", glyph: null, Palette.StopButton)
    {
      Anchor = AnchorStyles.Right,
      Margin = new Padding(Palette.SpacingSm, 0, 0, 0),
      DarkMode = _dark,
      Enabled = !_operationInProgress,
    };
    deleteButton.Click += async (_, _) => await DeleteRecordAsync(record.Id);

    TableLayoutPanel content = new()
    {
      // An AutoSize panel cannot derive a height from a Dock.Fill child: Fill consumes the space the
      // parent has already allocated rather than contributing its preferred height. Dock.Top keeps
      // this row full-width while allowing the record label and Delete button to determine the row
      // height, instead of collapsing every record into a thin line.
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 2,
      RowCount = 1,
      Padding = new Padding(Palette.SpacingSm),
      Margin = new Padding(0),
    };
    content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    content.Controls.Add(details, 0, 0);
    content.Controls.Add(deleteButton, 1, 0);

    Panel row = new()
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Margin = new Padding(0, 0, 0, Palette.SpacingXs),
      Padding = new Padding(1),
      BackColor = Palette.Border(_dark),
    };
    content.BackColor = Palette.RowBackground(_dark);
    details.ForeColor = Palette.Text(_dark);
    row.Controls.Add(content);
    return row;
  }

  private void ApplyTheme()
  {
    BackColor = Palette.CardBackground(_dark);
    ForeColor = Palette.Text(_dark);
    _emptyStateLabel.ForeColor = Palette.EmptyStateText(_dark);
    _pageLabel.ForeColor = Palette.MutedText(_dark);
    _clearAllButton.DarkMode = _dark;
    _previousButton.DarkMode = _dark;
    _nextButton.DarkMode = _dark;
    _clearAllButton.Invalidate();
    _previousButton.Invalidate();
    _nextButton.Invalidate();
  }
}
