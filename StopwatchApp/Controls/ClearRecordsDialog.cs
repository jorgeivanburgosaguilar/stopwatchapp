using StopwatchApp.Theme;

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
      Padding = new Padding(Palette.SpacingLg),
    };

    Label message = new()
    {
      Text = "Are you sure you want to clear all records? This action cannot be undone.",
      AutoSize = true,
      MaximumSize = new Size(320, 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };

    Button clearAllButton = new()
    {
      Text = "Clear All",
      DialogResult = DialogResult.Yes,
      AutoSize = true,
      FlatStyle = FlatStyle.Flat,
      Padding = new Padding(
        Palette.SpacingMd,
        Palette.SpacingXs,
        Palette.SpacingMd,
        Palette.SpacingXs
      ),
    };
    Button cancelButton = new()
    {
      Text = "Cancel",
      DialogResult = DialogResult.Cancel,
      AutoSize = true,
      FlatStyle = FlatStyle.Flat,
      Padding = new Padding(
        Palette.SpacingMd,
        Palette.SpacingXs,
        Palette.SpacingMd,
        Palette.SpacingXs
      ),
      Margin = new Padding(0, 0, Palette.SpacingSm, 0),
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

    // S11a: round the two buttons' corners (AGENTS.md §11/§17). A Region clip on an already-Flat
    // button is a cheap, one-time (no per-frame owner paint) way to do this for a dialog that never
    // resizes — PerformLayout first so Width/Height reflect the final AutoSize result.
    dialog.PerformLayout();
    ApplyRoundedRegion(clearAllButton);
    ApplyRoundedRegion(cancelButton);

    // The dialog's own outer corners are not rounded here: Windows 11 already rounds top-level
    // window frames at the DWM level by default, so AGENTS.md §11's DialogCornerRadius token needs
    // no extra rendering — see AGENTS.md §17.
    return dialog.ShowDialog(owner);
  }

  private static void ApplyRoundedRegion(Control control)
  {
    Rectangle bounds = new(0, 0, control.Width, control.Height);
    control.Region = new Region(RoundedRectangle.Path(bounds, Palette.ControlCornerRadius));
  }
}
