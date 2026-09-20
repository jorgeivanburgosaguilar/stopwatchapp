using System.Drawing.Drawing2D;
using StopwatchApp.Formatting;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// Renders the laps panel (shown only while there is at least one lap) and the records panel
/// (always shown, with a "No records yet" empty state), per AGENTS.md §8.5. Data is pushed in via
/// <see cref="UpdateRecords"/>/<see cref="UpdateLaps"/> — this control never reads
/// <c>IStopwatchStore</c> itself; it only raises <see cref="ClearAllRequested"/> or
/// <see cref="ManageRecordsRequested"/> and
/// lets its owner (<c>MainForm</c>, AGENTS.md §3) perform the actual clear.
/// </summary>
public sealed class RecordsListControl : UserControl
{
  // AGENTS.md §8.5 — only the most recent MaxDisplayedRecords are shown in this main window;
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
  private readonly Panel _recordsHost;
  private readonly Font _rowFont;
  private readonly RowIconSet _rowIcons = new();
  private bool _dark;

  /// <summary>
  /// Initializes a new instance of the <see cref="RecordsListControl"/> class.
  /// </summary>
  public RecordsListControl()
  {
    Dock = DockStyle.Fill;
    // AGENTS.md §8.5/§10.3 — this card's preferred height must genuinely reflect its
    // (bounded) content, since MainForm.FixedClientSize's height derivation reads it: capping
    // records to MaxDisplayedRecords is what makes that height a small, known constant instead of
    // "whatever's left over," which is the actual fix for "the window is too tall." GrowOnly (the
    // AutoSize default) would never shrink this card back down after a taller state (e.g. the laps
    // panel appearing) — the same pitfall already documented for StopwatchControl's card.
    AutoSize = true;
    AutoSizeMode = AutoSizeMode.GrowAndShrink;
    // AGENTS.md §11 card treatment, matching StopwatchControl: an explicit palette background plus
    // an inset so the rounded border drawn in OnPaint doesn't clip the content. Per §8.5,
    // the bottom inset is 0, not SpacingLg: MainForm's own root Padding (also bottom-0) plus this
    // card's 1px OnPaint border and the TableLayoutPanel cell's default 3px margin are what leave
    // the ~6px gap the owner asked for between the last record row and the window edge; a bottom
    // Padding here on top of those would double it.
    Padding = new Padding(Palette.SpacingLg, Palette.SpacingLg, Palette.SpacingLg, 0);
    DoubleBuffered = true;
    // AGENTS.md §10.3 — same ResizeRedraw fix as StopwatchControl: without it, the rounded
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
      // AGENTS.md §8.5/§11 — assigned before ConfigureRowRendering below, which derives
      // ItemHeight from this Font; setting it after would size rows off the stale default font.
      Font = _rowFont,
    };
    ConfigureRowRendering(_lapsListBox);
    // Starts empty; UpdateLaps sizes it to the actual laps shown (AGENTS.md §8.5) — capped at 3
    // rows, summing each row's real (possibly wrapped) height rather than a flat multiple of a
    // single-line ItemHeight.
    _lapsListBox.Height = 0;

    Label recordsLabel = new()
    {
      Text = "Records",
      AutoSize = true,
      Anchor = AnchorStyles.Left,
      Margin = new Padding(0, 0, 0, 0),
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

    // AGENTS.md §8.5 — both header-row buttons use the shared ButtonFactory, so they carry the
    // same stock, palette-colored appearance as the stopwatch card's transport buttons. Order
    // left-to-right is Manage Records then Clear All Records, both right-aligned beside the
    // "Records" label per the owner's markup.
    _manageRecordsButton = ButtonFactory.Create("Manage Records", Palette.LapButton);
    _manageRecordsButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);
    _manageRecordsButton.Click += (_, _) => ManageRecordsRequested?.Invoke();

    _clearAllButton = ButtonFactory.Create("Clear All Records", Palette.StopButton);
    _clearAllButton.Margin = new Padding(0);
    _clearAllButton.Visible = false;
    _clearAllButton.Click += (_, _) => ClearAllRequested?.Invoke();

    FlowLayoutPanel headerButtonsRow = new()
    {
      FlowDirection = FlowDirection.LeftToRight,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Anchor = AnchorStyles.Right,
      // Both buttons must always render on one line, never wrap: WrapContents defaults to true,
      // which can wrap to a second line during preferred-size measurement
      // during an AutoSize preferred-size query even though the real fixed window is comfortably
      // wide enough for both, silently inflating the computed height by a whole button row.
      WrapContents = false,
    };
    headerButtonsRow.Controls.Add(_manageRecordsButton);
    headerButtonsRow.Controls.Add(_clearAllButton);

    // Keep the actions in their own line below the title. Besides creating a clearer title/action
    // rhythm, this lets the main window fit its rows instead of permanently reserving the combined
    // width of the title and both action labels.
    TableLayoutPanel headerRow = new()
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      RowCount = 2,
      Margin = new Padding(0, 0, 0, Palette.SpacingSm),
    };
    headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    headerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    headerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    recordsLabel.Margin = new Padding(0, 0, 0, Palette.SpacingXs);
    headerRow.Controls.Add(recordsLabel, 0, 0);
    headerRow.Controls.Add(headerButtonsRow, 0, 1);

    // AGENTS.md §8.5/§10.3 — Dock.Top with an explicit Height (not Dock.Fill inside a Percent(100)
    // row) so this host's height is a small, known constant instead of stretching to fill whatever's
    // left in the window. The height is recomputed on every UpdateRecords call (the
    // record count, and therefore the true content height, changes at runtime) rather than fixed to
    // MaxDisplayedRecords rows regardless of how many records actually exist.
    _recordsHost = new Panel { Dock = DockStyle.Top };
    _recordsHost.Controls.Add(_recordsListBox);
    _recordsHost.Controls.Add(_emptyStateLabel);

    TableLayoutPanel layout = new()
    {
      // AGENTS.md §8.5/§10.3 — Dock.Top, not Fill: this control is itself AutoSize (see
      // the constructor), and a Dock.Fill child never contributes to an AutoSize parent's own
      // preferred size — Dock.Top still
      // spans the full width while contributing a real preferred height.
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 1,
      RowCount = 4,
    };
    // AGENTS.md §10.3 — without an explicit ColumnStyle, a single-column TableLayoutPanel falls
    // back to an implicit AutoSize column that only happens to span the control's width; pinning it
    // to 100% makes that span structural instead of incidental.
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    // Rows, top to bottom: laps header, laps list, header row (Records label + Manage Records/Clear
    // All Records buttons), records host.
    layout.Controls.Add(_lapsHeader, 0, 0);
    layout.Controls.Add(_lapsListBox, 0, 1);
    layout.Controls.Add(headerRow, 0, 2);
    layout.Controls.Add(_recordsHost, 0, 3);

    Controls.Add(layout);

    UpdateRecords([]);
    UpdateLaps([]);
    ApplyTheme();
  }

  /// <summary>Fires when the user clicks the "Clear All Records" button.</summary>
  public event Action? ClearAllRequested;

  /// <summary>Fires when the user clicks the "Manage Records" button.</summary>
  public event Action? ManageRecordsRequested;

  /// <summary>
  /// Gets or sets whether dark-theme colors should be used for the surfaces
  /// <see cref="Theme.Palette"/> covers (e.g. the empty-state text). Stock controls already follow
  /// <c>Application.SetColorMode</c> on their own; this only affects the colors Palette supplies.
  /// Defaults to <see langword="false"/>; <c>MainForm</c> wires it to the live OS setting (§7/§11).
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
  /// <see cref="MaxDisplayedRecords"/> are actually listed (AGENTS.md §8.5); the records manager
  /// offers the full list. "Clear All Records" still clears
  /// every persisted record, not just the ones shown here. The records host's height is recomputed
  /// to fit exactly the rows now shown (AGENTS.md §8.5/§10.3) — 1 row's worth for the empty state, or
  /// the true (possibly word-wrapped) height of however many of the capped records are displayed —
  /// instead of a fixed height sized for the worst case.
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

    int shownCount = Math.Min(records.Count, MaxDisplayedRecords);
    _recordsHost.Height = hasRecords
      ? SumItemHeights(_recordsListBox, shownCount)
      : _emptyStateLabel.PreferredSize.Height;
  }

  /// <summary>
  /// Replaces the displayed laps, expected newest first. Toggles the laps panel's visibility. The
  /// laps list's height is recomputed to fit exactly the (up to 3) most recent laps shown
  /// (AGENTS.md §8.5), rather than a flat multiple of a single-line row height.
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
    _lapsListBox.Height = SumItemHeights(_lapsListBox, Math.Min(laps.Count, 3));
  }

  /// <summary>
  /// Sums the real (possibly word-wrapped, per <see cref="ConfigureRowRendering"/>'s
  /// <c>MeasureItem</c> handler) height of the first <paramref name="count"/> items in
  /// <paramref name="listBox"/> — the actual content height a fixed-count row cap needs, since rows
  /// no longer share one flat <c>ItemHeight</c> (AGENTS.md §8.5).
  /// </summary>
  private static int SumItemHeights(ListBox listBox, int count)
  {
    int total = 0;
    for (int i = 0; i < count; i++)
    {
      total += listBox.GetItemHeight(i);
    }
    return total;
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
    // (AGENTS.md §11) — kept as one technique reused via RoundedRectangle rather than reinvented.
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
      _rowIcons.Dispose();
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
  /// Switches a records/laps <see cref="ListBox"/> to the documented row treatment: variable-height
  /// owner drawing (AGENTS.md §8.5; a row word-wraps instead of ellipsizing
  /// past the window's sized-for width) so each row renders as a small rounded card
  /// (<see cref="Palette.RowBackground"/> fill, <see cref="Palette.Border"/> outline) with its own
  /// inset spacing, instead of plain default-drawn text rows.
  /// Selection is turned off — these rows are a read-only log, and the stock selection highlight
  /// would clash with the custom paint.
  /// </summary>
  /// <param name="listBox">The list box to configure.</param>
  private void ConfigureRowRendering(ListBox listBox)
  {
    listBox.SelectionMode = SelectionMode.None;
    listBox.DrawMode = DrawMode.OwnerDrawVariable;
    listBox.MeasureItem += (_, e) =>
    {
      int availableWidth = Math.Max(
        1,
        listBox.ClientSize.Width
          - (Palette.SpacingSm * 2)
          - SystemInformation.VerticalScrollBarWidth
      );
      e.ItemHeight = MeasureRowHeight(
        listBox.Items[e.Index]?.ToString() ?? string.Empty,
        listBox.Font,
        availableWidth
      );
    };
    listBox.DrawItem += (_, e) => DrawRow(listBox, e);
  }

  /// <summary>
  /// Measures the height a row needs to render <paramref name="text"/> word-wrapped to
  /// <paramref name="availableWidth"/> in <paramref name="font"/> (AGENTS.md §8.5). A pure
  /// function of its three inputs — no <see cref="ListBox"/> needed — so
  /// <c>RecordsListControlTests</c> can cover the word-wrap threshold directly, the same
  /// pure-function-over-a-real-control convention <see cref="MainForm.RequiredClientWidth"/> already
  /// uses (AGENTS.md §13). The caller (<see cref="ConfigureRowRendering"/>'s <c>MeasureItem</c>
  /// handler) is responsible for deriving <paramref name="availableWidth"/> from the real list box's
  /// current width, reserving <see cref="SystemInformation.VerticalScrollBarWidth"/> — a plain Win32
  /// list box does not shrink <see cref="Control.ClientSize"/> for its own scrollbar, and the laps
  /// list (unlike the height-capped records list) can genuinely scroll — so a row measured while the
  /// scrollbar isn't yet showing doesn't wrap differently once it appears.
  /// </summary>
  /// <param name="text">The row text to measure.</param>
  /// <param name="font">The row font.</param>
  /// <param name="availableWidth">The width, in pixels, available for the text itself (chrome
  /// already excluded).</param>
  internal static int MeasureRowHeight(string text, Font font, int availableWidth)
  {
    Size measured = IconTextLayout.Measure(text, font, availableWidth);
    return measured.Height + (Palette.SpacingSm * 2) + Palette.SpacingXs;
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
    // AGENTS.md §8.5 — IconTextLayout word-wraps, not ellipsizes: a row past the window's sized-for
    // worst case (a session over 24h, a 4-digit lap id) wraps to a second line. It is the same
    // engine MeasureRowHeight above uses, so a wrapped row is never sized one way and painted
    // another, and it draws the emoji as color images rather than GDI's monochrome glyphs.
    IconTextLayout.Draw(
      e.Graphics,
      listBox.Items[e.Index].ToString() ?? string.Empty,
      listBox.Font,
      textBounds,
      Palette.Text(_dark),
      _rowIcons
    );
  }
}
