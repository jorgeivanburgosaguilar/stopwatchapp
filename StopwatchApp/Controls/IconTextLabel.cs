namespace StopwatchApp.Controls;

/// <summary>
/// A read-only, word-wrapping text label that draws the record row's emoji as embedded color images
/// (AGENTS.md §8.5). It exists so <c>ManageRecordsForm</c> renders rows through the same
/// <see cref="IconTextLayout"/> engine as the main window instead of a stock <see cref="Label"/>,
/// which would show the same flat monochrome glyphs.
/// </summary>
internal sealed class IconTextLabel : Control
{
  private readonly RowIconSet _icons;

  /// <summary>Initializes a new instance of the <see cref="IconTextLabel"/> class.</summary>
  /// <param name="icons">The decoded icons. Owned by the caller, so many labels share one set.</param>
  public IconTextLabel(RowIconSet icons)
  {
    _icons = icons;
    SetStyle(
      ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.AllPaintingInWmPaint
        | ControlStyles.UserPaint
        | ControlStyles.ResizeRedraw,
      true
    );
    // Not a tab stop and no selection: this is display-only text, like the Label it replaces.
    SetStyle(ControlStyles.Selectable, false);
    TabStop = false;
  }

  /// <inheritdoc />
  public override Size GetPreferredSize(Size proposedSize)
  {
    // A MaximumSize width (set by ManageRecordsForm.ConstrainRecordDetails) is the wrap width; with
    // none, the row stays on one line.
    int wrapWidth = MaximumSize.Width > 0 ? MaximumSize.Width : IconTextLayout.Unbounded;
    return IconTextLayout.Measure(Text, Font, wrapWidth);
  }

  /// <inheritdoc />
  protected override void OnTextChanged(EventArgs e)
  {
    base.OnTextChanged(e);
    Refresh();
  }

  /// <inheritdoc />
  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    IconTextLayout.Draw(e.Graphics, Text, Font, ClientRectangle, ForeColor, _icons);
  }
}
