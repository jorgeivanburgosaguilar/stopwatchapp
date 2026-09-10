namespace StopwatchApp.Controls;

/// <summary>
/// The "Clear All Records" confirm dialog (AGENTS.md §8.5). A static helper, not a reusable
/// instance — <see cref="RecordsListControl"/>'s owner shows it at most once per click of the
/// "Clear All Records" button.
/// </summary>
public static class ClearRecordsDialog
{
  /// <summary>
  /// Shows the confirm dialog modally, with the exact title, body, and button text AGENTS.md §8.5
  /// specifies.
  /// </summary>
  /// <param name="owner">The window that owns the dialog.</param>
  /// <returns>
  /// <see cref="DialogResult.Yes"/> if the user clicked "Clear All"; <see cref="DialogResult.Cancel"/>
  /// otherwise (the "Cancel" button, the dialog's close button, or Escape).
  /// </returns>
  public static DialogResult ShowConfirm(IWin32Window owner)
  {
    using Form dialog = new()
    {
      Text = "Clear All Records",
      FormBorderStyle = FormBorderStyle.FixedDialog,
      StartPosition = FormStartPosition.CenterParent,
      MinimizeBox = false,
      MaximizeBox = false,
      ShowInTaskbar = false,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Padding = new Padding(16),
    };

    Label message = new()
    {
      Text = "Are you sure you want to clear all records? This action cannot be undone.",
      AutoSize = true,
      MaximumSize = new Size(320, 0),
      Margin = new Padding(0, 0, 0, 16),
      Dock = DockStyle.Top,
    };

    Button clearAllButton = new()
    {
      Text = "Clear All",
      DialogResult = DialogResult.Yes,
      AutoSize = true,
    };
    Button cancelButton = new()
    {
      Text = "Cancel",
      DialogResult = DialogResult.Cancel,
      AutoSize = true,
      Margin = new Padding(0, 0, 8, 0),
    };

    FlowLayoutPanel buttonPanel = new()
    {
      FlowDirection = FlowDirection.RightToLeft,
      AutoSize = true,
      Dock = DockStyle.Top,
    };
    buttonPanel.Controls.Add(clearAllButton);
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
    // No AcceptButton: Enter should never trigger the destructive action by default.
    dialog.CancelButton = cancelButton;

    return dialog.ShowDialog(owner);
  }
}
