using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// Builds the stock, palette-colored <see cref="Button"/> shared by every button in the app
/// (AGENTS.md §8.5/§17): <see cref="StopwatchControl"/>'s transport row, <see cref="RecordsListControl"/>'s
/// and <see cref="ManageRecordsForm"/>'s header/pagination/row buttons, and the confirm dialogs'
/// action/cancel buttons. <see cref="FlatStyle.Flat"/> is fully stock (no owner-drawn <c>OnPaint</c>)
/// so it keeps the native keyboard-focus indicator and dark-mode/high-contrast behavior, at the cost
/// of square corners — WinForms has no stock style that gives both rounded corners and an arbitrary
/// fill color (AGENTS.md §17).
/// </summary>
internal static class ButtonFactory
{
  /// <summary>
  /// Creates a button styled from a <see cref="Palette"/> base/hover/pressed triplet.
  /// </summary>
  /// <param name="text">The button's label.</param>
  /// <param name="colors">The base/hover/pressed fill colors, from <see cref="Palette"/>.</param>
  internal static Button Create(string text, (Color Base, Color Hover, Color Pressed) colors)
  {
    Button button = new()
    {
      Text = text,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      // Matches GlyphButton's former padding exactly, so every width formula that measures a
      // button's preferred size (MainForm.HeaderActionsWidth, ManageRecordsForm.RequiredClientWidth/
      // ConstrainRecordDetails) still holds (AGENTS.md §17).
      Padding = new Padding(
        Palette.SpacingMd,
        Palette.SpacingSm,
        Palette.SpacingMd,
        Palette.SpacingSm
      ),
      FlatStyle = FlatStyle.Flat,
      BackColor = colors.Base,
      ForeColor = Color.White,
      // Required for BackColor to take effect under visual styles — without it, FlatStyle.Flat
      // still paints the stock theme's own flat button color instead of ours.
      UseVisualStyleBackColor = false,
    };
    button.FlatAppearance.BorderSize = 0;
    button.FlatAppearance.MouseOverBackColor = colors.Hover;
    button.FlatAppearance.MouseDownBackColor = colors.Pressed;
    return button;
  }
}
