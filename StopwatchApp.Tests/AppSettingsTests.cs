using StopwatchApp.Services;

namespace StopwatchApp.Tests;

public sealed class AppSettingsTests
{
  [Fact]
  public void Load_ValidInterval_UsesConfiguredValue()
  {
    string path = CreateSettingsFile("{ \"AutosaveIntervalMinutes\": 12 }");

    try
    {
      AppSettings settings = AppSettings.Load(path);

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
      AppSettings settings = AppSettings.Load(path);

      Assert.Equal(AppSettings.DefaultAutosaveIntervalMinutes, settings.AutosaveIntervalMinutes);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void Load_MissingFile_UsesDefaults()
  {
    string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

    AppSettings settings = AppSettings.Load(path);

    Assert.Equal(AppSettings.Defaults, settings);
  }

  [Fact]
  public void Load_ValidStopConfirmation_UsesConfiguredValue()
  {
    string path = CreateSettingsFile("{ \"StopConfirmationAfterMinutes\": 12 }");

    try
    {
      AppSettings settings = AppSettings.Load(path);

      Assert.Equal(12, settings.StopConfirmationAfterMinutes);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Theory]
  [InlineData("{ \"StopConfirmationAfterMinutes\": 0 }", 0)]
  [InlineData("{ \"StopConfirmationAfterMinutes\": -3 }", 0)]
  [InlineData("{ }", AppSettings.DefaultStopConfirmationAfterMinutes)]
  [InlineData("not json", AppSettings.DefaultStopConfirmationAfterMinutes)]
  public void Load_StopConfirmationZeroNegativeMissingOrMalformed_Normalizes(
    string json,
    int expected
  )
  {
    string path = CreateSettingsFile(json);

    try
    {
      AppSettings settings = AppSettings.Load(path);

      Assert.Equal(expected, settings.StopConfirmationAfterMinutes);
    }
    finally
    {
      File.Delete(path);
    }
  }

  [Fact]
  public void Load_InvalidAutosaveDoesNotDiscardStopConfirmation()
  {
    string path = CreateSettingsFile(
      "{ \"AutosaveIntervalMinutes\": 0, \"StopConfirmationAfterMinutes\": 12 }"
    );

    try
    {
      AppSettings settings = AppSettings.Load(path);

      Assert.Equal(AppSettings.DefaultAutosaveIntervalMinutes, settings.AutosaveIntervalMinutes);
      Assert.Equal(12, settings.StopConfirmationAfterMinutes);
    }
    finally
    {
      File.Delete(path);
    }
  }

  private static string CreateSettingsFile(string json)
  {
    string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    return path;
  }
}
