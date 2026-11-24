using System.Runtime.InteropServices;
using Microsoft.Win32;
using StopwatchApp.Models;
using StopwatchApp.Services;

namespace StopwatchApp.Tests;

public sealed class AwayMonitorTests
{
  private const int PbtPowerSettingChange = 0x8013;
  private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

  [Theory]
  [InlineData(SessionSwitchReason.SessionLock, AwayMonitor.Signal.Away)]
  [InlineData(SessionSwitchReason.ConsoleDisconnect, AwayMonitor.Signal.Away)]
  [InlineData(SessionSwitchReason.SessionUnlock, AwayMonitor.Signal.Return)]
  [InlineData(SessionSwitchReason.ConsoleConnect, AwayMonitor.Signal.Return)]
  [InlineData(SessionSwitchReason.SessionLogon, AwayMonitor.Signal.None)]
  [InlineData(SessionSwitchReason.SessionLogoff, AwayMonitor.Signal.None)]
  [InlineData(SessionSwitchReason.RemoteConnect, AwayMonitor.Signal.None)]
  public void Classify_MapsSessionSwitchReasons(
    SessionSwitchReason reason,
    AwayMonitor.Signal expected
  ) => Assert.Equal(expected, AwayMonitor.Classify(reason));

  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(2)]
  public void ReadDisplayState_ReadsTheDisplayValue(int state)
  {
    using PowerSetting setting = new(ConsoleDisplayState, state);

    Assert.Equal(state, AwayMonitor.ReadDisplayState(PbtPowerSettingChange, setting.Pointer));
  }

  [Fact]
  public void ReadDisplayState_IgnoresOtherPowerSettings()
  {
    using PowerSetting setting = new(Guid.NewGuid(), 0);

    Assert.Null(AwayMonitor.ReadDisplayState(PbtPowerSettingChange, setting.Pointer));
  }

  [Fact]
  public void ReadDisplayState_IgnoresOtherPowerBroadcastEvents()
  {
    using PowerSetting setting = new(ConsoleDisplayState, 0);

    Assert.Null(AwayMonitor.ReadDisplayState(0x0004, setting.Pointer));
  }

  [Fact]
  public void ReadDisplayState_IgnoresAnEmptyPayload()
  {
    using PowerSetting setting = new(ConsoleDisplayState, 0, dataLength: 0);

    Assert.Null(AwayMonitor.ReadDisplayState(PbtPowerSettingChange, setting.Pointer));
  }

  [Fact]
  public void ReadDisplayState_IgnoresANullPointer() =>
    Assert.Null(AwayMonitor.ReadDisplayState(PbtPowerSettingChange, IntPtr.Zero));

  [Fact]
  public void HandleMessage_DisplayOffReportsAway()
  {
    List<AwayReason> away = [];
    int returns = 0;
    using AwayMonitor monitor = new(away.Add, () => returns++);
    using PowerSetting setting = new(ConsoleDisplayState, 0);

    monitor.HandleMessage(PbtPowerSettingChange, setting.Pointer);

    Assert.Equal([AwayReason.DisplayOff], away);
    Assert.Equal(0, returns);
  }

  [Fact]
  public void HandleMessage_DisplayOnReportsReturnAndDimmedIsIgnored()
  {
    List<AwayReason> away = [];
    int returns = 0;
    using AwayMonitor monitor = new(away.Add, () => returns++);
    using PowerSetting on = new(ConsoleDisplayState, 1);
    using PowerSetting dimmed = new(ConsoleDisplayState, 2);

    monitor.HandleMessage(PbtPowerSettingChange, dimmed.Pointer);
    monitor.HandleMessage(PbtPowerSettingChange, on.Pointer);

    Assert.Empty(away);
    Assert.Equal(1, returns);
  }

  // A POWERBROADCAST_SETTING in unmanaged memory: GUID, DWORD DataLength, then the data.
  private sealed class PowerSetting : IDisposable
  {
    public PowerSetting(Guid setting, int state, int dataLength = sizeof(int))
    {
      Pointer = Marshal.AllocHGlobal(24);
      Marshal.Copy(setting.ToByteArray(), 0, Pointer, 16);
      Marshal.WriteInt32(Pointer, 16, dataLength);
      Marshal.WriteInt32(Pointer, 20, state);
    }

    public IntPtr Pointer { get; }

    public void Dispose() => Marshal.FreeHGlobal(Pointer);
  }
}
