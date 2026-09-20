using System.Text.Json;

namespace StopwatchApp.Services;

/// <summary>
/// The validated values loaded from the application's optional <c>settings.json</c> file. Each
/// property is validated independently, so one bad value never discards the other.
/// </summary>
/// <param name="AutosaveIntervalMinutes">The interval between running-time checkpoints, in minutes.</param>
/// <param name="StopConfirmationAfterMinutes">
/// The elapsed minutes after which Stop asks for confirmation; <c>0</c> disables the confirmation.
/// </param>
public sealed record AppSettings(int AutosaveIntervalMinutes, int StopConfirmationAfterMinutes)
{
  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  /// <summary>The fallback checkpoint interval used when configuration is absent or invalid.</summary>
  public const int DefaultAutosaveIntervalMinutes = 5;

  /// <summary>The fallback Stop-confirmation threshold used when configuration is absent or invalid.</summary>
  public const int DefaultStopConfirmationAfterMinutes = 5;

  /// <summary>Gets the settings file name expected beside the application executable.</summary>
  public const string FileName = "settings.json";

  /// <summary>Gets the settings used when the file is missing, unreadable, or malformed.</summary>
  public static AppSettings Defaults { get; } =
    new(DefaultAutosaveIntervalMinutes, DefaultStopConfirmationAfterMinutes);

  /// <summary>
  /// Loads settings from the file beside the application executable, with the same fallbacks as
  /// <see cref="Load"/>.
  /// </summary>
  /// <returns>The validated settings.</returns>
  public static AppSettings LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

  /// <summary>
  /// Loads settings from <paramref name="path"/>. A missing, unreadable, or malformed file uses
  /// both defaults. A zero, negative, or missing autosave interval uses
  /// <see cref="DefaultAutosaveIntervalMinutes"/>; a missing stop-confirmation threshold uses
  /// <see cref="DefaultStopConfirmationAfterMinutes"/>, while zero or a negative value disables the
  /// confirmation (<c>0</c>).
  /// </summary>
  /// <param name="path">The JSON settings file to read.</param>
  /// <returns>The validated settings.</returns>
  public static AppSettings Load(string path)
  {
    try
    {
      string json = File.ReadAllText(path);
      RawSettings? raw = JsonSerializer.Deserialize<RawSettings>(json, SerializerOptions);
      if (raw is null)
      {
        return Defaults;
      }

      int autosave = raw.AutosaveIntervalMinutes is > 0
        ? raw.AutosaveIntervalMinutes.Value
        : DefaultAutosaveIntervalMinutes;
      int stopConfirmation = raw.StopConfirmationAfterMinutes is { } minutes
        ? Math.Max(0, minutes)
        : DefaultStopConfirmationAfterMinutes;
      return new AppSettings(autosave, stopConfirmation);
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
      return Defaults;
    }
  }

  // Nullable mirror of the record so a missing key is distinguishable from an explicit 0.
  private sealed record RawSettings(
    int? AutosaveIntervalMinutes,
    int? StopConfirmationAfterMinutes
  );
}
