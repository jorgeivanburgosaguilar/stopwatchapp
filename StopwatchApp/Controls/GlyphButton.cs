using System.ComponentModel;
using System.Drawing.Drawing2D;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

// The monochrome transport-button glyphs the S11a comp specifies (AGENTS.md §17): Start/Continue
// draws Play, Pause draws Pause, Lap draws Flag, Stop draws Stop. A button with no glyph (S15,
// AGENTS.md §17 — extracted so RecordsListControl's header-row buttons can reuse the same rounded,
// palette-driven paint routine without a glyph) passes null instead of one of these.
internal enum Glyph
{
  Play,
  Pause,
  Flag,
  Stop,
}

/// <summary>
/// An owner-drawn <see cref="Button"/> with rounded corners (<see cref="Palette.ControlCornerRadius"/>)
/// and an optional monochrome glyph beside its text label — originally the S11a swap-in for the
/// earlier stages' plain colored-rectangle buttons (AGENTS.md §8.5/§11), extracted from
/// <see cref="StopwatchControl"/> in S15 so <see cref="RecordsListControl"/>'s header-row buttons and
/// <see cref="ClearRecordsDialog"/>'s confirm/cancel buttons can reuse it. A <see langword="null"/>
/// <c>glyph</c> renders text only, with no glyph square or gutter. <see cref="Control.Enabled"/>
/// <see langword="false"/> paints a muted, palette-driven disabled state instead of the stock gray.
/// </summary>
internal sealed class GlyphButton : Button
{
  private readonly Glyph? _glyph;
  private readonly (Color Base, Color Hover, Color Pressed) _colors;
  private bool _hovered;
  private bool _pressed;

  /// <summary>Initializes a new instance of the <see cref="GlyphButton"/> class.</summary>
  /// <param name="text">The button's label.</param>
  /// <param name="glyph">The glyph to draw beside the label, or <see langword="null"/> for a
  /// text-only button.</param>
  /// <param name="colors">The base/hover/pressed fill colors, from <see cref="Palette"/>.</param>
  internal GlyphButton(string text, Glyph? glyph, (Color Base, Color Hover, Color Pressed) colors)
  {
    _glyph = glyph;
    _colors = colors;
    Text = text;
    AutoSize = true;
    AutoSizeMode = AutoSizeMode.GrowAndShrink;
    Padding = new Padding(
      Palette.SpacingMd,
      Palette.SpacingSm,
      Palette.SpacingMd,
      Palette.SpacingSm
    );
    FlatStyle = FlatStyle.Flat;
    FlatAppearance.BorderSize = 0;
    ForeColor = Color.White;
    SetStyle(
      ControlStyles.UserPaint
        | ControlStyles.AllPaintingInWmPaint
        | ControlStyles.OptimizedDoubleBuffer
        | ControlStyles.ResizeRedraw,
      true
    );
  }

  /// <summary>Whether to draw the dark-mode-only mica-style top-edge highlight on hover/press, and
  /// which theme's <see cref="Palette.CardBackground"/>/<see cref="Palette.MutedText"/> the disabled
  /// state blends toward.</summary>
  [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
  internal bool DarkMode { get; set; }

  /// <inheritdoc />
  public override Size GetPreferredSize(Size proposedSize)
  {
    Size textSize = TextRenderer.MeasureText(Text, Font);
    int glyphBoxSize = _glyph.HasValue ? textSize.Height : 0;
    int glyphGutter = _glyph.HasValue ? Palette.SpacingXs : 0;
    int width = Padding.Left + glyphBoxSize + glyphGutter + textSize.Width + Padding.Right;
    int height = Padding.Top + Math.Max(textSize.Height, glyphBoxSize) + Padding.Bottom;
    return new Size(width, height);
  }

  /// <inheritdoc />
  protected override void OnMouseEnter(EventArgs e)
  {
    _hovered = true;
    Invalidate();
    base.OnMouseEnter(e);
  }

  /// <inheritdoc />
  protected override void OnMouseLeave(EventArgs e)
  {
    _hovered = false;
    _pressed = false;
    Invalidate();
    base.OnMouseLeave(e);
  }

  /// <inheritdoc />
  protected override void OnMouseDown(MouseEventArgs mevent)
  {
    _pressed = true;
    Invalidate();
    base.OnMouseDown(mevent);
  }

  /// <inheritdoc />
  protected override void OnMouseUp(MouseEventArgs mevent)
  {
    _pressed = false;
    Invalidate();
    base.OnMouseUp(mevent);
  }

  /// <inheritdoc />
  protected override void OnEnabledChanged(EventArgs e)
  {
    Invalidate();
    base.OnEnabledChanged(e);
  }

  /// <inheritdoc />
  protected override void OnPaintBackground(PaintEventArgs pevent)
  {
    // Fills the area outside the rounded fill path (below) with the ambient BackColor, which
    // Control resolves from this button's parent chain up to the owning card's own palette-driven
    // BackColor, so the corners always match the surrounding card.
    pevent.Graphics.Clear(BackColor);
  }

  /// <inheritdoc />
  protected override void OnPaint(PaintEventArgs pevent)
  {
    Graphics graphics = pevent.Graphics;
    graphics.SmoothingMode = SmoothingMode.AntiAlias;

    // S15 (AGENTS.md §17) — a disabled button (currently only the "Manage Records" placeholder)
    // blends its base color toward the surrounding card background instead of the stock gray, so it
    // reads as "not yet available" without breaking the card's own color scheme.
    Color fill;
    Color labelColor;
    if (Enabled)
    {
      fill =
        _pressed ? _colors.Pressed
        : _hovered ? _colors.Hover
        : _colors.Base;
      labelColor = ForeColor;
    }
    else
    {
      fill = Blend(_colors.Base, Palette.CardBackground(DarkMode), 0.6f);
      labelColor = Palette.MutedText(DarkMode);
    }

    Rectangle bounds = new(0, 0, Width - 1, Height - 1);
    using GraphicsPath path = RoundedRectangle.Path(bounds, Palette.ControlCornerRadius);
    using (SolidBrush fillBrush = new(fill))
    {
      graphics.FillPath(fillBrush, path);
    }

    // Dark-mode-only mica-style top-edge highlight on hover/press (AGENTS.md §11/§17); light mode
    // has no equivalent comp token, so it draws nothing there.
    if (DarkMode && Enabled && (_hovered || _pressed))
    {
      using Pen highlightPen = new(Palette.TopEdgeHighlight, 1f);
      graphics.DrawLine(
        highlightPen,
        bounds.Left + Palette.ControlCornerRadius,
        bounds.Top + 1,
        bounds.Right - Palette.ControlCornerRadius,
        bounds.Top + 1
      );
    }

    int glyphSize = _glyph.HasValue ? TextRenderer.MeasureText(Text, Font).Height : 0;
    int textLeft = Padding.Left;
    if (_glyph.HasValue)
    {
      Rectangle glyphRect = new(Padding.Left, (Height - glyphSize) / 2, glyphSize, glyphSize);
      DrawGlyph(graphics, _glyph.Value, glyphRect, labelColor);
      textLeft = glyphRect.Right + Palette.SpacingXs;
    }

    // S15 follow-up (AGENTS.md §17) — HorizontalCenter, not Left: for an AutoSize button (the
    // common case) this rect already matches the text's own width, so the two flags render
    // identically there, but the repo owner asked explicitly for centered button text, and
    // HorizontalCenter is correct/robust if a button is ever stretched wider than its own preferred
    // size (e.g. shared-width buttons in a future layout).
    Rectangle textRect = new(textLeft, 0, Math.Max(0, Width - textLeft - Padding.Right), Height);
    TextRenderer.DrawText(
      graphics,
      Text,
      Font,
      textRect,
      labelColor,
      TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding
    );
  }

  private static Color Blend(Color from, Color toward, float amount)
  {
    int Lerp(int a, int b) => a + (int)((b - a) * amount);
    return Color.FromArgb(Lerp(from.R, toward.R), Lerp(from.G, toward.G), Lerp(from.B, toward.B));
  }

  private static void DrawGlyph(Graphics graphics, Glyph glyph, Rectangle rect, Color color)
  {
    using SolidBrush brush = new(color);
    int pad = Math.Max(2, rect.Width / 5);
    Rectangle inner = Rectangle.Inflate(rect, -pad, -pad);
    switch (glyph)
    {
      case Glyph.Play:
        graphics.FillPolygon(
          brush,
          [
            new Point(inner.Left, inner.Top),
            new Point(inner.Left, inner.Bottom),
            new Point(inner.Right, inner.Top + (inner.Height / 2)),
          ]
        );
        break;
      case Glyph.Pause:
        int barWidth = Math.Max(2, inner.Width / 3);
        graphics.FillRectangle(brush, inner.Left, inner.Top, barWidth, inner.Height);
        graphics.FillRectangle(brush, inner.Right - barWidth, inner.Top, barWidth, inner.Height);
        break;
      case Glyph.Flag:
        int poleWidth = Math.Max(1, inner.Width / 8);
        graphics.FillRectangle(brush, inner.Left, inner.Top, poleWidth, inner.Height);
        graphics.FillPolygon(
          brush,
          [
            new Point(inner.Left + poleWidth, inner.Top),
            new Point(inner.Right, inner.Top + (inner.Height / 4)),
            new Point(inner.Left + poleWidth, inner.Top + (inner.Height / 2)),
          ]
        );
        break;
      case Glyph.Stop:
        graphics.FillRectangle(brush, inner);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(glyph), glyph, message: null);
    }
  }
}
