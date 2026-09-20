using StopwatchApp.Formatting;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// The "Stop Stopwatch" confirm dialog (AGENTS.md §8.5), shown when Stop is pressed after the
/// configured elapsed-time threshold. A static helper, like <see cref="ClearRecordsDialog"/>.
/// </summary>
public static class StopConfirmationDialog
{
  /// <summary>Gets the dialog title.</summary>
  public const string Title = "Stop Stopwatch";

  /// <summary>Builds the dialog body text for the given elapsed time.</summary>
  /// <param name="elapsedMs">The frozen elapsed time, in milliseconds.</param>
  /// <returns>The body text.</returns>
  public static string FormatMessage(long elapsedMs) =>
    $"The stopwatch is at {TimeFormat.FormatTime(elapsedMs)}. "
    + "Stop it and save this session as a record?";

  /// <summary>Shows the confirm dialog modally.</summary>
  /// <param name="owner">The window that owns the dialog.</param>
  /// <param name="elapsedMs">The frozen elapsed time, in milliseconds.</param>
  /// <returns>
  /// <see cref="DialogResult.Yes"/> if the user clicked "Stop"; <see cref="DialogResult.Cancel"/>
  /// otherwise (the "Cancel" button, the dialog's close button, or Escape).
  /// </returns>
  public static DialogResult ShowConfirm(IWin32Window owner, long elapsedMs)
  {
    // A modal Form does not inherit MainForm.Font, so the dialog needs its own body font.
    using Font bodyFont = Typography.CreateBodyFont();
    using Form dialog = new()
    {
      Text = Title,
      FormBorderStyle = FormBorderStyle.FixedDialog,
      StartPosition = FormStartPosition.CenterParent,
      MinimizeBox = false,
      MaximizeBox = false,
      ShowInTaskbar = false,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Padding = new Padding(Palette.SpacingLg),
      Font = bodyFont,
    };

    // 420 is a 96dpi design-pixel literal, scaled to the dialog's real device DPI (same
    // convention as ClearRecordsDialog).
    int messageMaxWidth = (int)Math.Ceiling(420 * (dialog.DeviceDpi / 96f));
    Label message = new()
    {
      Text = FormatMessage(elapsedMs),
      AutoSize = true,
      MaximumSize = new Size(messageMaxWidth, 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };

    Button stopButton = ButtonFactory.Create("Stop", Palette.StopButton);
    stopButton.DialogResult = DialogResult.Yes;
    stopButton.Margin = new Padding(0);
    Button cancelButton = ButtonFactory.Create("Cancel", Palette.CancelButton);
    cancelButton.DialogResult = DialogResult.Cancel;
    cancelButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);

    FlowLayoutPanel buttonPanel = new()
    {
      FlowDirection = FlowDirection.RightToLeft,
      AutoSize = true,
      Dock = DockStyle.Top,
    };
    buttonPanel.Controls.Add(stopButton);
    buttonPanel.Controls.Add(cancelButton);

    TableLayoutPanel layout = new()
    {
      ColumnCount = 1,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Dock = DockStyle.Fill,
    };
    layout.Controls.Add(message);
    layout.Controls.Add(buttonPanel);

    dialog.Controls.Add(layout);
    // No AcceptButton: Enter is the app's Stop shortcut, so the keypress that opened this dialog
    // must never also confirm it.
    dialog.CancelButton = cancelButton;

    return dialog.ShowDialog(owner);
  }
}
