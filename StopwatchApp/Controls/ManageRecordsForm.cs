using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>A modeless, paginated view of every persisted stopwatch record.</summary>
internal sealed class ManageRecordsForm : Form
{
  internal const int PageSize = 10;
  private const int DesignClientHeight = 660;
  private const int CompactClientWidth = 640;

  private readonly Func<long, Task> _deleteRecordAsync;
  private readonly Func<Task> _clearRecordsAsync;
  private readonly Font _bodyFont;
  private readonly Font _rowFont;
  private readonly Icon _appIcon;
  private readonly Label _pageLabel;
  private readonly Label _emptyStateLabel;
  private readonly TableLayoutPanel _rowsLayout;
  private readonly TableLayoutPanel _rootLayout;
  private readonly Button _clearAllButton;
  private readonly Button _previousButton;
  private readonly Button _nextButton;
  private readonly List<Label> _recordDetails = [];
  private IReadOnlyList<StopwatchRecord> _records;
  private int _pageIndex;
  private bool _dark;
  private bool _operationInProgress;

  internal ManageRecordsForm(
    IReadOnlyList<StopwatchRecord> records,
    Func<long, Task> deleteRecordAsync,
    Func<Task> clearRecordsAsync,
    Icon appIcon
  )
  {
    _records = records;
    _deleteRecordAsync = deleteRecordAsync;
    _clearRecordsAsync = clearRecordsAsync;
    _appIcon = (Icon)appIcon.Clone();

    Text = "Manage Records";
    Icon = _appIcon;
    FormBorderStyle = FormBorderStyle.FixedSingle;
    MaximizeBox = false;
    MinimizeBox = false;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.CenterScreen;
    AutoScaleMode = AutoScaleMode.Dpi;
    AutoScaleDimensions = new SizeF(96F, 96F);
    ClientSize = new Size(1, DesignClientHeight);
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
    _clearAllButton = ButtonFactory.Create("Clear All Records", Palette.StopButton);
    _clearAllButton.Anchor = AnchorStyles.Right;
    _clearAllButton.Margin = new Padding(0);
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

    _previousButton = ButtonFactory.Create("Previous", Palette.CancelButton);
    _previousButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);
    _previousButton.Click += (_, _) => ChangePage(-1);
    _pageLabel = new Label
    {
      AutoSize = true,
      Anchor = AnchorStyles.None,
      Margin = new Padding(Palette.SpacingSm, 0, Palette.SpacingSm, 0),
    };
    _nextButton = ButtonFactory.Create("Next", Palette.LapButton);
    _nextButton.Margin = new Padding(0);
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

    _rootLayout = new TableLayoutPanel
    {
      Dock = DockStyle.Fill,
      ColumnCount = 1,
      RowCount = 3,
      Padding = new Padding(Palette.SpacingLg),
    };
    _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    _rootLayout.Controls.Add(header, 0, 0);
    _rootLayout.Controls.Add(rowsHost, 0, 1);
    _rootLayout.Controls.Add(pagination, 0, 2);
    Controls.Add(_rootLayout);

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
      _appIcon.Dispose();
    }

    base.Dispose(disposing);
  }

  /// <inheritdoc />
  protected override void OnDpiChanged(DpiChangedEventArgs e)
  {
    base.OnDpiChanged(e);
    ResizeToCurrentPage(e.DeviceDpiNew);
  }

  /// <inheritdoc />
  protected override void OnShown(EventArgs e)
  {
    base.OnShown(e);
    ResizeToCurrentPage(DeviceDpi);
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
    _recordDetails.Clear();

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
    ApplyTheme();
    ResizeToCurrentPage(DeviceDpi);
  }

  private void ResizeToCurrentPage(int deviceDpi)
  {
    int contentWidth = RequiredClientWidth(
      deviceDpi,
      GetPage(_records, _pageIndex),
      _records.Count
    );
    int compactWidth = (int)Math.Ceiling(CompactClientWidth * (deviceDpi / 96f));
    int width = Math.Min(contentWidth, compactWidth);
    int height = (int)Math.Ceiling(DesignClientHeight * (deviceDpi / 96f));
    Screen screen = Screen.FromControl(this);
    int chromeWidth = SystemInformation.FixedFrameBorderSize.Width * 2;
    int chromeHeight = SystemInformation.FixedFrameBorderSize.Height * 2;
    Size clientSize = new(
      Math.Max(1, Math.Min(width, screen.WorkingArea.Width - chromeWidth)),
      Math.Max(1, Math.Min(height, screen.WorkingArea.Height - chromeHeight))
    );
    if (ClientSize != clientSize)
    {
      ClientSize = clientSize;
    }
    ConstrainRecordDetails();
    if (Visible)
    {
      CenterOnCurrentScreen(screen);
    }
  }

  private void CenterOnCurrentScreen(Screen screen)
  {
    Location = new Point(
      screen.WorkingArea.Left + ((screen.WorkingArea.Width - Width) / 2),
      screen.WorkingArea.Top + ((screen.WorkingArea.Height - Height) / 2)
    );
  }

  private void ConstrainRecordDetails()
  {
    int deleteButtonWidth =
      TextRenderer
        .MeasureText(
          "Delete",
          Font,
          Size.Empty,
          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        )
        .Width + (Palette.SpacingMd * 2);
    int rowTextWidth = Math.Max(
      1,
      ClientSize.Width
        - (Palette.SpacingLg * 2)
        - SystemInformation.VerticalScrollBarWidth
        - deleteButtonWidth
        - (Palette.SpacingSm * 3)
        - 2
    );
    foreach (Label details in _recordDetails)
    {
      details.MaximumSize = new Size(rowTextWidth, 0);
    }
  }

  internal static int RequiredClientWidth(
    int deviceDpi,
    IReadOnlyList<StopwatchRecord> page,
    int totalRecordCount
  )
  {
    float scale = deviceDpi / 96f;
    using Font bodyFont = Typography.CreateBodyFont();
    using Font scaledBodyFont = new(bodyFont.FontFamily, bodyFont.Size * scale, bodyFont.Style);
    using Font rowFont = Typography.CreateMonospaceBodyFont();
    using Font scaledRowFont = new(rowFont.FontFamily, rowFont.Size * scale, rowFont.Style);

    int TextWidth(string text, Font font) =>
      TextRenderer
        .MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine)
        .Width;
    int ButtonWidth(string text) =>
      TextWidth(text, scaledBodyFont) + (int)Math.Ceiling(Palette.SpacingMd * 2 * scale);

    int rowTextWidth = page.Select(record =>
        TextWidth(RecordsListControl.FormatRecordRow(record), scaledRowFont)
      )
      .DefaultIfEmpty(0)
      .Max();
    int rowWidth =
      rowTextWidth + ButtonWidth("Delete") + (int)Math.Ceiling((Palette.SpacingSm * 3 + 2) * scale);
    int headerWidth =
      TextWidth("Manage Records", scaledBodyFont) + ButtonWidth("Clear All Records");
    int paginationWidth =
      ButtonWidth("Previous")
      + TextWidth(
        $"Page {GetPageCount(totalRecordCount)} of {GetPageCount(totalRecordCount)}",
        scaledBodyFont
      )
      + ButtonWidth("Next")
      + (int)Math.Ceiling(Palette.SpacingSm * 3 * scale);
    int rootChrome = (int)
      Math.Ceiling((Palette.SpacingLg * 2 + SystemInformation.VerticalScrollBarWidth) * scale);
    return Math.Max(Math.Max(rowWidth, headerWidth), paginationWidth) + rootChrome;
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
    _recordDetails.Add(details);
    Button deleteButton = ButtonFactory.Create("Delete", Palette.StopButton);
    deleteButton.Anchor = AnchorStyles.Right;
    deleteButton.Margin = new Padding(Palette.SpacingSm, 0, 0, 0);
    deleteButton.Enabled = !_operationInProgress;
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
  }
}
