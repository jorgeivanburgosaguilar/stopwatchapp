using System.Drawing.Drawing2D;
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
  // S14b (AGENTS.md §17) — only the most recent MaxDisplayedRecords are shown in this main window;
  // a future history/records-manager window (out of scope for now) will offer the full list with
  // edit/delete. Bounding the display count is also what lets this control's height be a small,
  // fixed constant instead of stretching to fill whatever space MainForm's fixed window has left.
  private const int MaxDisplayedRecords = 5;

  private readonly Label _lapsHeader;
  private readonly ListBox _lapsListBox;
  private readonly ListBox _recordsListBox;
  private readonly Label _emptyStateLabel;
  private readonly Button _clearAllButton;
  private readonly Button _manageRecordsButton;
  private readonly Font _rowFont;
  private bool _dark;

  /// <summary>
  /// Initializes a new instance of the <see cref="RecordsListControl"/> class.
  /// </summary>
  public RecordsListControl()
  {
    Dock = DockStyle.Fill;
    // S14b (AGENTS.md §17) — this card's own preferred height must now genuinely reflect its
    // (bounded) content, since MainForm.FixedClientSize's height derivation reads it: capping
    // records to MaxDisplayedRecords is what makes that height a small, known constant instead of
    // "whatever's left over," which is the actual fix for "the window is too tall." GrowOnly (the
    // AutoSize default) would never shrink this card back down after a taller state (e.g. the laps
    // panel appearing) — the same pitfall already documented for StopwatchControl's card.
    AutoSize = true;
    AutoSizeMode = AutoSizeMode.GrowAndShrink;
    // S11a card treatment, matching StopwatchControl: an explicit palette background plus an
    // inset so the rounded border drawn in OnPaint doesn't clip the content.
    Padding = new Padding(Palette.SpacingLg);
    DoubleBuffered = true;
    // S14 (AGENTS.md §17) — same ResizeRedraw fix as StopwatchControl: without it, the rounded
    // border this control's own OnPaint draws at Width-1/Height-1 stays visible at its old position
    // after a resize.
    SetStyle(
      ControlStyles.ResizeRedraw
        | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.AllPaintingInWmPaint,
      true
    );

    _rowFont = Typography.CreateMonospaceBodyFont();

    _lapsHeader = new Label
    {
      Text = "Laps",
      AutoSize = true,
      Margin = new Padding(0, 0, 0, Palette.SpacingXs),
    };
    _lapsListBox = new ListBox
    {
      Dock = DockStyle.Top,
      IntegralHeight = false,
      Margin = new Padding(0, 0, 0, Palette.SpacingSm),
      BorderStyle = BorderStyle.None,
      // S14 (AGENTS.md §17) — assigned before ConfigureRowRendering below, which derives
      // ItemHeight from this Font; setting it after would size rows off the stale default font.
      Font = _rowFont,
    };
    ConfigureRowRendering(_lapsListBox);
    // Three rows tall, in terms of the new type scale's actual row height, not a stale pixel literal.
    _lapsListBox.Height = 3 * _lapsListBox.ItemHeight;

    Label recordsHeader = new()
    {
      Text = "Records",
      AutoSize = true,
      Margin = new Padding(0, 0, 0, Palette.SpacingXs),
    };
    _recordsListBox = new ListBox
    {
      Dock = DockStyle.Fill,
      IntegralHeight = false,
      BorderStyle = BorderStyle.None,
      Font = _rowFont,
    };
    ConfigureRowRendering(_recordsListBox);
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
      Margin = new Padding(0, Palette.SpacingSm, Palette.SpacingSm, Palette.SpacingSm),
      Visible = false,
    };
    _clearAllButton.Click += (_, _) => ClearAllRequested?.Invoke();

    // S14c (AGENTS.md §17) — a disabled placeholder for the planned (not yet built) records
    // history/manager window: always visible (unlike Clear-All, it isn't gated on having any
    // records — an empty manager is still openable once it exists) but Enabled = false until that
    // window is actually implemented, so its space is reserved in the fixed layout now rather than
    // requiring another height recalculation later.
    _manageRecordsButton = new Button
    {
      Text = "Manage Records",
      AutoSize = true,
      Enabled = false,
      Margin = new Padding(0, Palette.SpacingSm, 0, Palette.SpacingSm),
    };

    FlowLayoutPanel actionButtonsRow = new()
    {
      FlowDirection = FlowDirection.LeftToRight,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      // Both buttons must always render on one line, never wrap: WrapContents defaults to true,
      // which — observed while re-deriving the S14c height budget — can wrap to a second line
      // during an AutoSize preferred-size query even though the real fixed window is comfortably
      // wide enough for both, silently inflating the computed height by a whole button row.
      WrapContents = false,
    };
    actionButtonsRow.Controls.Add(_clearAllButton);
    actionButtonsRow.Controls.Add(_manageRecordsButton);

    // S14b (AGENTS.md §17) — Dock.Top with an explicit Height (not Dock.Fill inside a Percent(100)
    // row) so this host's height is a small, known constant — MaxDisplayedRecords rows' worth —
    // instead of stretching to fill whatever's left in the fixed window. Height is assigned right
    // after construction, mirroring _lapsListBox.Height above: it needs _recordsListBox.ItemHeight,
    // already computed by ConfigureRowRendering above.
    Panel recordsHost = new() { Dock = DockStyle.Top };
    recordsHost.Controls.Add(_recordsListBox);
    recordsHost.Controls.Add(_emptyStateLabel);
    recordsHost.Height = MaxDisplayedRecords * _recordsListBox.ItemHeight;

    TableLayoutPanel layout = new()
    {
      // S14a/S14b (AGENTS.md §17) — Dock.Top, not Fill: this control is itself AutoSize now (see
      // the constructor), and a Dock.Fill child never contributes to an AutoSize parent's own
      // preferred size (the exact StopwatchControl.contentLayout bug from S14a) — Dock.Top still
      // spans the full width while contributing a real preferred height.
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      RowCount = 5,
    };
    // S14 (AGENTS.md §17) — without an explicit ColumnStyle, a single-column TableLayoutPanel falls
    // back to an implicit AutoSize column that only happens to span the control's width; pinning it
    // to 100% makes that span structural instead of incidental.
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    // S14b (AGENTS.md §17) — AutoSize, not Percent(100): recordsHost above now carries its own
    // fixed height (MaxDisplayedRecords rows), so this row sizes to match instead of stretching.
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    // Rows, top to bottom: laps header, laps list, records header, action buttons row
    // (Clear All Records + the disabled Manage Records placeholder), records host.
    layout.Controls.Add(_lapsHeader, 0, 0);
    layout.Controls.Add(_lapsListBox, 0, 1);
    layout.Controls.Add(recordsHeader, 0, 2);
    layout.Controls.Add(actionButtonsRow, 0, 3);
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
  /// Toggles the empty state and the "Clear All Records" button's visibility. Only the most recent
  /// <see cref="MaxDisplayedRecords"/> are actually listed (S14b, AGENTS.md §17) — a future
  /// history/records-manager window will offer the full list; "Clear All Records" still clears
  /// every persisted record, not just the ones shown here.
  /// </summary>
  /// <param name="records">The records to display.</param>
  public void UpdateRecords(IReadOnlyList<StopwatchRecord> records)
  {
    _recordsListBox.BeginUpdate();
    _recordsListBox.Items.Clear();
    foreach (StopwatchRecord record in records.Take(MaxDisplayedRecords))
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

  /// <inheritdoc />
  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    // Same low-risk "thin rounded outline" resting-elevation treatment as StopwatchControl's card
    // (AGENTS.md §17) — kept as one technique reused via RoundedRectangle rather than reinvented.
    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
    Rectangle bounds = new(0, 0, Width - 1, Height - 1);
    using GraphicsPath path = RoundedRectangle.Path(bounds, Palette.CardCornerRadius);
    using Pen borderPen = new(Palette.ShadowResting(_dark), 1f);
    e.Graphics.DrawPath(borderPen, path);
  }

  /// <inheritdoc />
  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _rowFont.Dispose();
    }
    base.Dispose(disposing);
  }

  private void ApplyTheme()
  {
    Color cardBackground = Palette.CardBackground(_dark);
    BackColor = cardBackground;
    _lapsListBox.BackColor = cardBackground;
    _recordsListBox.BackColor = cardBackground;
    _emptyStateLabel.ForeColor = Palette.EmptyStateText(_dark);
    // Row colors are read from _dark at paint time (DrawRow below), so a theme flip just needs a
    // repaint, not a rebuild of the (unchanged) row text.
    _lapsListBox.Invalidate();
    _recordsListBox.Invalidate();
  }

  /// <summary>
  /// Switches a records/laps <see cref="ListBox"/> to the S11a row treatment: fixed-height owner
  /// drawing so each row renders as a small rounded card (<see cref="Palette.RowBackground"/> fill,
  /// <see cref="Palette.Border"/> outline) with its own inset spacing, instead of the plain
  /// default-drawn text rows the control used before this stage. Selection is turned off — these
  /// rows are a read-only log, and the stock selection highlight would clash with the custom paint.
  /// </summary>
  /// <param name="listBox">The list box to configure.</param>
  private void ConfigureRowRendering(ListBox listBox)
  {
    listBox.SelectionMode = SelectionMode.None;
    listBox.DrawMode = DrawMode.OwnerDrawFixed;
    listBox.ItemHeight =
      TextRenderer.MeasureText("Xg", listBox.Font).Height
      + (Palette.SpacingSm * 2)
      + Palette.SpacingXs;
    listBox.DrawItem += (_, e) => DrawRow(listBox, e);
  }

  private void DrawRow(ListBox listBox, DrawItemEventArgs e)
  {
    if (e.Index < 0)
    {
      return;
    }

    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

    // The card background (set on the list box itself in ApplyTheme) shows through as the gap
    // between rounded row cards, so it only needs painting here, not a separate lookup.
    using (SolidBrush gapBrush = new(listBox.BackColor))
    {
      e.Graphics.FillRectangle(gapBrush, e.Bounds);
    }

    Rectangle rowBounds = Rectangle.Inflate(e.Bounds, 0, -(Palette.SpacingXs / 2));
    using GraphicsPath path = RoundedRectangle.Path(rowBounds, Palette.ControlCornerRadius);
    using (SolidBrush rowBrush = new(Palette.RowBackground(_dark)))
    {
      e.Graphics.FillPath(rowBrush, path);
    }
    using (Pen borderPen = new(Palette.Border(_dark), 1f))
    {
      e.Graphics.DrawPath(borderPen, path);
    }

    Rectangle textBounds = Rectangle.Inflate(rowBounds, -Palette.SpacingSm, 0);
    TextRenderer.DrawText(
      e.Graphics,
      listBox.Items[e.Index].ToString() ?? string.Empty,
      listBox.Font,
      textBounds,
      Palette.Text(_dark),
      // S14 (AGENTS.md §17) — EndEllipsis degrades a pathologically long row (well beyond the
      // measured worst case the fixed window's width is sized for) to a clipped-with-ellipsis
      // string instead of clipping mid-glyph with no indication.
      TextFormatFlags.VerticalCenter
        | TextFormatFlags.Left
        | TextFormatFlags.NoPadding
        | TextFormatFlags.EndEllipsis
    );
  }
}
