using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>A modeless, paginated view of every persisted stopwatch record.</summary>
internal sealed class ManageRecordsForm : Form
{
  internal const int PageSize = 10;
  private const int DesignClientHeight = 660;

  // The two expand-toggle glyph states, both the same width (a filled triangle), so the toggle
  // column never reflows when a row flips between collapsed and expanded.
  private const string CollapsedGlyph = "▸";
  private const string ExpandedGlyph = "▾";

  private readonly Func<long, Task> _deleteRecordAsync;
  private readonly Func<Task> _clearRecordsAsync;
  private readonly Func<long, Task<IReadOnlyList<Lap>>> _getLapsAsync;
  private readonly Font _bodyFont;
  private readonly Font _rowFont;
  private readonly RowIconSet _rowIcons = new();
  private readonly Icon _appIcon;
  private readonly Label _pageLabel;
  private readonly Label _emptyStateLabel;
  private readonly TableLayoutPanel _rowsLayout;
  private readonly TableLayoutPanel _rootLayout;
  private readonly Button _clearAllButton;
  private readonly Button _previousButton;
  private readonly Button _nextButton;
  private readonly List<RecordRowParts> _rows = [];

  // Expansion survives a page turn, a theme flip, and a records reload; the lap cache is
  // per-record and repopulated lazily on first expand, and reset whenever the record set changes
  // (AGENTS.md §8.5 — master-detail laps).
  private readonly HashSet<long> _expandedRecordIds = [];
  private readonly HashSet<long> _loadingLapRecordIds = [];
  private readonly Dictionary<long, IReadOnlyList<Lap>> _lapCache = [];
  private IReadOnlyList<StopwatchRecord> _records;
  private int _pageIndex;
  private bool _dark;
  private bool _operationInProgress;

  internal ManageRecordsForm(
    IReadOnlyList<StopwatchRecord> records,
    Func<long, Task> deleteRecordAsync,
    Func<Task> clearRecordsAsync,
    Func<long, Task<IReadOnlyList<Lap>>> getLapsAsync,
    Icon appIcon
  )
  {
    _records = records;
    _deleteRecordAsync = deleteRecordAsync;
    _clearRecordsAsync = clearRecordsAsync;
    _getLapsAsync = getLapsAsync;
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
    // The record set changed (a save/delete/clear), so any cached laps could now be stale; drop
    // them and re-fetch lazily for whatever stays expanded. Expansion itself is kept, pruned to
    // the records that still exist, so a refresh doesn't silently collapse the user's view.
    _lapCache.Clear();
    _expandedRecordIds.IntersectWith(records.Select(record => record.Id).ToHashSet());
    RebuildRows();
    RefreshExpandedLapsForCurrentPage();
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
      _rowIcons.Dispose();
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

  private async Task ToggleLapsAsync(long id)
  {
    if (_loadingLapRecordIds.Contains(id))
    {
      return;
    }

    if (_expandedRecordIds.Remove(id))
    {
      ApplyExpansionToVisibleRow(id);
      return;
    }

    _expandedRecordIds.Add(id);
    await EnsureLapsLoadedAsync(id);
    ApplyExpansionToVisibleRow(id);
  }

  private async Task EnsureLapsLoadedAsync(long id)
  {
    if (_lapCache.ContainsKey(id))
    {
      return;
    }

    _loadingLapRecordIds.Add(id);
    ApplyExpansionToVisibleRow(id);
    try
    {
      IReadOnlyList<Lap> laps = await _getLapsAsync(id);
      _lapCache[id] = laps;
    }
    finally
    {
      _loadingLapRecordIds.Remove(id);
    }
  }

  /// <summary>
  /// Re-fetches laps for every record on the current page that is expanded but missing from the
  /// (just-cleared) cache — called after <see cref="UpdateRecords"/> so an expanded row's laps
  /// come back instead of silently showing empty until the user collapses and re-expands it.
  /// </summary>
  private void RefreshExpandedLapsForCurrentPage()
  {
    foreach (RecordRowParts parts in _rows)
    {
      if (
        parts.Delete.Tag is long id
        && _expandedRecordIds.Contains(id)
        && !_lapCache.ContainsKey(id)
      )
      {
        _ = ReloadLapsForRowAsync(id);
      }
    }
  }

  private async Task ReloadLapsForRowAsync(long id)
  {
    await EnsureLapsLoadedAsync(id);
    ApplyExpansionToVisibleRow(id);
  }

  private RecordRowParts? FindVisibleRow(long id) =>
    _rows.Find(parts => parts.Delete.Tag is long tagId && tagId == id);

  private void ApplyExpansionToVisibleRow(long id)
  {
    RecordRowParts? parts = FindVisibleRow(id);
    if (parts is not null)
    {
      ApplyExpansionToRow(parts, id);
    }
  }

  private void ApplyExpansionToRow(RecordRowParts parts, long id)
  {
    bool expanded = _expandedRecordIds.Contains(id);
    bool loading = _loadingLapRecordIds.Contains(id);
    parts.Toggle.Text = expanded ? ExpandedGlyph : CollapsedGlyph;
    parts.Toggle.AccessibleName = expanded ? "Hide laps" : "Show laps";
    parts.Toggle.Enabled = !loading && !_operationInProgress;

    IReadOnlyList<Lap> laps =
      expanded && _lapCache.TryGetValue(id, out IReadOnlyList<Lap>? cached) ? cached : [];
    PopulateLaps(parts, laps);
    parts.LapsPanel.Visible = expanded && laps.Count > 0;
  }

  private void PopulateLaps(RecordRowParts parts, IReadOnlyList<Lap> laps)
  {
    // Pooled exactly like the record rows themselves (AGENTS.md §17): a label is created or
    // disposed only when this row's lap count changes, otherwise its text is reassigned.
    while (parts.LapLabels.Count > laps.Count)
    {
      IconTextLabel surplus = parts.LapLabels[^1];
      parts.LapLabels.RemoveAt(parts.LapLabels.Count - 1);
      parts.LapsPanel.Controls.Remove(surplus);
      surplus.Dispose();
    }
    while (parts.LapsPanel.RowStyles.Count > parts.LapLabels.Count)
    {
      parts.LapsPanel.RowStyles.RemoveAt(parts.LapsPanel.RowStyles.Count - 1);
    }

    for (int i = 0; i < laps.Count; i++)
    {
      if (i >= parts.LapLabels.Count)
      {
        IconTextLabel lapLabel = new(_rowIcons)
        {
          Dock = DockStyle.Fill,
          Font = _rowFont,
          AutoSize = true,
          Margin = new Padding(0, 0, 0, Palette.SpacingXs),
        };
        if (ClientSize.Width > 1)
        {
          lapLabel.MaximumSize = new Size(Math.Max(1, RowTextWidth() - LapIndentWidth()), 0);
        }
        parts.LapLabels.Add(lapLabel);
        parts.LapsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        parts.LapsPanel.Controls.Add(lapLabel, 0, i);
      }
      parts.LapLabels[i].Text = RecordsListControl.FormatLapRow(laps[i]);
      parts.LapLabels[i].ForeColor = Palette.Text(_dark);
    }
  }

  private int LapIndentWidth() => (int)Math.Ceiling(Palette.SpacingLg * (DeviceDpi / 96f));

  private void SetOperationInProgress(bool operationInProgress)
  {
    _operationInProgress = operationInProgress;
    // Only enablement changes; rebuilding every row for that (and again when the records reload)
    // made each delete recreate the whole page several times.
    UpdateButtonStates();
  }

  private void UpdateButtonStates()
  {
    int pageCount = GetPageCount(_records.Count);
    _clearAllButton.Enabled = _records.Count > 0 && !_operationInProgress;
    _previousButton.Enabled = _pageIndex > 0 && !_operationInProgress;
    _nextButton.Enabled = _pageIndex < pageCount - 1 && !_operationInProgress;
    foreach (RecordRowParts parts in _rows)
    {
      parts.Delete.Enabled = !_operationInProgress;
      bool loading = parts.Toggle.Tag is long id && _loadingLapRecordIds.Contains(id);
      parts.Toggle.Enabled = !_operationInProgress && !loading;
    }
  }

  private void RebuildRows()
  {
    _rowsLayout.SuspendLayout();
    IReadOnlyList<StopwatchRecord> page = GetPage(_records, _pageIndex);

    // Reuse the existing row controls and only change what each shows: creating a row is a nest of
    // panels, a table layout and a button, so recreating a whole page for every delete or page turn
    // was what made the window lag. Rows are created or disposed only when the page count changes.
    // (Controls.Remove alone would leak them, so surplus rows are disposed explicitly.)
    while (_rows.Count > page.Count)
    {
      RecordRowParts surplus = _rows[^1];
      _rows.RemoveAt(_rows.Count - 1);
      _rowsLayout.Controls.Remove(surplus.Row);
      surplus.Row.Dispose();
    }
    while (_rowsLayout.RowStyles.Count > _rows.Count)
    {
      _rowsLayout.RowStyles.RemoveAt(_rowsLayout.RowStyles.Count - 1);
    }

    for (int i = 0; i < page.Count; i++)
    {
      if (i >= _rows.Count)
      {
        RecordRowParts created = CreateRecordRow();
        _rows.Add(created);
        _rowsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rowsLayout.Controls.Add(created.Row, 0, i);
      }
      ShowRecordInRow(_rows[i], page[i]);
    }

    _rowsLayout.ResumeLayout();
    bool hasRecords = _records.Count > 0;
    _rowsLayout.Visible = hasRecords;
    _emptyStateLabel.Visible = !hasRecords;
    int pageCount = GetPageCount(_records.Count);
    _pageLabel.Text = $"Page {_pageIndex + 1} of {pageCount}";
    UpdateButtonStates();
    ApplyTheme();
    ResizeToCurrentPage(DeviceDpi);
  }

  private void ResizeToCurrentPage(int deviceDpi)
  {
    int contentWidth = RequiredClientWidth(deviceDpi, _records.Count);
    // Sized for a 23:59 row (WorstCaseElapsedMinutes) so ordinary rows never wrap; the screen clamp
    // below also bounds the window, and IconTextLabel wraps any row longer than the budget.
    int width = contentWidth;
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

  private int RowTextWidth()
  {
    // Mirrors RequiredClientWidth's budget term for term (including the DPI scaling of the fixed
    // pixel constants), so a row that was sized to fit is never told to wrap; only a row longer
    // than the screen-clamped window wraps.
    float scale = DeviceDpi / 96f;
    int deleteButtonWidth =
      TextRenderer
        .MeasureText(
          "Delete",
          Font,
          Size.Empty,
          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        )
        .Width + (int)Math.Ceiling(Palette.SpacingMd * 2 * scale);
    int toggleButtonWidth =
      TextRenderer
        .MeasureText(
          ExpandedGlyph,
          Font,
          Size.Empty,
          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
        )
        .Width
      + (int)Math.Ceiling(Palette.SpacingMd * 2 * scale)
      + (int)Math.Ceiling(Palette.SpacingSm * scale);
    int rootChrome = (int)
      Math.Ceiling((Palette.SpacingLg * 2 + SystemInformation.VerticalScrollBarWidth) * scale);
    return Math.Max(
      1,
      ClientSize.Width
        - rootChrome
        - deleteButtonWidth
        - toggleButtonWidth
        - (int)Math.Ceiling((Palette.SpacingSm * 3 + 2) * scale)
    );
  }

  private void ConstrainRecordDetails()
  {
    Size maximum = new(RowTextWidth(), 0);
    Size lapMaximum = new(Math.Max(1, RowTextWidth() - LapIndentWidth()), 0);
    foreach (RecordRowParts parts in _rows)
    {
      parts.Details.MaximumSize = maximum;
      foreach (IconTextLabel lapLabel in parts.LapLabels)
      {
        lapLabel.MaximumSize = lapMaximum;
      }
    }
  }

  /// <summary>
  /// The longest duration the window is sized for, <c>23:59</c> (AGENTS.md §8.5/§10.3). The width is a
  /// fixed budget for that worst-case row, not a function of the records shown, so the window does
  /// not resize between pages; a longer row wraps to a second line instead of widening it.
  /// </summary>
  internal const int WorstCaseElapsedMinutes = (23 * 60) + 59;

  internal static int RequiredClientWidth(int deviceDpi, int totalRecordCount)
  {
    float scale = deviceDpi / 96f;
    // The fonts are deliberately NOT pre-scaled by `scale`: in the real PerMonitorV2 process WinForms
    // already renders (and TextRenderer already measures) a point-sized font at the device DPI, so
    // scaling the size again double-counted it and made the window far wider than its rows. Only the
    // fixed pixel constants below scale by hand.
    using Font scaledBodyFont = Typography.CreateBodyFont();
    using Font scaledRowFont = Typography.CreateMonospaceBodyFont();

    int TextWidth(string text, Font font) =>
      TextRenderer
        .MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine)
        .Width;
    int ButtonWidth(string text) =>
      TextWidth(text, scaledBodyFont) + (int)Math.Ceiling(Palette.SpacingMd * 2 * scale);

    // Date and times are fixed-width, so only the duration decides how long a row can get.
    StopwatchRecord worstCase = new(
      Id: 0,
      StartTimestamp: 0,
      EndTimestamp: 0,
      ElapsedMinutes: WorstCaseElapsedMinutes,
      LapCount: 1
    );
    int rowTextWidth = IconTextLayout
      .MeasureSingleLine(RecordsListControl.FormatRecordRow(worstCase), scaledRowFont)
      .Width;
    int rowWidth =
      rowTextWidth
      + ButtonWidth("Delete")
      + ButtonWidth(ExpandedGlyph)
      // Four SpacingSm gaps (one more than before, for the toggle column) and the 2px border, plus
      // one more SpacingSm of slack: the row's real cell padding and rounding come out a few pixels
      // wider than the analytic sum, and a budget that is short by even one pixel wraps the
      // duration of every ordinary row.
      + (int)Math.Ceiling((Palette.SpacingSm * 5 + 2) * scale);
    // A worst-case lap detail row, indented under an expanded record, must also fit without
    // wrapping.
    Lap worstCaseLap = new(
      Id: 999,
      StartTimestamp: 0,
      EndTimestamp: 0,
      ElapsedMinutes: WorstCaseElapsedMinutes
    );
    int lapRowWidth =
      IconTextLayout
        .MeasureSingleLine(RecordsListControl.FormatLapRow(worstCaseLap), scaledRowFont)
        .Width + (int)Math.Ceiling(Palette.SpacingLg * scale);
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
    return Math.Max(Math.Max(Math.Max(rowWidth, headerWidth), paginationWidth), lapRowWidth)
      + rootChrome;
  }

  private RecordRowParts CreateRecordRow()
  {
    Button toggleButton = ButtonFactory.Create(CollapsedGlyph, Palette.CancelButton);
    toggleButton.Anchor = AnchorStyles.Left;
    toggleButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);
    toggleButton.AccessibleName = "Show laps";
    // The row is reused for different records, so the id lives in Tag instead of a captured local.
    toggleButton.Click += async (sender, _) =>
    {
      if (sender is Button { Tag: long id })
      {
        await ToggleLapsAsync(id);
      }
    };

    IconTextLabel details = new(_rowIcons)
    {
      Dock = DockStyle.Fill,
      Font = _rowFont,
      AutoSize = true,
      Margin = new Padding(0),
    };
    // Give the row its wrap width up front (once the window has been sized) so it lays out at its
    // final size instead of wide first and then again when ConstrainRecordDetails catches up.
    if (ClientSize.Width > 1)
    {
      details.MaximumSize = new Size(RowTextWidth(), 0);
    }
    Button deleteButton = ButtonFactory.Create("Delete", Palette.StopButton);
    deleteButton.Anchor = AnchorStyles.Right;
    deleteButton.Margin = new Padding(Palette.SpacingSm, 0, 0, 0);
    deleteButton.Click += async (sender, _) =>
    {
      if (sender is Button { Tag: long id })
      {
        await DeleteRecordAsync(id);
      }
    };

    // Holds this record's lap rows, indented, below the details/delete line. Hidden until
    // expanded; rows within it are pooled the same way the record rows themselves are.
    TableLayoutPanel lapsPanel = new()
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      Padding = new Padding(Palette.SpacingLg, Palette.SpacingXs, 0, 0),
      Margin = new Padding(0),
      Visible = false,
    };
    lapsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

    TableLayoutPanel content = new()
    {
      // An AutoSize panel cannot derive a height from a Dock.Fill child: Fill consumes the space the
      // parent has already allocated rather than contributing its preferred height. Dock.Top keeps
      // this row full-width while allowing the record label and Delete button to determine the row
      // height, instead of collapsing every record into a thin line.
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 3,
      RowCount = 2,
      Padding = new Padding(Palette.SpacingSm),
      Margin = new Padding(0),
    };
    content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    content.Controls.Add(toggleButton, 0, 0);
    content.Controls.Add(details, 1, 0);
    content.Controls.Add(deleteButton, 2, 0);
    content.Controls.Add(lapsPanel, 0, 1);
    content.SetColumnSpan(lapsPanel, 3);

    Panel row = new()
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Margin = new Padding(0, 0, 0, Palette.SpacingXs),
      Padding = new Padding(1),
    };
    row.Controls.Add(content);
    return new RecordRowParts(row, content, details, deleteButton, toggleButton, lapsPanel, []);
  }

  private void ShowRecordInRow(RecordRowParts parts, StopwatchRecord record)
  {
    parts.Details.Text = RecordsListControl.FormatRecordRow(record);
    parts.Delete.Tag = record.Id;
    parts.Toggle.Tag = record.Id;
    parts.Toggle.Visible = record.LapCount > 0;
    // Colors are reapplied on every show so a light/dark switch (which rebuilds) recolors reused rows.
    parts.Row.BackColor = Palette.Border(_dark);
    parts.Content.BackColor = Palette.RowBackground(_dark);
    parts.Details.ForeColor = Palette.Text(_dark);
    ApplyExpansionToRow(parts, record.Id);
  }

  private void ApplyTheme()
  {
    BackColor = Palette.CardBackground(_dark);
    ForeColor = Palette.Text(_dark);
    _emptyStateLabel.ForeColor = Palette.EmptyStateText(_dark);
    _pageLabel.ForeColor = Palette.MutedText(_dark);
  }

  /// <summary>
  /// The controls of one record row, kept together so the row can be reused for another record.
  /// <see cref="LapLabels"/> is the pooled set of lap detail labels currently shown inside
  /// <see cref="LapsPanel"/> — a mutable list, even though the record itself is immutable, so it
  /// can be grown or shrunk in place as this row is reused for records with different lap counts.
  /// </summary>
  private sealed record RecordRowParts(
    Panel Row,
    TableLayoutPanel Content,
    IconTextLabel Details,
    Button Delete,
    Button Toggle,
    TableLayoutPanel LapsPanel,
    List<IconTextLabel> LapLabels
  );
}
