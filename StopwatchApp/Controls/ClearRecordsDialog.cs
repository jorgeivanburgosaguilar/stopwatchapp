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
    // AGENTS.md §8.5/§11 — a modal Form does not inherit MainForm.Font, so the dialog needs its own
    // body font to match the rest of the app's 16px type scale; disposed with the `using` below.
    using Font bodyFont = Typography.CreateBodyFont();
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
      Font = bodyFont,
    };

    // AGENTS.md §7/§8.5 — 420 is a 96dpi design-pixel literal (same convention as
    // MainForm.FixedClientSize); scaled to the dialog's real device DPI so the wrap width stays a
    // comfortable two-line measure instead of narrowing at higher OS scaling.
    int messageMaxWidth = (int)Math.Ceiling(420 * (dialog.DeviceDpi / 96f));
    Label message = new()
    {
      Text = "Are you sure you want to clear all records? This action cannot be undone.",
      AutoSize = true,
      MaximumSize = new Size(messageMaxWidth, 0),
      Margin = new Padding(0, 0, 0, Palette.SpacingLg),
      Dock = DockStyle.Top,
    };

    // AGENTS.md §8.5 — both buttons are GlyphButton instances (text-only, glyph: null), the same
    // rounded, palette-driven paint as the main window's transport and header-row buttons: "Clear
    // All" red (matching RecordsListControl's "Clear All Records") and "Cancel" dark slate
    // (Palette.CancelButton) — a non-destructive action must not read the same as the destructive
    // one it sits beside. Explicit equal-looking margins (0 vs. right-SpacingSm) keep both on the
    // same baseline in the FlowLayoutPanel below, instead of the mismatched default/explicit margins
    // that misaligned them before this change.
    bool dark = Application.IsDarkModeEnabled;
    GlyphButton clearAllButton = new("Clear All", glyph: null, Palette.StopButton)
    {
      DialogResult = DialogResult.Yes,
      Margin = new Padding(0),
      DarkMode = dark,
    };
    GlyphButton cancelButton = new("Cancel", glyph: null, Palette.CancelButton)
    {
      DialogResult = DialogResult.Cancel,
      Margin = new Padding(0, 0, Palette.SpacingSm, 0),
      DarkMode = dark,
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

    // AGENTS.md §8.5/§11 — GlyphButton owner-paints its own rounded corners (the same routine
    // StopwatchControl's transport buttons use), so the separate Region clip that this dialog used
    // for plain stock Buttons is no longer needed.
    //
    // The dialog's own outer corners are not rounded here: Windows 11 already rounds top-level
    // window frames at the DWM level by default, so AGENTS.md §11's DialogCornerRadius token needs
    // no extra rendering — see AGENTS.md §11.
    return dialog.ShowDialog(owner);
  }
}
