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
      // Another instance already holds the mutex: find its window by title and ask it to
      // restore/activate, then exit immediately without ever showing a second window (AGENTS.md
      // AGENTS.md §10.5 — a broadcast to HWND_BROADCAST does not reach the hidden-to-tray window, since
      // ShowInTaskbar = false gives it an owner and Windows excludes owned windows from
      // HWND_BROADCAST delivery regardless of visibility; a direct FindWindow lookup is not
      // subject to that exclusion).
      IntPtr targetWindow = FindWindow(null, MainForm.WindowTitle);
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
    EntryPoint = "FindWindowW",
    StringMarshalling = StringMarshalling.Utf16
  )]
  private static partial IntPtr FindWindow(string? className, string windowName);
}
