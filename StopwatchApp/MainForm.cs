namespace StopwatchApp;

/// <summary>
/// The application's main window. A thin orchestrator that wires controls and services together;
/// it contains no business logic of its own (see AGENTS.md §3).
/// </summary>
public sealed class MainForm : Form
{
  /// <summary>
  /// Initializes a new instance of the <see cref="MainForm"/> class.
  /// </summary>
  public MainForm()
  {
    Text = "Stopwatch";
    ClientSize = new Size(400, 300);
    StartPosition = FormStartPosition.CenterScreen;
  }
}
