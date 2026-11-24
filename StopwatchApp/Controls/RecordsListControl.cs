using System.Drawing.Drawing2D;
using StopwatchApp.Formatting;
using StopwatchApp.Models;
using StopwatchApp.Services;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// Renders the laps panel (shown only while there is at least one lap) and the records panel
/// (always shown, with a "No records yet" empty state), per AGENTS.md §8.5. Data is pushed in via
/// <see cref="UpdateRecords"/>/<see cref="UpdateLaps"/> — this control never reads
/// <c>IStopwatchStore</c> itself; it only raises <see cref="ManageRecordsRequested"/> or
/// <see cref="ResumeRequested"/> and lets its owner (<c>MainForm</c>, AGENTS.md §3) perform the
/// actual action.
/// </summary>
public sealed class RecordsListControl : UserControl
{
  // AGENTS.md §8.5 — only the most recent MaxDisplayedRecords are shown in this main window;
  // a future history/records-manager window (out of scope for now) will offer the full list with
  // edit/delete. Bounding the display count is also what lets this control's height be a small,
  // fixed constant instead of stretching to fill whatever space MainForm's fixed window has left.
  private const int MaxDisplayedRecords = StopwatchTimer.RecentRecordCount;

  /// <summary>The Resume pill's label.</summary>
  internal const string ResumeLabel = "↻ Resume";

  private const TextFormatFlags PillTextFlags =
    TextFormatFlags.NoPadding
    | TextFormatFlags.SingleLine
    | TextFormatFlags.HorizontalCenter
    | TextFormatFlags.VerticalCenter;

  private readonly List<StopwatchRecord> _shownRecords = [];
  private readonly Label _lapsHeader;
  private readonly ListBox _lapsListBox;
  private readonly ListBox _recordsListBox;
  private readonly Label _emptyStateLabel;
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
    ConfigureRowRendering(_recordsListBox, withResumePill: true);
    _recordsListBox.MouseClick += OnRecordsMouseClick;
    _recordsListBox.MouseMove += OnRecordsMouseMove;
    _recordsListBox.MouseLeave += (_, _) => SetPillCursor(false);
    _emptyStateLabel = new Label
    {
      Text = "No records yet",
      Dock = DockStyle.Fill,
      TextAlign = ContentAlignment.MiddleCenter,
      Visible = false,
    };

    // AGENTS.md §8.5 — the header's one action button uses the shared ButtonFactory, so it carries
    // the same stock, palette-colored appearance as the stopwatch card's transport buttons. Clear
    // All Records lives only in the Manage Records window, so the "Records" title sits left-aligned
    // on the same line as Manage Records, right-aligned.
    _manageRecordsButton = ButtonFactory.Create("Manage Records", Palette.LapButton);
    _manageRecordsButton.Margin = new Padding(0);
    _manageRecordsButton.Anchor = AnchorStyles.Right;
    _manageRecordsButton.Click += (_, _) => ManageRecordsRequested?.Invoke();

    TableLayoutPanel headerRow = new()
    {
      Dock = DockStyle.Top,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      ColumnCount = 2,
      RowCount = 1,
      Margin = new Padding(0, 0, 0, Palette.SpacingSm),
    };
    headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    headerRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    recordsLabel.Margin = new Padding(0);
    headerRow.Controls.Add(recordsLabel, 0, 0);
    headerRow.Controls.Add(_manageRecordsButton, 1, 0);

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
    // Rows, top to bottom: laps header, laps list, header row (Records label + Manage Records
    // button), records host.
    layout.Controls.Add(_lapsHeader, 0, 0);
    layout.Controls.Add(_lapsListBox, 0, 1);
    layout.Controls.Add(headerRow, 0, 2);
    layout.Controls.Add(_recordsHost, 0, 3);

    Controls.Add(layout);

    UpdateRecords([]);
    UpdateLaps([]);
    ApplyTheme();
  }

  /// <summary>Fires when the user clicks the "Manage Records" button.</summary>
  public event Action? ManageRecordsRequested;

  /// <summary>
  /// Fires when the user clicks the Resume pill on one of the shown records. The owner decides
  /// whether to proceed (AGENTS.md §8.3) — this control only reports the request.
  /// </summary>
  public event Action<StopwatchRecord>? ResumeRequested;

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
  /// Toggles the empty state. Only the most recent <see cref="MaxDisplayedRecords"/> are actually
  /// listed (AGENTS.md §8.5), each with a Resume pill; the records manager offers the full list. The
  /// records host's height is recomputed
  /// to fit exactly the rows now shown (AGENTS.md §8.5/§10.3) — 1 row's worth for the empty state, or
  /// the true (possibly word-wrapped) height of however many of the capped records are displayed —
  /// instead of a fixed height sized for the worst case.
  /// </summary>
  /// <param name="records">The records to display.</param>
  public void UpdateRecords(IReadOnlyList<StopwatchRecord> records)
  {
    _shownRecords.Clear();
    _shownRecords.AddRange(records.Take(MaxDisplayedRecords));

    _recordsListBox.BeginUpdate();
    _recordsListBox.Items.Clear();
    foreach (StopwatchRecord record in _shownRecords)
    {
      _recordsListBox.Items.Add(FormatRecordRow(record));
    }
    _recordsListBox.EndUpdate();

    bool hasRecords = records.Count > 0;
    _recordsListBox.Visible = hasRecords;
    _emptyStateLabel.Visible = !hasRecords;

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
    bool hasLaps = laps.Count > 0;
    if (hasLaps && !_lapsListBox.Visible)
    {
      // AGENTS.md §8.5/§10.3 — while hidden, this list box is skipped entirely by WinForms' dock
      // layout engine, so its own ClientSize.Width can still be the stale default control size the
      // very first time a lap appears — MeasureItem below would then word-wrap against that too-
      // narrow width and report a hugely inflated row height (every word landing on its own line)
      // until the *next* lap's fresh Items.Clear()/Add re-measures against the by-then-correct
      // width. Its parent TableLayoutPanel (`layout`) is never itself hidden, so its ClientSize is
      // already trustworthy; syncing to it here makes the very first lap's measurement correct too
      // instead of relying on the next lap to self-correct it.
      _lapsListBox.Width = _lapsListBox.Parent?.ClientSize.Width ?? _lapsListBox.Width;
    }

    _lapsListBox.BeginUpdate();
    _lapsListBox.Items.Clear();
    foreach (Lap lap in laps)
    {
      _lapsListBox.Items.Add(FormatLapRow(lap));
    }
    _lapsListBox.EndUpdate();

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
  /// <param name="withResumePill">
  /// Whether rows reserve room for, and draw, the Resume pill at their right edge (records only).
  /// </param>
  private void ConfigureRowRendering(ListBox listBox, bool withResumePill = false)
  {
    listBox.SelectionMode = SelectionMode.None;
    listBox.DrawMode = DrawMode.OwnerDrawVariable;
    listBox.MeasureItem += (_, e) =>
    {
      float scale = listBox.DeviceDpi / 96f;
      // A record row's text wraps before the Resume pill that is drawn at its right edge.
      int pillReserve = withResumePill ? ResumePillReserve(listBox.Font, scale) : 0;
      int availableWidth = Math.Max(
        1,
        listBox.ClientSize.Width
          - Scaled(Palette.SpacingSm * 2, scale)
          - SystemInformation.VerticalScrollBarWidth
          - pillReserve
      );
      e.ItemHeight = MeasureRowHeight(
        listBox.Items[e.Index]?.ToString() ?? string.Empty,
        listBox.Font,
        availableWidth,
        scale
      );
    };
    listBox.DrawItem += (_, e) => DrawRow(listBox, e, withResumePill);
  }

  /// <summary>
  /// The width of the Resume pill: its label plus horizontal padding. <paramref name="font"/> is the
  /// row font as already scaled for the device, so only the padding needs <paramref name="scale"/>.
  /// </summary>
  /// <param name="font">The row font.</param>
  /// <param name="scale">The device-DPI scale (<c>DeviceDpi / 96</c>).</param>
  internal static int ResumePillWidth(Font font, float scale) =>
    TextRenderer
      .MeasureText(
        ResumeLabel,
        font,
        Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
      )
      .Width + Scaled(Palette.SpacingMd * 2, scale);

  /// <summary>
  /// The horizontal space a record row gives up to its Resume pill: the pill plus the gap between
  /// it and the row text. Shared by measuring, drawing, and <c>MainForm</c>'s width budget so the
  /// three can never disagree.
  /// </summary>
  /// <param name="font">The row font.</param>
  /// <param name="scale">The device-DPI scale (<c>DeviceDpi / 96</c>).</param>
  internal static int ResumePillReserve(Font font, float scale) =>
    ResumePillWidth(font, scale) + Scaled(Palette.SpacingSm, scale);

  /// <summary>
  /// The Resume pill's rectangle inside a row card: right-aligned (inset by the row's text inset)
  /// and vertically centered. Used for drawing and for hit-testing clicks.
  /// </summary>
  /// <param name="rowBounds">The row card's bounds.</param>
  /// <param name="font">The row font.</param>
  /// <param name="scale">The device-DPI scale (<c>DeviceDpi / 96</c>).</param>
  internal static Rectangle ResumePillBounds(Rectangle rowBounds, Font font, float scale)
  {
    int width = ResumePillWidth(font, scale);
    int height = Math.Min(rowBounds.Height, font.Height + Scaled(Palette.SpacingXs * 2, scale));
    int inset = Scaled(Palette.SpacingSm, scale);
    return new Rectangle(
      rowBounds.Right - inset - width,
      rowBounds.Top + ((rowBounds.Height - height) / 2),
      width,
      height
    );
  }

  /// <summary>The row card's rectangle inside its list item bounds (the gap between cards).</summary>
  private static Rectangle RowCardBounds(Rectangle itemBounds, float scale) =>
    Rectangle.Inflate(itemBounds, 0, -(Scaled(Palette.SpacingXs, scale) / 2));

  /// <summary>
  /// Finds the shown record whose Resume pill is under <paramref name="point"/>, or -1.
  /// </summary>
  private int PillIndexAt(Point point)
  {
    int index = _recordsListBox.IndexFromPoint(point);
    if (index < 0 || index >= _shownRecords.Count)
    {
      return -1;
    }

    float scale = _recordsListBox.DeviceDpi / 96f;
    Rectangle card = RowCardBounds(_recordsListBox.GetItemRectangle(index), scale);
    return ResumePillBounds(card, _recordsListBox.Font, scale).Contains(point) ? index : -1;
  }

  private void OnRecordsMouseClick(object? sender, MouseEventArgs e)
  {
    if (e.Button != MouseButtons.Left)
    {
      return;
    }

    int index = PillIndexAt(e.Location);
    if (index >= 0)
    {
      ResumeRequested?.Invoke(_shownRecords[index]);
    }
  }

  // The pill has no hover visual: repainting owner-drawn rows on hover flickered the list, so
  // hovering only swaps the cursor and never invalidates anything.
  private void OnRecordsMouseMove(object? sender, MouseEventArgs e) =>
    SetPillCursor(PillIndexAt(e.Location) >= 0);

  private void SetPillCursor(bool overPill)
  {
    Cursor wanted = overPill ? Cursors.Hand : Cursors.Default;
    if (_recordsListBox.Cursor != wanted)
    {
      _recordsListBox.Cursor = wanted;
    }
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
  /// <param name="scale">
  /// The device-DPI scale (<c>DeviceDpi / 96</c>) applied to the 96-dpi design spacing. Owner-drawn
  /// geometry is not touched by WinForms' automatic scaling, so it converts here, once, and
  /// <see cref="DrawRow"/> uses the same scale.
  /// </param>
  internal static int MeasureRowHeight(string text, Font font, int availableWidth, float scale = 1f)
  {
    Size measured = IconTextLayout.Measure(text, font, availableWidth);
    return measured.Height
      + Scaled(Palette.SpacingSm * 2, scale)
      + Scaled(Palette.SpacingXs, scale);
  }

  private static int Scaled(int designPixels, float scale) =>
    (int)Math.Ceiling(designPixels * scale);

  private void DrawRow(ListBox listBox, DrawItemEventArgs e, bool withResumePill)
  {
    if (e.Index < 0)
    {
      return;
    }

    Graphics g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;

    // The card background (set on the list box itself in ApplyTheme) shows through as the gap
    // between rounded row cards, so it only needs painting here, not a separate lookup.
    using (SolidBrush gapBrush = new(listBox.BackColor))
    {
      g.FillRectangle(gapBrush, e.Bounds);
    }

    // The same scale MeasureRowHeight used, so a row is painted at the height it was measured for.
    float scale = listBox.DeviceDpi / 96f;
    Rectangle rowBounds = RowCardBounds(e.Bounds, scale);
    using GraphicsPath path = RoundedRectangle.Path(
      rowBounds,
      Scaled(Palette.ControlCornerRadius, scale)
    );
    using (SolidBrush rowBrush = new(Palette.RowBackground(_dark)))
    {
      g.FillPath(rowBrush, path);
    }
    using (Pen borderPen = new(Palette.Border(_dark), 1f))
    {
      g.DrawPath(borderPen, path);
    }

    Rectangle textBounds = Rectangle.Inflate(rowBounds, -Scaled(Palette.SpacingSm, scale), 0);
    if (withResumePill)
    {
      // Text wraps before the pill, exactly as MeasureItem budgeted for it.
      textBounds.Width = Math.Max(1, textBounds.Width - ResumePillReserve(listBox.Font, scale));
    }
    // AGENTS.md §8.5 — IconTextLayout word-wraps, not ellipsizes: a row past the window's sized-for
    // worst case (a session over 24h, a 4-digit lap id) wraps to a second line. It is the same
    // engine MeasureRowHeight above uses, so a wrapped row is never sized one way and painted
    // another, and it draws the emoji as color images rather than GDI's monochrome glyphs.
    IconTextLayout.Draw(
      g,
      listBox.Items[e.Index].ToString() ?? string.Empty,
      listBox.Font,
      textBounds,
      Palette.Text(_dark),
      _rowIcons
    );

    if (withResumePill)
    {
      DrawResumePill(g, listBox, rowBounds, scale);
    }
  }

  private static void DrawResumePill(Graphics g, ListBox listBox, Rectangle rowBounds, float scale)
  {
    Rectangle pill = ResumePillBounds(rowBounds, listBox.Font, scale);
    (Color baseColor, _, _) = Palette.StartButton;
    using GraphicsPath pillPath = RoundedRectangle.Path(
      pill,
      Scaled(Palette.ControlCornerRadius, scale)
    );
    using SolidBrush pillBrush = new(baseColor);
    g.FillPath(pillBrush, pillPath);
    TextRenderer.DrawText(g, ResumeLabel, listBox.Font, pill, Color.White, PillTextFlags);
  }
}
