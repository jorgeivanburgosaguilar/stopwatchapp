using StopwatchApp.Formatting;
using StopwatchApp.Models;
using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// The "Resume Session" confirm dialog (AGENTS.md §8.5), shown before a recent stopped record is
/// moved back into the clock. A static helper, like <see cref="ResetConfirmationDialog"/>.
/// </summary>
public static class ResumeRecordDialog
{
  /// <summary>Gets the dialog title.</summary>
  public const string Title = "Resume Session";

  /// <summary>
  /// Builds the dialog body text. Names the record being resumed and the exact time it comes back
  /// at, and — only when one exists — warns that the live session will be discarded.
  /// </summary>
  /// <param name="record">The record to resume.</param>
  /// <param name="liveElapsedMs">
  /// The live session's elapsed time, in milliseconds, or <see langword="null"/> if there is no
  /// running or paused session that would be discarded.
  /// </param>
  /// <returns>The body text.</returns>
  public static string FormatMessage(StopwatchRecord record, long? liveElapsedMs)
  {
    long resumedMs = record.ElapsedMs ?? record.ElapsedMinutes * 60000;
    string text =
      $"Resume the session from {TimeFormat.FormatDate(record.StartTimestamp)} "
      + $"{TimeFormat.FormatTimeOnly(record.StartTimestamp)}-"
      + $"{TimeFormat.FormatTimeOnly(record.EndTimestamp)} at {TimeFormat.FormatTime(resumedMs)}? "
      + "It moves back into the stopwatch, paused, and leaves Records until you stop it again.";
    if (liveElapsedMs is long live)
    {
      text +=
        $" The current session ({TimeFormat.FormatTime(live)}) and its laps will be discarded.";
    }

    return text;
  }

  /// <summary>Shows the confirm dialog modally.</summary>
  /// <param name="owner">The window that owns the dialog.</param>
  /// <param name="record">The record to resume.</param>
  /// <param name="liveElapsedMs">The live session's elapsed time, or <see langword="null"/> if none.</param>
  /// <returns>
  /// <see cref="DialogResult.Yes"/> if the user clicked "Resume"; <see cref="DialogResult.Cancel"/>
  /// otherwise (the "Cancel" button, the dialog's close button, or Escape).
  /// </returns>
  public static DialogResult ShowConfirm(
    IWin32Window owner,
    StopwatchRecord record,
    long? liveElapsedMs
  )
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
      // Same explicit baseline as the other dialogs: WinForms scales padding, margins and the
      // message's 420 design-pixel MaximumSize for the monitor the dialog actually appears on.
      AutoScaleMode = AutoScaleMode.Dpi,
      AutoScaleDimensions = new SizeF(96F, 96F),
    };

    // 420 is a 96dpi design-pixel literal; AutoScaleMode.Dpi above scales it.
    const int messageMaxWidth = 420;
    Label message = new()
    {
      Text = FormatMessage(record, liveElapsedMs),
      AutoSize = true,
      MaximumSize = new Size(messageMaxWidth, 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };

    Button resumeButton = ButtonFactory.Create("Resume", Palette.StartButton);
    resumeButton.DialogResult = DialogResult.Yes;
    resumeButton.Margin = new Padding(0);
    Button cancelButton = ButtonFactory.Create("Cancel", Palette.CancelButton);
    cancelButton.DialogResult = DialogResult.Cancel;
    cancelButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);

    FlowLayoutPanel buttonPanel = new()
    {
      FlowDirection = FlowDirection.RightToLeft,
      AutoSize = true,
      Dock = DockStyle.Top,
    };
    buttonPanel.Controls.Add(resumeButton);
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
    // Only an explicit Resume proceeds; Escape and initial focus cancel.
    dialog.CancelButton = cancelButton;
    dialog.ActiveControl = cancelButton;

    return dialog.ShowDialog(owner);
  }
}
