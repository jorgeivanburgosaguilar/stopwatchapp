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
  private (
    int Width,
    string Text,
    Font Font,
    (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> Items, Size Size) Layout
  )? _cached;

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
    // A MaximumSize width (set by ManageRecordsForm) is the wrap width; with none, the row stays on
    // one line.
    int wrapWidth = MaximumSize.Width > 0 ? MaximumSize.Width : IconTextLayout.Unbounded;
    return GetLayout(wrapWidth).Size;
  }

  /// <inheritdoc />
  protected override void OnTextChanged(EventArgs e)
  {
    base.OnTextChanged(e);
    // A stock Label re-lays-out on a text change; this control must too, because rows are reused for
    // other records and the new text can need a different width or wrap.
    if (AutoSize)
    {
      Parent?.PerformLayout(this, nameof(Text));
    }
    Invalidate();
  }

  /// <inheritdoc />
  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    IconTextLayout.DrawLayout(
      e.Graphics,
      GetLayout(ClientRectangle.Width),
      Font,
      ClientRectangle,
      ForeColor,
      _icons
    );
  }

  /// <summary>
  /// The layout for <paramref name="width"/>, reused until the text, font, or width changes. WinForms
  /// asks for the preferred size many times per layout pass and paints on every invalidation; laying
  /// the row out measures every word through GDI, so redoing it each time made the window lag.
  /// </summary>
  private (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> Items, Size Size) GetLayout(
    int width
  )
  {
    if (
      _cached is { } cached
      && cached.Width == width
      && cached.Text == Text
      && ReferenceEquals(cached.Font, Font)
    )
    {
      return cached.Layout;
    }

    (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> Items, Size Size) layout =
      IconTextLayout.Layout(Text, Font, width);
    _cached = (width, Text, Font, layout);
    return layout;
  }
}
