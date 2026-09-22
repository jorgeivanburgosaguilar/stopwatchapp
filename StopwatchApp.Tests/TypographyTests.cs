using StopwatchApp.Theme;

namespace StopwatchApp.Tests;

/// <summary>
/// Covers <see cref="Typography"/>'s monospace family resolution — the one piece of the documented type
/// scale that depends on the running machine's installed fonts rather than a fixed literal.
/// </summary>
public sealed class TypographyTests
{
  private static readonly string[] ExpectedMonospaceFamilies = ["Cascadia Mono", "Consolas"];

  [Fact]
  public void DisplayPointSize_IsDoubleThePreviousDesignSize() =>
    Assert.Equal(72f, Typography.DisplayPointSize);

  [Fact]
  public void LapDisplayPointSize_Is30PercentOfDisplayPointSize() =>
    Assert.Equal(Typography.DisplayPointSize * 0.3f, Typography.LapDisplayPointSize);

  [Fact]
  public void CreateLapDisplayFont_IsBoldMonospaceAtLapDisplayPointSize()
  {
    using Font font = Typography.CreateLapDisplayFont();

    Assert.Equal(Typography.MonospaceFamilyName, font.FontFamily.Name);
    Assert.True(font.Bold);
    Assert.Equal(Typography.LapDisplayPointSize, font.Size);
  }

  [Fact]
  public void MonospaceFamilyName_ResolvesToInstalledFamily()
  {
    string name = Typography.MonospaceFamilyName;

    Assert.False(string.IsNullOrWhiteSpace(name));
    using FontFamily family = new(name);
    Assert.True(
      family.IsStyleAvailable(FontStyle.Regular) || family.IsStyleAvailable(FontStyle.Bold)
    );
  }

  [Fact]
  public void MonospaceFamilyName_IsCascadiaMonoOrConsolas()
  {
    // AGENTS.md §8.5/§11: Cascadia Mono when installed (ships with Windows 11), else Consolas.
    // (ships with every Windows since Vista) — never a silent fallback to a proportional font.
    Assert.Contains(Typography.MonospaceFamilyName, ExpectedMonospaceFamilies);
  }
}
