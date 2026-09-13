using StopwatchApp.Services;

namespace StopwatchApp.Tests;

public sealed class AutosaveSettingsTests
{
  [Fact]
  public void Load_ValidInterval_UsesConfiguredValue()
  {
    string path = CreateSettingsFile("{ \"AutosaveIntervalMinutes\": 12 }");

    try
    {
      AutosaveSettings settings = AutosaveSettings.Load(path);

      Assert.Equal(12, settings.AutosaveIntervalMinutes);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Theory]
  [InlineData("{ }")]
  [InlineData("{ \"AutosaveIntervalMinutes\": 0 }")]
  [InlineData("{ \"AutosaveIntervalMinutes\": -1 }")]
  [InlineData("not json")]
  public void Load_MissingMalformedOrInvalidValue_UsesDefault(string json)
  {
    string path = CreateSettingsFile(json);

    try
    {
      AutosaveSettings settings = AutosaveSettings.Load(path);

      Assert.Equal(
        AutosaveSettings.DefaultAutosaveIntervalMinutes,
        settings.AutosaveIntervalMinutes
      );
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void Load_MissingFile_UsesDefault()
  {
    string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

    AutosaveSettings settings = AutosaveSettings.Load(path);

    Assert.Equal(AutosaveSettings.DefaultAutosaveIntervalMinutes, settings.AutosaveIntervalMinutes);
  }

  private static string CreateSettingsFile(string json)
  {
    string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    return path;
  }
}
