using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>
/// Mandatory confirmation before discarding the current session and its recovery snapshot.
/// </summary>
public static class ResetConfirmationDialog
{
  /// <summary>Gets the dialog title.</summary>
  public const string Title = "Reset Stopwatch";

  /// <summary>Gets the explanation of the session data discarded by Reset.</summary>
  public const string Message =
    "Reset the clock to zero and discard this session, all its laps, and its recovery snapshot? "
    + "Previously saved records will be kept.";

  /// <summary>Shows the confirm dialog modally.</summary>
  /// <param name="owner">The window that owns the dialog.</param>
  /// <returns>
  /// <see cref="DialogResult.Yes"/> if the user clicked "Reset"; <see cref="DialogResult.Cancel"/>
  /// otherwise (the "Cancel" button, the dialog's close button, or Escape).
  /// </returns>
  public static DialogResult ShowConfirm(IWin32Window owner)
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
      // Same explicit baseline as the main and records windows: WinForms then scales this dialog's
      // padding, margins and the message's 420 design-pixel MaximumSize for the monitor it actually
      // appears on, instead of the DPI an unshown dialog reports before it is placed.
      AutoScaleMode = AutoScaleMode.Dpi,
      AutoScaleDimensions = new SizeF(96F, 96F),
    };

    // 420 is a 96dpi design-pixel literal; AutoScaleMode.Dpi above scales it.
    const int messageMaxWidth = 420;
    Label message = new()
    {
      Text = Message,
      AutoSize = true,
      MaximumSize = new Size(messageMaxWidth, 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };

    Button resetButton = ButtonFactory.Create("Reset", Palette.ResetButton);
    resetButton.DialogResult = DialogResult.Yes;
    resetButton.Margin = new Padding(0);
    Button cancelButton = ButtonFactory.Create("Cancel", Palette.CancelButton);
    cancelButton.DialogResult = DialogResult.Cancel;
    cancelButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);

    FlowLayoutPanel buttonPanel = new()
    {
      FlowDirection = FlowDirection.RightToLeft,
      AutoSize = true,
      Dock = DockStyle.Top,
    };
    buttonPanel.Controls.Add(resetButton);
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
    // Only an explicit Reset activates the destructive action; Escape and initial focus cancel.
    dialog.CancelButton = cancelButton;
    dialog.ActiveControl = cancelButton;

    return dialog.ShowDialog(owner);
  }
}
