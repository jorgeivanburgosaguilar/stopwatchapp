using StopwatchApp.Controls;
using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="ButtonFactory.Create"/>'s mapping from a <see cref="Palette"/> triplet to the
/// stock <see cref="Button"/>'s color properties (AGENTS.md §17) — the one part of the button
/// rewrite that is UI-free and worth pinning so the color mapping can't silently regress.
/// </summary>
public sealed class ButtonFactoryTests
{
  [Fact]
  public void Create_MapsPaletteTripletToButtonColors()
  {
    (Color Base, Color Hover, Color Pressed) colors = Palette.StopButton;

    using Button button = ButtonFactory.Create("Stop", colors);

    Assert.Equal(colors.Base, button.BackColor);
    Assert.Equal(colors.Hover, button.FlatAppearance.MouseOverBackColor);
    Assert.Equal(colors.Pressed, button.FlatAppearance.MouseDownBackColor);
    Assert.Equal(FlatStyle.Flat, button.FlatStyle);
    Assert.Equal(0, button.FlatAppearance.BorderSize);
    Assert.False(button.UseVisualStyleBackColor);
  }

  [Fact]
  public void Create_SetsLabelText()
  {
    using Button button = ButtonFactory.Create("Previous", Palette.CancelButton);

    Assert.Equal("Previous", button.Text);
  }
}
