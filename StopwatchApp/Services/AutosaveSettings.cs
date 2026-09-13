using System.Text.Json;

namespace StopwatchApp.Services;

/// <summary>
/// The validated autosave cadence loaded from the application's optional <c>settings.json</c>
/// file.
/// </summary>
/// <param name="AutosaveIntervalMinutes">The interval between running-time checkpoints, in minutes.</param>
public sealed record AutosaveSettings(int AutosaveIntervalMinutes)
{
  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  /// <summary>The fallback checkpoint interval used when configuration is absent or invalid.</summary>
  public const int DefaultAutosaveIntervalMinutes = 5;

  /// <summary>Gets the settings file name expected beside the application executable.</summary>
  public const string FileName = "settings.json";

  /// <summary>
  /// Loads settings from the file beside the application executable. Missing, unreadable, malformed,
  /// zero, and negative values use <see cref="DefaultAutosaveIntervalMinutes"/>.
  /// </summary>
  /// <returns>The validated autosave settings.</returns>
  public static AutosaveSettings LoadDefault() =>
    Load(Path.Combine(AppContext.BaseDirectory, FileName));

  /// <summary>
  /// Loads settings from <paramref name="path"/>. Missing, unreadable, malformed, zero, and
  /// negative values use <see cref="DefaultAutosaveIntervalMinutes"/>.
  /// </summary>
  /// <param name="path">The JSON settings file to read.</param>
  /// <returns>The validated autosave settings.</returns>
  public static AutosaveSettings Load(string path)
  {
    try
    {
      string json = File.ReadAllText(path);
      AutosaveSettings? settings = JsonSerializer.Deserialize<AutosaveSettings>(
        json,
        SerializerOptions
      );
      if (settings is { AutosaveIntervalMinutes: > 0 })
      {
        return settings;
      }

      return new AutosaveSettings(DefaultAutosaveIntervalMinutes);
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
      return new AutosaveSettings(DefaultAutosaveIntervalMinutes);
    }
  }
}
