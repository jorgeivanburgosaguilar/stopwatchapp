using System.Text;

namespace StopwatchApp.Controls;

/// <summary>
/// The one measure/draw engine for record and lap rows (AGENTS.md §8.5). The row strings keep their
/// literal emoji; this lays them out with the emoji replaced by embedded color images, because
/// <see cref="TextRenderer"/> (GDI) can only paint a color font's monochrome fallback outline.
/// Measuring and drawing share <see cref="Layout"/> so a wrapped row is never sized one way and
/// painted another. Pure functions over <c>(text, font, width)</c>, so word-wrap is unit-testable
/// without a live control (AGENTS.md §13).
/// </summary>
internal static class IconTextLayout
{
  private const TextFormatFlags MeasureFlags =
    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

  /// <summary>
  /// The largest width to pass as "unconstrained". Not <see cref="int.MaxValue"/>, so adding an atom
  /// width to a line position can never overflow.
  /// </summary>
  internal const int Unbounded = 1 << 24;

  /// <summary>
  /// Splits <paramref name="text"/> into atoms: words, single spaces (the literal text
  /// <c>" "</c>), and icons (text <see langword="null"/>). A word is a maximal run of characters that
  /// are neither a space nor a recognized emoji.
  /// </summary>
  /// <param name="text">The row text.</param>
  internal static List<(string? Text, RowIcon? Icon)> Tokenize(string text)
  {
    List<(string? Text, RowIcon? Icon)> atoms = [];
    StringBuilder word = new();

    void FlushWord()
    {
      if (word.Length > 0)
      {
        atoms.Add((word.ToString(), null));
        word.Clear();
      }
    }

    for (int i = 0; i < text.Length; i++)
    {
      RowIcon? icon = IconAt(text, i, out int length);
      if (icon is not null)
      {
        FlushWord();
        atoms.Add((null, icon));
        i += length - 1;
      }
      else if (text[i] == ' ')
      {
        FlushWord();
        atoms.Add((" ", null));
      }
      else
      {
        word.Append(text[i]);
      }
    }
    FlushWord();
    return atoms;
  }

  /// <summary>
  /// Lays <paramref name="text"/> out word-wrapped to <paramref name="availableWidth"/>. An icon is an
  /// atomic <c>font.Height</c>-square box that is never split across a line; a word wider than the
  /// whole line is placed alone and overflows rather than being broken mid-word.
  /// </summary>
  /// <param name="text">The row text.</param>
  /// <param name="font">The row font; the icon size and line height derive from it, so DPI scaling
  /// is inherited from the already-scaled font.</param>
  /// <param name="availableWidth">The width available before wrapping.</param>
  /// <returns>The placed items (relative to the origin) and the overall size.</returns>
  internal static (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> Items, Size Size) Layout(
    string text,
    Font font,
    int availableWidth
  )
  {
    int lineHeight = font.Height;
    int spaceWidth = Math.Max(
      1,
      TextRenderer.MeasureText("a a", font, Size.Empty, MeasureFlags).Width
        - TextRenderer.MeasureText("aa", font, Size.Empty, MeasureFlags).Width
    );
    int limit = Math.Max(1, availableWidth);

    List<(Rectangle Bounds, string? Text, RowIcon? Icon)> items = [];
    int x = 0;
    int y = 0;
    int widest = 0;
    bool lineHasContent = false;

    foreach ((string? atomText, RowIcon? icon) in Tokenize(text))
    {
      if (icon is null && atomText == " ")
      {
        // A space only advances the cursor; a wrap decision belongs to the next real atom, and a
        // space that lands at the start of a wrapped line is simply dropped.
        if (lineHasContent)
        {
          x += spaceWidth;
        }
        continue;
      }

      int width = icon is not null
        ? lineHeight
        : TextRenderer.MeasureText(atomText ?? string.Empty, font, Size.Empty, MeasureFlags).Width;

      if (lineHasContent && x + width > limit)
      {
        x = 0;
        y += lineHeight;
        lineHasContent = false;
      }

      items.Add((new Rectangle(x, y, width, lineHeight), atomText, icon));
      x += width;
      widest = Math.Max(widest, x);
      lineHasContent = true;
    }

    return (items, new Size(widest, y + lineHeight));
  }

  /// <summary>Measures <paramref name="text"/> word-wrapped to <paramref name="availableWidth"/>.</summary>
  /// <param name="text">The row text.</param>
  /// <param name="font">The row font.</param>
  /// <param name="availableWidth">The width available before wrapping.</param>
  internal static Size Measure(string text, Font font, int availableWidth) =>
    Layout(text, font, availableWidth).Size;

  /// <summary>Measures <paramref name="text"/> on a single unwrapped line.</summary>
  /// <param name="text">The row text.</param>
  /// <param name="font">The row font.</param>
  internal static Size MeasureSingleLine(string text, Font font) =>
    Layout(text, font, Unbounded).Size;

  /// <summary>
  /// Draws <paramref name="text"/> inside <paramref name="bounds"/>, vertically centered, using the
  /// same layout <see cref="Measure"/> reports.
  /// </summary>
  /// <param name="graphics">The target graphics.</param>
  /// <param name="text">The row text.</param>
  /// <param name="font">The row font.</param>
  /// <param name="bounds">The area to draw into; its width drives wrapping.</param>
  /// <param name="color">The text color. Icons keep their own colors.</param>
  /// <param name="icons">The decoded icons.</param>
  internal static void Draw(
    Graphics graphics,
    string text,
    Font font,
    Rectangle bounds,
    Color color,
    RowIconSet icons
  )
  {
    DrawLayout(graphics, Layout(text, font, bounds.Width), font, bounds, color, icons);
  }

  /// <summary>
  /// Draws an already computed <paramref name="layout"/> (from <see cref="Layout"/>), so a caller that
  /// repaints often can cache the measurement instead of redoing it on every paint.
  /// </summary>
  /// <param name="graphics">The target graphics.</param>
  /// <param name="layout">The layout to draw, computed for <paramref name="bounds"/>'s width.</param>
  /// <param name="font">The row font the layout was computed with.</param>
  /// <param name="bounds">The area to draw into.</param>
  /// <param name="color">The text color. Icons keep their own colors.</param>
  /// <param name="icons">The decoded icons.</param>
  internal static void DrawLayout(
    Graphics graphics,
    (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> Items, Size Size) layout,
    Font font,
    Rectangle bounds,
    Color color,
    RowIconSet icons
  )
  {
    (List<(Rectangle Bounds, string? Text, RowIcon? Icon)> items, Size size) = layout;
    int top = bounds.Top + Math.Max(0, (bounds.Height - size.Height) / 2);

    foreach ((Rectangle itemBounds, string? itemText, RowIcon? icon) in items)
    {
      Rectangle placed = new(
        bounds.Left + itemBounds.X,
        top + itemBounds.Y,
        itemBounds.Width,
        itemBounds.Height
      );
      if (icon is RowIcon rowIcon)
      {
        // Pre-scaled to exactly this size, so this is a 1:1 blit with no per-paint resampling.
        graphics.DrawImage(icons.GetScaled(rowIcon, placed.Width), placed);
      }
      else if (itemText is not null)
      {
        TextRenderer.DrawText(graphics, itemText, font, placed.Location, color, MeasureFlags);
      }
    }
  }

  /// <summary>
  /// Recognizes an emoji at <paramref name="index"/>, including a trailing emoji-presentation
  /// selector (U+FE0F), and reports how many UTF-16 code units it spans.
  /// </summary>
  private static RowIcon? IconAt(string text, int index, out int length)
  {
    RowIcon? icon = null;
    length = 0;
    if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length)
    {
      if (char.ConvertToUtf32(text[index], text[index + 1]) == 0x1F4C5)
      {
        icon = RowIcon.Calendar;
        length = 2;
      }
    }
    else if (text[index] == '⏱')
    {
      icon = RowIcon.Stopwatch;
      length = 1;
    }
    else if (text[index] == '⏳')
    {
      icon = RowIcon.Hourglass;
      length = 1;
    }

    if (icon is not null && index + length < text.Length && text[index + length] == '️')
    {
      length++;
    }
    return icon;
  }
}
