using System.Diagnostics;
using System.Runtime.InteropServices;
using StopwatchApp.Services;

namespace StopwatchApp;

/// <summary>
/// Application entry point and single-instance enforcement (AGENTS.md §10.5).
/// </summary>
internal static partial class Program
{
  // GUID-qualified so this app's mutex/message never collides with an unrelated app's (AGENTS.md
  // §10.5).
  private const string MutexName =
    "StopwatchApp-SingleInstance-9F1B6E2A-3C4D-4E5F-8A6B-1C2D3E4F5A6B";

  /// <summary>
  /// The registered window message a second instance posts to ask the first instance to restore
  /// and activate its window; <see cref="MainForm"/> listens for it in its <c>WndProc</c>
  /// override.
  /// </summary>
  internal const string ActivateMessageName =
    "StopwatchApp-Activate-9F1B6E2A-3C4D-4E5F-8A6B-1C2D3E4F5A6B";

  /// <summary>
  /// The main entry point for the application.
  /// </summary>
  [STAThread]
  private static void Main()
  {
    using Mutex instanceMutex = new(initiallyOwned: false, name: MutexName, out bool createdNew);
    if (!createdNew)
    {
      // Another instance already holds the mutex: find its window and ask it to restore/activate,
      // then exit immediately without ever showing a second window (AGENTS.md §10.5 — a broadcast
      // to HWND_BROADCAST does not reach the hidden-to-tray window, since ShowInTaskbar = false
      // gives it an owner and Windows excludes owned windows from HWND_BROADCAST delivery
      // regardless of visibility; a direct per-window lookup is not subject to that exclusion).
      IntPtr targetWindow = FindRunningInstanceWindow();
      if (targetWindow != IntPtr.Zero)
      {
        uint activateMessage = RegisterWindowMessage(ActivateMessageName);
        PostMessage(targetWindow, activateMessage, IntPtr.Zero, IntPtr.Zero);
      }

      return;
    }

    ApplicationConfiguration.Initialize();
    Application.SetColorMode(SystemColorMode.System);
    AppSettings settings = AppSettings.LoadDefault();
    Application.Run(new MainForm(settings));
  }

  /// <summary>
  /// Finds the main window of the already-running instance, which may be a different build (its
  /// title carries the version) and may be hidden to the tray. Walks the top-level windows and
  /// accepts one whose title starts with <see cref="MainForm.WindowTitlePrefix"/> and whose owning
  /// process has this process's name, so an unrelated window that merely starts with the same word
  /// is never activated.
  /// </summary>
  private static unsafe IntPtr FindRunningInstanceWindow()
  {
    string processName = Process.GetCurrentProcess().ProcessName;
    int ownProcessId = Environment.ProcessId;
    const int titleCapacity = 256;
    char* title = stackalloc char[titleCapacity];

    IntPtr window = IntPtr.Zero;
    while ((window = FindWindowEx(IntPtr.Zero, window, null, null)) != IntPtr.Zero)
    {
      int length = GetWindowText(window, title, titleCapacity);
      if (
        length <= 0
        || !new ReadOnlySpan<char>(title, length).StartsWith(
          MainForm.WindowTitlePrefix,
          StringComparison.Ordinal
        )
      )
      {
        continue;
      }

      _ = GetWindowThreadProcessId(window, out uint processId);
      if (processId != ownProcessId && IsProcessNamed((int)processId, processName))
      {
        return window;
      }
    }

    return IntPtr.Zero;
  }

  private static bool IsProcessNamed(int processId, string processName)
  {
    try
    {
      using Process process = Process.GetProcessById(processId);
      return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
    }
    catch (ArgumentException)
    {
      // The process exited between enumeration and lookup.
      return false;
    }
  }

  [LibraryImport(
    "user32.dll",
    EntryPoint = "RegisterWindowMessageW",
    StringMarshalling = StringMarshalling.Utf16
  )]
  internal static partial uint RegisterWindowMessage(string message);

  [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

  [LibraryImport(
    "user32.dll",
    EntryPoint = "FindWindowExW",
    StringMarshalling = StringMarshalling.Utf16
  )]
  private static partial IntPtr FindWindowEx(
    IntPtr parentWindow,
    IntPtr childAfter,
    string? className,
    string? windowName
  );

  [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
  private static unsafe partial int GetWindowText(IntPtr window, char* text, int maxCount);

  [LibraryImport("user32.dll", SetLastError = true)]
  private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
