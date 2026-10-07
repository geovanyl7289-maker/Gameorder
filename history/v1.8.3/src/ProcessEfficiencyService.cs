using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gameorder;

[StructLayout(LayoutKind.Sequential)]
public struct OwnProcessPowerState : IEquatable<OwnProcessPowerState>
{
    public uint Version,ControlMask,StateMask;
    public OwnProcessPowerState(uint version,uint controlMask,uint stateMask)
    { Version=version; ControlMask=controlMask; StateMask=stateMask; }
    public readonly bool Equals(OwnProcessPowerState other)=>Version==other.Version&&ControlMask==other.ControlMask&&StateMask==other.StateMask;
}
public sealed record ProcessEfficiencySnapshot(bool Requested,bool Applied,bool Supported,bool RestorePending,int LastError,string Hint,
    bool EcoQoSApplied=false,bool PriorityLowered=false);

/// <summary>Only operations on the calling process; there is no PID/handle parameter for external processes.</summary>
public interface IOwnProcessEfficiencyPlatform
{
    bool TryReadPowerState(out OwnProcessPowerState state,out int error);
    bool TryWritePowerState(OwnProcessPowerState state,out int error);
    bool TryReadPriority(out ProcessPriorityClass priority,out int error);
    bool TryWritePriority(ProcessPriorityClass priority,out int error);
}

/// <summary>Reversible own-process background scheduling. It never trims memory or changes timer resolution.</summary>
public sealed class ProcessEfficiencyService(IOwnProcessEfficiencyPlatform? platform=null,Func<DateTimeOffset>? utcNow=null) : IDisposable
{
    private const uint ExecutionSpeed=1;
    private readonly IOwnProcessEfficiencyPlatform _platform=platform??new WindowsOwnProcessEfficiencyPlatform();
    private readonly Func<DateTimeOffset> _now=utcNow??(()=>DateTimeOffset.UtcNow);
    private readonly object _gate=new();
    private OwnProcessPowerState? _powerBefore;
    private ProcessPriorityClass? _priorityBefore;
    private bool _requested,_ecoApplied,_priorityLowered,_powerConfigured,_priorityConfigured,_unsupported,_disposed;
    private int _lastError;
    private DateTimeOffset _nextAttempt;
    public ProcessEfficiencySnapshot Snapshot { get { lock(_gate) return CreateSnapshot(); } }

    public ProcessEfficiencySnapshot SetBackgroundEfficiency(bool background)
    {
        lock(_gate)
        {
            if(_disposed) return CreateSnapshot();
            if(_requested!=background) { _requested=background; _nextAttempt=default; }
            if(_now()<_nextAttempt) return CreateSnapshot();
            _lastError=0;
            if(background) Apply(); else Restore();
            _nextAttempt=_now().AddSeconds(_lastError==0?0:30);
            return CreateSnapshot();
        }
    }
    private void Apply()
    {
        if(!_powerConfigured&&!_unsupported)
        {
            if(_platform.TryReadPowerState(out var original,out int error))
            {
                var eco=new OwnProcessPowerState(1,original.ControlMask|ExecutionSpeed,original.StateMask|ExecutionSpeed);
                if(original.Equals(eco)||_platform.TryWritePowerState(eco,out error))
                {
                    if(!original.Equals(eco)) _powerBefore??=original;
                    _ecoApplied=true; _powerConfigured=true;
                }
                else Error(error);
            }
            else Error(error);
        }
        if(!_priorityConfigured)
        {
            if(_platform.TryReadPriority(out var original,out int error))
            {
                if(original is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal)
                { _priorityLowered=true; _priorityConfigured=true; }
                else if(_platform.TryWritePriority(ProcessPriorityClass.BelowNormal,out error))
                { _priorityBefore??=original; _priorityLowered=true; _priorityConfigured=true; }
                else Error(error,false);
            }
            else Error(error,false);
        }
    }
    private void Restore()
    {
        if(_powerBefore is { } original)
        {
            if(_platform.TryReadPowerState(out var current,out int error))
            {
                // Restore only the execution-speed bit we owned. Preserve other policies changed meanwhile.
                var restored=new OwnProcessPowerState(1,(current.ControlMask&~ExecutionSpeed)|(original.ControlMask&ExecutionSpeed),
                    (current.StateMask&~ExecutionSpeed)|(original.StateMask&ExecutionSpeed));
                if(current.Equals(restored)||_platform.TryWritePowerState(restored,out error))
                { _powerBefore=null; _ecoApplied=false; _powerConfigured=false; }
                else Error(error);
            }
            else Error(error);
        }
        else { _ecoApplied=false; _powerConfigured=false; }
        if(_priorityBefore is { } priority)
        {
            if(_platform.TryWritePriority(priority,out int error))
            { _priorityBefore=null; _priorityLowered=false; _priorityConfigured=false; }
            else Error(error,false);
        }
        else { _priorityLowered=false; _priorityConfigured=false; }
    }
    private void Error(int error,bool power=true)
    {
        _lastError=error==0?1:error;
        if(power&&error is 50 or 87 or 120 or 127) _unsupported=true;
    }
    private ProcessEfficiencySnapshot CreateSnapshot()
    {
        bool pending=!_requested&&(_powerBefore.HasValue||_priorityBefore.HasValue);
        string hint=pending?"后台效率策略等待恢复":_requested?(_ecoApplied?"后台效率模式":"后台轻量模式"):
            "前台交互模式";
        return new(_requested,_requested&&(_ecoApplied||_priorityLowered),!_unsupported,pending,_lastError,hint,_ecoApplied,_priorityLowered);
    }
    public void Dispose()
    {
        lock(_gate)
        {
            if(_disposed) return;
            _requested=false; _lastError=0;
            Restore();
            _disposed=true;
        }
    }
}

public sealed class WindowsOwnProcessEfficiencyPlatform : IOwnProcessEfficiencyPlatform
{
    private const int ProcessPowerThrottling=4;
    public bool TryReadPowerState(out OwnProcessPowerState state,out int error)
    {
        state=new(1,0,0); error=0;
        if(!OperatingSystem.IsWindows()) { error=50; return false; }
        try
        {
            if(GetProcessInformation(GetCurrentProcess(),ProcessPowerThrottling,ref state,12)) return true;
            error=Marshal.GetLastWin32Error(); return false;
        }
        catch(Exception ex) when(ex is EntryPointNotFoundException or DllNotFoundException) { error=127; return false; }
    }
    public bool TryWritePowerState(OwnProcessPowerState state,out int error)
    {
        error=0;
        if(!OperatingSystem.IsWindows()) { error=50; return false; }
        try
        {
            if(SetProcessInformation(GetCurrentProcess(),ProcessPowerThrottling,ref state,12)) return true;
            error=Marshal.GetLastWin32Error(); return false;
        }
        catch(Exception ex) when(ex is EntryPointNotFoundException or DllNotFoundException) { error=127; return false; }
    }
    public bool TryReadPriority(out ProcessPriorityClass priority,out int error)
    {
        priority=ProcessPriorityClass.Normal; error=0;
        try { using var own=Process.GetCurrentProcess(); priority=own.PriorityClass; return true; }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException)
        { error=ex is Win32Exception native?native.NativeErrorCode:50; return false; }
    }
    public bool TryWritePriority(ProcessPriorityClass priority,out int error)
    {
        error=0;
        try { using var own=Process.GetCurrentProcess(); own.PriorityClass=priority; return true; }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException)
        { error=ex is Win32Exception native?native.NativeErrorCode:50; return false; }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessInformation(IntPtr process,int informationClass,ref OwnProcessPowerState state,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetProcessInformation(IntPtr process,int informationClass,ref OwnProcessPowerState state,uint size);
}