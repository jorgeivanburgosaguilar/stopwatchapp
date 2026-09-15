using StopwatchApp.Theme;

namespace StopwatchApp.Controls;

/// <summary>Confirmation dialog for permanently deleting one saved record.</summary>
internal static class DeleteRecordDialog
{
  internal static DialogResult ShowConfirm(IWin32Window owner)
  {
    using Font bodyFont = Typography.CreateBodyFont();
    using Form dialog = new()
    {
      Text = "Delete Record",
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

    Label message = new()
    {
      Text = "Are you sure you want to delete this record? This action cannot be undone.",
      AutoSize = true,
      MaximumSize = new Size((int)Math.Ceiling(420 * (dialog.DeviceDpi / 96f)), 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };
    Button deleteButton = ButtonFactory.Create("Delete", Palette.StopButton);
    deleteButton.DialogResult = DialogResult.Yes;
    deleteButton.Margin = new Padding(0);
    Button cancelButton = ButtonFactory.Create("Cancel", Palette.CancelButton);
    cancelButton.DialogResult = DialogResult.Cancel;
    cancelButton.Margin = new Padding(0, 0, Palette.SpacingSm, 0);
    FlowLayoutPanel buttons = new()
    {
      FlowDirection = FlowDirection.RightToLeft,
      AutoSize = true,
      Dock = DockStyle.Top,
    };
    buttons.Controls.Add(deleteButton);
    buttons.Controls.Add(cancelButton);

    TableLayoutPanel layout = new()
    {
      ColumnCount = 1,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Dock = DockStyle.Fill,
    };
    layout.Controls.Add(message);
    layout.Controls.Add(buttons);
    dialog.Controls.Add(layout);
    dialog.CancelButton = cancelButton;
    return dialog.ShowDialog(owner);
  }
}
