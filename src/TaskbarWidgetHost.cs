using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Gameorder;

public sealed record TaskbarWidgetSnapshot(bool Embedded,string Status,long ParentHandle,long WidgetHandle,string ParentClass,
    bool NativeChild,bool ParentVerified,bool WithinTaskbar,bool TransparentChild,Rect TaskbarBoundsPixels,Rect BoundsPixels,
    double DpiScale,double WidgetWidthDip,int OccupiedCount,int LayoutRefreshCount,string[] ProtectedControls,
    bool HitTestOwnWindow,long HitWindowHandle,bool DpiContextMatched,uint ParentDpi,uint ChildDpi,bool CreationThreadContextRestored,
    int SourceGeneration,int DestroyedOwnSourceCount,bool DoesNotOverlapProtectedControls,bool ProtectedLayoutCurrent,
    bool Enabled,string DockPosition,bool IsDragging,bool DropPending,string? PreviewDockPosition,TaskbarDockSlot[] DockSlots,
    bool HintVisible,long HintHandle,Rect HintBoundsPixels,bool HintMatchesWidgetSize,bool HintInputTransparent,bool HintNoActivate,bool HintNoTaskbarButton,
    int SuccessfulDropCount,int CanceledDragCount,bool TemporarilySuspended=false,bool WatchdogRunning=false,double WatchdogIntervalSeconds=0,int AutomationProbeCount=0,bool AutomationWatcherActive=false,
    int NativeMoveCount=0,int NativeShowCount=0,int NativeHideCount=0,int WindowPositionMessageCount=0,int VisibilityMessageCount=0);
public sealed record TaskbarWidgetChooserPlacement(Rect BoundsDip,Rect BoundsPixels,Rect WorkAreaPixels,double DpiScale);

/// <summary>Owns a transparent child HWND inside Explorer's existing taskbar. It never changes Explorer's layout.</summary>
public sealed class TaskbarWidgetHost : IDisposable
{
    private const int WsChild=0x40000000,WsVisible=0x10000000,WsClipSiblings=0x04000000,WsClipChildren=0x02000000;
    private const int ExNoActivate=0x08000000,WmMouseActivate=0x21,WmDpiChanged=0x02E0,WmDisplayChange=0x7E;
    private static readonly TimeSpan ProtectedLayoutLifetime=TimeSpan.FromSeconds(3.5);
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _dragWatchdog;
    private readonly Action _openChooser,_openManager;
    private readonly Action<SteamAccount,SwitchMode> _requestSwitch;
    private readonly HashSet<HwndSource> _watchedSources=[];
    private readonly uint _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    private readonly int _ownProcess=Environment.ProcessId;
    private int _automationLayoutVersion,_probeLayoutVersion,_protectedLayoutVersion,_automationProbeCount,_refreshQueued;
    private Rect[] _nativeProtectedBounds=[];
    private HwndSource? _source;
    private IntPtr _parent;
    private uint _parentProcess;
    private Task<Probe>? _pendingProbe;
    private Task<TaskbarWidgetSnapshot>? _activeRefresh;
    private IntPtr _probeParent;
    private Rect _probeBounds;
    private DateTimeOffset _probeStarted;
    private DateTimeOffset _minimumProbeStarted;
    private bool _disposed,_refreshing;
    private bool _enabled=true,_temporarilySuspended,_dragging,_dropPending,_canceling;
    private int _visibilityGeneration;
    private TaskbarDockPosition _dockPosition=TaskbarDockPosition.Tray,_dragOriginalDock;
    private TaskbarDockSlots _dockSlots=new(null,null);
    private TaskbarDockSlot? _previewSlot;
    private TaskbarDockHint? _hint;
    private Point _dragPointer;
    private double _grabFractionX,_grabFractionY,_dragScale;
    private Rect _dragOriginalBounds=Rect.Empty,_dragOriginalBar=Rect.Empty;
    private IntPtr _dragParent;
    private int _dragToken,_dragSourceGeneration,_successfulDrops,_canceledDrags;
    private int _refreshCount;
    private int _sourceGeneration,_destroyedOwnSourceCount;
    private int _nativeMoveCount,_nativeShowCount,_nativeHideCount,_windowPositionMessageCount,_visibilityMessageCount;
    private bool _creationContextRestored;
    private Rect _barBounds=Rect.Empty,_bounds=Rect.Empty;
    private double _scale=1;
    private int _occupiedCount;
    private string[] _protectedNames=[];
    private Rect[] _protectedBounds=[];
    private DateTimeOffset _protectedLayoutRead;
    private IntPtr _protectedParent;
    private Rect _protectedBarBounds=Rect.Empty;
    private double _protectedScale=1;
    private string _status="正在查找任务栏空位";
    private readonly List<Window> _watchedWindows=[];
    public TaskbarAccountWidget WidgetForVerification { get; }
    public event EventHandler? PlacementChanged;
    public event EventHandler<TaskbarDockPositionCommittedEventArgs>? DockPositionCommitted;
    public bool Enabled=>_enabled;
    public bool TemporarilySuspended=>_temporarilySuspended;
    public bool WatchdogRunning=>_watchdog.IsEnabled;
    public double WatchdogIntervalSeconds=>_watchdog.Interval.TotalSeconds;
    public int AutomationProbeCount=>Volatile.Read(ref _automationProbeCount);
    public bool AutomationWatcherActive=>false;
    private bool CanObserveAutomation=>!Volatile.Read(ref _disposed)&&Volatile.Read(ref _enabled)&&!Volatile.Read(ref _temporarilySuspended);
    private bool CanShow=>_enabled&&!_temporarilySuspended&&!_disposed;
    private string InactiveStatus=>!_enabled?"任务栏控件已关闭":"游戏运行中，暂时隐藏";
    public TaskbarDockPosition DockPosition=>_dockPosition;
    public TaskbarDockSlots DockSlots=>_dockSlots;
    public bool IsDragging=>_dragging;
    public bool DropPending=>_dropPending;
    public bool IsEmbedded=>CanShow&&_source is not null&&!_source.IsDisposed&&IsWindow(_source.Handle)&&IsWindow(_parent)
        &&GetParent(_source.Handle)==_parent&&IsChild(_parent,_source.Handle)&&IsWindowVisible(_source.Handle);
    public string Status=>_status;
    public Rect NativeBounds=>_bounds;
    public Rect BoundsPixels=>_bounds;
    public Rect TaskbarBoundsPixels=>_barBounds;
    public IntPtr NativeHandle=>_source?.Handle??IntPtr.Zero;
    public IntPtr ParentHandle=>_parent;
    public TaskbarWidgetSnapshot Snapshot=>GetSnapshot();

    public TaskbarWidgetHost(MainViewModel viewModel,Action openChooser,Action<SteamAccount,SwitchMode> requestSwitch,Action openManager)
    {
        _dispatcher=Dispatcher.CurrentDispatcher;
        _openChooser=openChooser; _requestSwitch=requestSwitch; _openManager=openManager;
        WidgetForVerification=new TaskbarAccountWidget(viewModel);
        WidgetForVerification.OpenAccountsRequested+=OpenAccounts;
        WidgetForVerification.OpenManagerRequested+=OpenManager;
        WidgetForVerification.SwitchRequested+=SwitchAccount;
        WidgetForVerification.DragStarted+=DragStarted;
        WidgetForVerification.DragMoved+=DragMoved;
        WidgetForVerification.DragCompleted+=DragCompleted;
        WidgetForVerification.DragCanceled+=DragCanceled;
        _watchdog=new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromSeconds(3) };
        _watchdog.Tick+=WatchdogTick;
        _watchdog.Start();
        _dragWatchdog=new DispatcherTimer(DispatcherPriority.Input) { Interval=TimeSpan.FromMilliseconds(40) };
        _dragWatchdog.Tick+=CheckDragState;
    }

    private void OpenAccounts(object? sender,EventArgs e) { if(IsEmbedded&&!_dragging&&!_dropPending) _openChooser(); }
    private void OpenManager(object? sender,EventArgs e) { if(IsEmbedded&&!_dragging&&!_dropPending) _openManager(); }
    private void SwitchAccount(object? sender,TaskbarAccountSwitchEventArgs e)
    { if(IsEmbedded&&!_dragging&&!_dropPending) _requestSwitch(e.Account,e.Mode); }
    private void DragStarted(object? sender,TaskbarWidgetDragEventArgs e)
    { if(BeginDrag(e.StartScreenPixels)) UpdateDrag(e.ScreenPixels); else WidgetForVerification.CancelDragGesture(); }
    private void DragMoved(object? sender,TaskbarWidgetDragEventArgs e)=>UpdateDrag(e.ScreenPixels);
    private async void DragCompleted(object? sender,TaskbarWidgetDragEventArgs e)
    { try { await EndDragAsync(e.ScreenPixels); } catch(Exception ex) { CancelDrag(); _status="停靠未完成："+ex.Message; } }
    private void DragCanceled(object? sender,EventArgs e)=>CancelDrag();

    public void SetEnabled(bool enabled)
    {
        if(!_dispatcher.CheckAccess()) { _dispatcher.Invoke(()=>SetEnabled(enabled)); return; }
        if(_disposed) return;
        bool changed=_enabled!=enabled; _enabled=enabled;
        if(changed) _visibilityGeneration++;
        if(enabled&&changed) _minimumProbeStarted=DateTimeOffset.UtcNow;
        UpdateWatchdog();
        WidgetForVerification.EnableInteraction(CanShow&&!_dropPending);
        if(!CanShow) { CancelDrag(); HideOwnWindow(InactiveStatus); }
        else QueueRefresh();
    }
    /// <summary>Temporarily hides this host without changing the user's enabled or dock preferences.</summary>
    public void SetTemporarilySuspended(bool suspended)
    {
        if(!_dispatcher.CheckAccess()) { _dispatcher.Invoke(()=>SetTemporarilySuspended(suspended)); return; }
        if(_disposed||_temporarilySuspended==suspended) return;
        _temporarilySuspended=suspended; _visibilityGeneration++;
        // Samples read before a pause/resume cannot authorize showing a child after the transition.
        _minimumProbeStarted=DateTimeOffset.UtcNow;
        UpdateWatchdog();
        WidgetForVerification.EnableInteraction(CanShow&&!_dropPending);
        if(!CanShow)
        {
            CancelDrag(); HideOwnWindow(InactiveStatus);
            // Detach the retained control from its compositor/HWND while a game is running.
            // Its view model and preferences survive; resume requires a fresh guarded source creation.
            DestroyOwnSource(); _hint?.Dispose(); _hint=null;
        }
        else { _status="正在恢复任务栏控件"; QueueRefresh(); }
    }
    private void UpdateWatchdog()
    {
        if(CanShow) _watchdog.Start(); else _watchdog.Stop();
        if(!CanShow) _dragWatchdog.Stop();
    }
    public void SetDockPosition(TaskbarDockPosition position)
    {
        if(!_dispatcher.CheckAccess()) { _dispatcher.Invoke(()=>SetDockPosition(position)); return; }
        if(position is not TaskbarDockPosition.Tray and not TaskbarDockPosition.Left) throw new ArgumentOutOfRangeException(nameof(position));
        if(_disposed) return;
        if(position==_dockPosition&&!_dragging&&!_dropPending) return;
        CancelDrag(); _dockPosition=position; _minimumProbeStarted=DateTimeOffset.UtcNow;
        if(CanShow) { HideOwnWindow("正在确认停靠位置"); QueueRefresh(); }
    }
    private void QueueRefresh()
    {
        if(!CanShow) return;
        if(Interlocked.Exchange(ref _refreshQueued,1)!=0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>
        {
            Interlocked.Exchange(ref _refreshQueued,0);
            if(!CanShow) return;
            try { await RefreshLayoutAsync(); } catch(Exception ex) { HideOwnWindow("任务栏控件暂不可用："+ex.Message); }
        }));
    }

    public bool BeginDrag(Point screenPixels)
    {
        if(!_dispatcher.CheckAccess()) return _dispatcher.Invoke(()=>BeginDrag(screenPixels));
        if(!CanShow||_dragging||_dropPending||!IsEmbedded||!ProtectedLayoutCurrent
            ||!Finite(screenPixels)||_bounds.IsEmpty||!_bounds.Contains(screenPixels)) return false;
        _dragOriginalBounds=_bounds; _dragOriginalBar=_barBounds; _dragOriginalDock=_dockPosition;
        _dragParent=_parent; _dragSourceGeneration=_sourceGeneration; _dragScale=_scale;
        _grabFractionX=Math.Clamp((screenPixels.X-_bounds.Left)/_bounds.Width,0,1);
        _grabFractionY=Math.Clamp((screenPixels.Y-_bounds.Top)/_bounds.Height,0,1);
        _dragPointer=screenPixels; _dragging=true; _dropPending=false; _dragToken++;
        _dragWatchdog.Start();
        QueueRefresh(); // Refresh safety bounds while moving; pointer feedback remains on the 40 ms watchdog.
        _previewSlot=null; _status="拖动到停靠位置";
        return true;
    }
    public void UpdateDrag(Point screenPixels)
    {
        if(!_dispatcher.CheckAccess()) { _dispatcher.Invoke(()=>UpdateDrag(screenPixels)); return; }
        if(!CanShow||!_dragging||_dropPending) return;
        if(!Finite(screenPixels)||!DragContextCurrent()) { CancelDrag(); return; }
        _dragPointer=screenPixels;
        var target=ProtectedLayoutCurrent?FindNearbySlot(screenPixels):null;
        var size=target?.BoundsPixels.Size??_dragOriginalBounds.Size;
        double x=Math.Clamp(screenPixels.X-_grabFractionX*size.Width,_barBounds.Left+_scale,
            Math.Max(_barBounds.Left+_scale,_barBounds.Right-size.Width-_scale));
        var moving=new Rect(x,_barBounds.Top+(_barBounds.Height-size.Height)/2,size.Width,size.Height);
        try
        {
            MoveDraggingSource(moving);
            _previewSlot=target;
            if(target is null) _hint?.Hide();
            else { _hint??=new TaskbarDockHint(); _hint.ShowAt(target.BoundsPixels,_scale); }
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or ArgumentException)
        { CancelDrag(); HideOwnWindow("拖动暂不可用："+ex.Message); }
    }
    public async Task<bool> EndDragAsync(Point screenPixels)
    {
        if(!_dispatcher.CheckAccess()) return await _dispatcher.InvokeAsync(()=>EndDragAsync(screenPixels)).Task.Unwrap();
        if(!CanShow||!_dragging||_dropPending) return false;
        UpdateDrag(screenPixels);
        if(!_dragging||_previewSlot is null) { CancelDrag(); return false; }
        int token=_dragToken;
        var expected=_previewSlot;
        DateTimeOffset dropRequested=DateTimeOffset.UtcNow;
        _dropPending=true;
        WidgetForVerification.EnableInteraction(false);
        try
        {
            // Finish any older single provider read, then require a sample started after release.
            for(int attempt=0;attempt<3&&_dragging&&_dragToken==token;attempt++)
            {
                await RefreshLayoutAsync();
                if(ProtectedLayoutCurrent&&_protectedLayoutRead>=dropRequested) break;
            }
            if(!_dragging||_dragToken!=token||!CanShow||!DragContextCurrent()||!ProtectedLayoutCurrent||_protectedLayoutRead<dropRequested)
            { CancelDrag(); return false; }
            var target=FindNearbySlot(screenPixels);
            // A target whose size changed after mouse release was never previewed; return safely.
            if(target is null||target.Position!=expected.Position||!SameSize(target.BoundsPixels,expected.BoundsPixels))
            { CancelDrag(); return false; }
            _hint?.Hide();
            MoveOwnSource(target.BoundsPixels);
            if(!IsEmbedded||!DoesNotOverlapProtectedControls(_bounds)||!_barBounds.Contains(_bounds))
            { CancelDrag(); return false; }
            _dockPosition=target.Position; _dragging=false; _dropPending=false; _previewSlot=null;
            _dragWatchdog.Stop();
            _successfulDrops++; _status="已嵌入 Windows 任务栏";
            WidgetForVerification.CancelDragGesture();
            DockPositionCommitted?.Invoke(this,new(target.Position));
            return true;
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or COMException or ArgumentException)
        { CancelDrag(); _status="停靠未完成："+ex.Message; return false; }
        finally { if(!_disposed) WidgetForVerification.EnableInteraction(CanShow); }
    }
    public void CancelDrag()
    {
        if(!_dispatcher.CheckAccess()) { _dispatcher.Invoke(CancelDrag); return; }
        if(_canceling) return;
        _canceling=true;
        bool active=_dragging||_dropPending;
        _dragging=false; _dropPending=false; _dragToken++; _previewSlot=null; _hint?.Hide();
        _dragWatchdog.Stop();
        try
        {
            WidgetForVerification.CancelDragGesture();
            if(!_disposed) WidgetForVerification.EnableInteraction(CanShow);
            if(!active) return;
            _canceledDrags++; _dockPosition=_dragOriginalDock;
            var original=FindAvailableDockSlot(_dragOriginalDock);
            if(CanShow&&DragContextCurrent()&&ProtectedLayoutCurrent&&original is not null)
                MoveOwnSource(original.BoundsPixels);
            else if(_source is not null&&!_source.IsDisposed&&IsWindow(_source.Handle)) ShowWindow(_source.Handle,0);
            _status=IsEmbedded&&original is not null?EmbeddedStatus(original.Position):"正在重新确认停靠空位";
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or ArgumentException)
        { if(_source is not null&&!_source.IsDisposed&&IsWindow(_source.Handle)) ShowWindow(_source.Handle,0); _status="正在重新确认停靠空位"; }
        finally { _canceling=false; }
        if(active&&CanShow) QueueRefresh();
    }
    private void CheckDragState(object? sender,EventArgs e)
    {
        if(!CanShow||!_dragging) { _dragWatchdog.Stop(); return; }
        if((GetAsyncKeyState(0x1B)&0x8000)!=0||!DragContextCurrent()) CancelDrag();
    }
    private bool DragContextCurrent()
    {
        if(!CanShow||_source is null||_source.IsDisposed||!IsWindow(_source.Handle)||!IsWindow(_dragParent)
            ||_parent!=_dragParent||_sourceGeneration!=_dragSourceGeneration||GetParent(_source.Handle)!=_dragParent
            ||FindWindow("Shell_TrayWnd",null)!=_dragParent||!GetWindowRect(_dragParent,out var rect)||rect.ToRect()!=_dragOriginalBar) return false;
        GetWindowThreadProcessId(_dragParent,out uint process);
        if(process!=_parentProcess) return false;
        uint dpi=GetDpiForWindow(_dragParent); double scale=dpi>0?dpi/96d:1;
        return Math.Abs(scale-_dragScale)<.005;
    }
    private TaskbarDockSlot? FindAvailableDockSlot(TaskbarDockPosition preferred)
        =>_dockSlots.Get(preferred)??_dockSlots.Available.FirstOrDefault();
    private string EmbeddedStatus(TaskbarDockPosition actual)
        =>actual==_dockPosition?"已嵌入 Windows 任务栏":_dockPosition==TaskbarDockPosition.Left
            ?"最左侧空位不足，暂用托盘旁":"托盘旁空位不足，暂用最左侧";
    private TaskbarDockSlot? FindNearbySlot(Point pointer)
    {
        var slots=_dockSlots.Available.ToArray();
        Point GrabPoint(TaskbarDockSlot slot)=>new(slot.BoundsPixels.Left+_grabFractionX*slot.BoundsPixels.Width,
            slot.BoundsPixels.Top+_grabFractionY*slot.BoundsPixels.Height);
        double radius=80*_scale;
        if(slots.Length==2) radius=Math.Min(radius,(GrabPoint(slots[0])-GrabPoint(slots[1])).Length/2);
        var distances=slots.Select(slot=>(Slot:slot,Distance:(GrabPoint(slot)-pointer).Length)).OrderBy(pair=>pair.Distance).ToArray();
        if(distances.Length==0||distances[0].Distance>radius) return null;
        if(distances.Length==2&&Math.Abs(distances[1].Distance-distances[0].Distance)<=_scale) return null;
        return distances[0].Slot;
    }
    private void MoveDraggingSource(Rect bounds)
    {
        if(!CanShow) return;
        if(_source is null||_source.IsDisposed) throw new InvalidOperationException("任务栏子窗口已失效。");
        WidgetForVerification.ConfigureSize(bounds.Width/_scale,bounds.Height/_scale);
        bool wasVisible=IsWindowVisible(_source.Handle);
        var point=new NativePoint { X=(int)Math.Round(bounds.X),Y=(int)Math.Round(bounds.Y) };
        if(!ScreenToClient(_parent,ref point)||!SetWindowPos(_source.Handle,IntPtr.Zero,point.X,point.Y,
            (int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height),0x0010|0x0040)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _nativeMoveCount++;
        if(!wasVisible) _nativeShowCount++;
        if(!GetWindowRect(_source.Handle,out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _bounds=rect.ToRect();
        if(!_barBounds.Contains(_bounds)) throw new InvalidOperationException("拖动位置超出任务栏。");
    }
    private static bool SameSize(Rect left,Rect right)=>Math.Abs(left.Width-right.Width)<=1&&Math.Abs(left.Height-right.Height)<=1;
    private static bool Finite(Point point)=>double.IsFinite(point.X)&&double.IsFinite(point.Y);
    public Rect? GetDockSlotBoundsForVerification(TaskbarDockPosition position)=>_dockSlots.Get(position)?.BoundsPixels;
    public bool SaveDockHintPreviewForVerification(string output)=>_hint?.SavePreview(output)??false;
    public static TaskbarDockSlots FindDockSlots(Rect taskbar,IReadOnlyCollection<Rect> occupied,Rect trayBounds,double scale,
        double desiredWidthDip=360,double minimumWidthDip=80,double desiredHeightDip=48)
        =>TaskbarWidgetDockLayout.FindSlots(taskbar,occupied,trayBounds,scale,desiredWidthDip,minimumWidthDip,desiredHeightDip);

    public void WatchWindow(Window window)
    {
        if(_watchedWindows.Contains(window)) return;
        _watchedWindows.Add(window); window.SourceInitialized+=WatchSourceInitialized;
        AttachMonitorSource(window);
    }
    private void WatchSourceInitialized(object? sender,EventArgs e) { if(sender is Window window) AttachMonitorSource(window); }
    private void AttachMonitorSource(Window window)
    {
        var source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if(source is not null&&_watchedSources.Add(source)) source.AddHook(ShellMonitorHook);
    }
    private IntPtr ShellMonitorHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(CanShow&&(message==_taskbarCreated||message==WmDisplayChange||message==0x001A))
        { InvalidateLayout(); }
        return IntPtr.Zero;
    }

    private async void WatchdogTick(object? sender,EventArgs e)
    {
        if(!CanShow) { UpdateWatchdog(); return; }
        if((_dragging||_dropPending)&&(GetAsyncKeyState(0x1B)&0x8000)!=0) CancelDrag();
        try { await RefreshLayoutAsync(); }
        catch(Exception ex) { HideOwnWindow("任务栏控件暂不可用："+ex.Message); }
    }

    public Task<TaskbarWidgetSnapshot> RefreshNativeLayoutForVerificationAsync()=>RefreshLayoutAsync();
    public Task<TaskbarWidgetSnapshot> RefreshNowForVerificationAsync()=>RefreshLayoutAsync();
    public Task<TaskbarWidgetSnapshot> RefreshLayoutAsync()
    {
        if(!_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(RefreshLayoutAsync).Task.Unwrap();
        if(!CanShow) { if(!_disposed) HideOwnWindow(InactiveStatus); return Task.FromResult(GetSnapshot()); }
        if(_activeRefresh is { IsCompleted:false }) return _activeRefresh;
        _activeRefresh=RefreshCoreAsync(); return _activeRefresh;
    }
    private async Task<TaskbarWidgetSnapshot> RefreshCoreAsync()
    {
        if(_disposed) return GetSnapshot();
        if(!CanShow) { HideOwnWindow(InactiveStatus); return GetSnapshot(); }
        if(_refreshing) return GetSnapshot();
        int visibilityGeneration=_visibilityGeneration;
        _refreshing=true;
        try
        {
            IntPtr parent=FindWindow("Shell_TrayWnd",null);
            if(parent==IntPtr.Zero||!GetWindowRect(parent,out var nativeBar)||!IsWindowVisible(parent))
            { CancelDrag(); DestroyOwnSource(); _status="正在等待 Windows 任务栏"; return GetSnapshot(); }
            GetWindowThreadProcessId(parent,out uint parentProcess);
            if(_source is not null&&(_source.IsDisposed||!IsWindow(_source.Handle)||GetParent(_source.Handle)!=parent||_parentProcess!=parentProcess))
            { CancelDrag(); DestroyOwnSource(); }
            _barBounds=nativeBar.ToRect();
            if(_barBounds.Width<=_barBounds.Height)
            { HideOwnWindow("当前竖向任务栏没有可容纳控件的横向空位"); return GetSnapshot(); }
            uint dpi=GetDpiForWindow(parent); _scale=dpi>0?dpi/96d:1;
            if(_dragging&&!DragContextCurrent()) CancelDrag();
            // Probe every 3 s without global UIA subscriptions. Explorer reports some child events on
            // its parent, so subscribing could mistake our own Show/Hide for a layout change.
            // Each provider read stays on one non-UI worker and only protects placement for a short TTL.
            var native=ReadNativeOccupied(parent,_barBounds);
            if(_source is not null&&(!ProtectedLayoutCurrent||!native.Occupied.SequenceEqual(_nativeProtectedBounds)))
                HideOwnWindow("正在确认任务栏布局");
            if(!_dragging&&!_dropPending&&_pendingProbe is null&&IsEmbedded&&ProtectedLayoutCurrent
                &&_source is not null&&GetWindowRect(_source.Handle,out var currentChild)&&currentChild.ToRect()==_bounds
                &&DoesNotOverlapProtectedControls(currentChild.ToRect())
                // Skipping this read is safe only when the cached placement survives the next tick.
                // A 3-second cache hit otherwise skips to 6 seconds and forces a visible hide/show.
                &&_protectedLayoutRead>=_minimumProbeStarted
                &&DateTimeOffset.UtcNow-_protectedLayoutRead+_watchdog.Interval<ProtectedLayoutLifetime
                &&native.Occupied.SequenceEqual(_nativeProtectedBounds)) return GetSnapshot();
            if(_pendingProbe is null)
            {
                _probeStarted=DateTimeOffset.UtcNow;
                _probeParent=parent; _probeBounds=_barBounds;
                _probeLayoutVersion=Volatile.Read(ref _automationLayoutVersion);
                _nativeProtectedBounds=native.Occupied.ToArray();
                var bar=_barBounds;
                _pendingProbe=Task.Run(()=>ReadAutomationOccupied(parent,bar,native));
            }
            var finished=await Task.WhenAny(_pendingProbe,Task.Delay(3500));
            // A UI Automation worker may finish after suspension, or after a rapid pause/resume.
            // Its result must never recreate or show our taskbar child in either case.
            if(!CanShow||visibilityGeneration!=_visibilityGeneration)
            {
                if(_pendingProbe.IsCompleted) _pendingProbe=null;
                if(!CanShow&&!_disposed) HideOwnWindow(InactiveStatus);
                return GetSnapshot();
            }
            if(finished!=_pendingProbe)
            { HideOwnWindow("无法确认任务栏空位，已暂停显示控件"); return GetSnapshot(); }
            var probe=await _pendingProbe; _pendingProbe=null;
            if(_disposed) return GetSnapshot();
            if(!CanShow||visibilityGeneration!=_visibilityGeneration)
            { if(!CanShow) HideOwnWindow(InactiveStatus); return GetSnapshot(); }
            if(DateTimeOffset.UtcNow-_probeStarted>TimeSpan.FromMilliseconds(3500))
            { HideOwnWindow("任务栏位置读取超时，正在重新确认空位"); return GetSnapshot(); }
            if(_probeStarted<_minimumProbeStarted||_probeLayoutVersion!=Volatile.Read(ref _automationLayoutVersion))
            { HideOwnWindow("正在重新确认停靠空位"); QueueRefresh(); return GetSnapshot(); }
            if(_probeParent!=parent||_probeBounds!=_barBounds)
            { HideOwnWindow("任务栏布局变化，正在重新定位"); return GetSnapshot(); }
            if(parent!=FindWindow("Shell_TrayWnd",null)||!IsWindow(parent)) { DestroyOwnSource(); return GetSnapshot(); }
            if(!GetWindowRect(parent,out var latestBar)||latestBar.ToRect()!=_barBounds||GetDpiForWindow(parent)!=dpi)
            { HideOwnWindow("任务栏布局变化，正在重新定位"); return GetSnapshot(); }
            if(!probe.Success)
            { HideOwnWindow("无法读取任务栏控件位置，已暂停显示控件"); return GetSnapshot(); }
            _occupiedCount=probe.Occupied.Count; _protectedNames=probe.Names.Distinct().ToArray();
            _protectedBounds=probe.Occupied.ToArray(); _protectedLayoutRead=_probeStarted; _protectedParent=parent;
            _protectedBarBounds=_probeBounds; _protectedScale=_scale; _protectedLayoutVersion=_probeLayoutVersion;
            _dockSlots=TaskbarWidgetDockLayout.FindSlots(_barBounds,probe.Occupied,ReadTrayBounds(parent),_scale,
                WidgetForVerification.DesiredDIPSize.Width,80,WidgetForVerification.DesiredDIPSize.Height);
            if(_dragging)
            {
                if(!DragContextCurrent()) CancelDrag();
                else
                {
                    if(!_dropPending) UpdateDrag(_dragPointer);
                    _refreshCount++; _status="正在选择停靠位置";
                    return GetSnapshot();
                }
            }
            var available=FindAvailableDockSlot(_dockPosition);
            if(available is null)
            { HideOwnWindow(_dockPosition==TaskbarDockPosition.Left?"最左侧没有可容纳控件的安全空位":"托盘旁没有可容纳控件的安全空位"); return GetSnapshot(); }
            if(_source is null) CreateOwnSource(parent,parentProcess,available.BoundsPixels);
            if(_source is null) return GetSnapshot();
            if(!ProtectedLayoutCurrent) { HideOwnWindow("正在重新确认任务栏空位"); QueueRefresh(); return GetSnapshot(); }
            MoveOwnSource(available.BoundsPixels);
            _refreshCount++; _status=IsEmbedded?EmbeddedStatus(available.Position):"任务栏嵌入未成功";
            SetTheme();
            return GetSnapshot();
        }
        catch(Exception ex) when(ex is Win32Exception or InvalidOperationException or COMException or ArgumentException)
        { HideOwnWindow("任务栏控件暂不可用："+ex.Message); return GetSnapshot(); }
        finally
        {
            _refreshing=false;
            // A resume can happen while the old refresh is awaiting its provider. Retry only once
            // it finishes, so QueueRefresh cannot consume the old task instead of a fresh sample.
            if(visibilityGeneration!=_visibilityGeneration&&CanShow) QueueRefresh();
        }
    }

    public async Task<TaskbarWidgetSnapshot> ForceRebindAsyncForVerification()
    {
        if(!_dispatcher.CheckAccess()) return await _dispatcher.InvokeAsync(ForceRebindAsyncForVerification).Task.Unwrap();
        CancelDrag();
        if(_activeRefresh is { IsCompleted:false }) await _activeRefresh;
        DestroyOwnSource(); return await RefreshLayoutAsync();
    }

    private void CreateOwnSource(IntPtr parent,uint parentProcess,Rect bounds)
    {
        if(!CanShow) return;
        // Match the existing parent's context while creating its child, then restore the app thread's context.
        // Mixing awareness in a cross-process HWND tree can reset the caller's process-wide DPI awareness.
        IntPtr context=GetWindowDpiAwarenessContext(parent),previous=SetThreadDpiAwarenessContext(context);
        if(previous==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"无法匹配任务栏 DPI 环境。");
        try
        {
            var point=new NativePoint { X=(int)Math.Round(bounds.X),Y=(int)Math.Round(bounds.Y) };
            if(!ScreenToClient(parent,ref point)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var parameters=new HwndSourceParameters("Gameorder.TaskbarWidget",(int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height))
            {
                ParentWindow=parent,WindowStyle=WsChild|WsClipChildren|WsClipSiblings,
                ExtendedWindowStyle=ExNoActivate,UsesPerPixelTransparency=true,TreatAsInputRoot=true,
                PositionX=point.X,PositionY=point.Y,HwndSourceHook=ChildHook
            };
            _source=new HwndSource(parameters);
            _parent=parent; _parentProcess=parentProcess;
            _source.CompositionTarget.BackgroundColor=Colors.Transparent;
            _source.RootVisual=WidgetForVerification;
            if(GetParent(_source.Handle)!=parent||!IsChild(parent,_source.Handle)||((long)GetWindowLongPtr(_source.Handle,-16)&WsChild)==0)
                throw new InvalidOperationException("Windows 未建立任务栏子窗口关系。");
            _sourceGeneration++;
        }
        catch { DestroyOwnSource(); throw; }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
            _creationContextRestored=AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),previous);
        }
    }

    private IntPtr ChildHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x0047) _windowPositionMessageCount++; // WM_WINDOWPOSCHANGED, own child only.
        if(message==0x0018) _visibilityMessageCount++; // WM_SHOWWINDOW, own child only.
        if(message==WmMouseActivate) { handled=true; return new IntPtr(3); } // MA_NOACTIVATE
        if(CanShow&&(message==WmDpiChanged||message==WmDisplayChange))
        { InvalidateLayout(); }
        return IntPtr.Zero;
    }

    private void MoveOwnSource(Rect bounds)
    {
        if(!CanShow||_source is null) return;
        Rect previousBounds=_bounds;
        uint dpi=GetDpiForWindow(_source.Handle); if(dpi>0) _scale=dpi/96d;
        WidgetForVerification.ConfigureSize(bounds.Width/_scale,bounds.Height/_scale);
        var target=new Rect(Math.Round(bounds.X),Math.Round(bounds.Y),Math.Round(bounds.Width),Math.Round(bounds.Height));
        bool visible=IsWindowVisible(_source.Handle);
        if(!GetWindowRect(_source.Handle,out var before)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Polling placement is a read-only operation when its physical bounds and visibility are unchanged.
        // A redundant SWP_SHOWWINDOW can invalidate Explorer and repaint this transparent WPF child.
        if(before.ToRect()!=target||!visible)
        {
            var point=new NativePoint { X=(int)target.X,Y=(int)target.Y };
            if(!ScreenToClient(_parent,ref point)||!SetWindowPos(_source.Handle,IntPtr.Zero,point.X,point.Y,
                (int)target.Width,(int)target.Height,0x0010u|(!visible?0x0040u:0u)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _nativeMoveCount++;
            if(!visible) _nativeShowCount++;
        }
        if(!GetWindowRect(_source.Handle,out var actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _bounds=actual.ToRect();
        if(!_barBounds.Contains(_bounds)) { HideOwnWindow("控件超出任务栏边界，已暂停显示"); return; }
        if(!DoesNotOverlapProtectedControls(_bounds)) { HideOwnWindow("任务栏空位已改变，已暂停显示控件"); return; }
        if(_bounds!=previousBounds) PlacementChanged?.Invoke(this,EventArgs.Empty);
    }

    private static void SetThemeForControl(TaskbarAccountWidget widget)
    {
        using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        widget.SetTaskbarTheme(key?.GetValue("SystemUsesLightTheme") is int value&&value==0);
    }
    private void SetTheme() { try { SetThemeForControl(WidgetForVerification); } catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException) { } }

    public static Rect? FindAvailableBounds(Rect taskbar,IReadOnlyCollection<Rect> occupied,double scale,double desiredWidthDip=360,double minimumWidthDip=80,double desiredHeightDip=48)
    {
        if(taskbar.IsEmpty||taskbar.Width<=taskbar.Height||!double.IsFinite(scale)||scale<=0) return null;
        double inset=4*scale,clearance=8*scale;
        double left=taskbar.Left+inset,right=taskbar.Right-inset;
        var segments=occupied.Select(rect=>Rect.Intersect(taskbar,rect)).Where(rect=>!rect.IsEmpty&&rect.Width>0&&rect.Height>1)
            .Select(rect=>(Start:Math.Max(left,rect.Left-clearance),End:Math.Min(right,rect.Right+clearance))).Where(s=>s.End>s.Start).OrderBy(s=>s.Start).ToArray();
        var gaps=new List<(double Start,double End)>(); double current=left;
        foreach(var segment in segments) { if(segment.Start>current) gaps.Add((current,segment.Start)); current=Math.Max(current,segment.End); }
        if(current<right) gaps.Add((current,right));
        var candidates=gaps.Where(g=>g.End-g.Start>=minimumWidthDip*scale)
            .OrderByDescending(g=>g.End-g.Start>=desiredWidthDip*scale).ThenByDescending(g=>g.End-g.Start).ThenByDescending(g=>g.End).ToArray();
        if(candidates.Length==0) return null;
        var gap=candidates[0]; double width=Math.Min(desiredWidthDip*scale,gap.End-gap.Start),height=Math.Min(desiredHeightDip*scale,taskbar.Height-2*scale);
        if(height<24*scale) return null;
        return new Rect(gap.End-width,taskbar.Top+(taskbar.Height-height)/2,width,height);
    }

    private sealed record NativeProbe(List<Rect> Occupied,List<string> Names);
    private sealed record Probe(bool Success,List<Rect> Occupied,List<string> Names);
    private static Rect ReadTrayBounds(IntPtr parent)
    {
        IntPtr tray=FindWindowEx(parent,IntPtr.Zero,"TrayNotifyWnd",null);
        return tray!=IntPtr.Zero&&IsWindowVisible(tray)&&GetWindowRect(tray,out var bounds)?bounds.ToRect():Rect.Empty;
    }
    private NativeProbe ReadNativeOccupied(IntPtr parent,Rect taskbar)
    {
        var occupied=new List<Rect>(); var names=new List<string>();
        string[] containers=["ReBarWindow32","MSTaskSwWClass","MSTaskListWClass","WorkerW","Windows.UI.Composition.DesktopWindowContentBridge","Windows.UI.Input.InputSite.WindowClass","Windows.UI.Core.CoreWindow"];
        EnumChildWindows(parent,(window,_)=>
        {
            GetWindowThreadProcessId(window,out uint process);
            if(process==_ownProcess||!IsWindowVisible(window)||!GetWindowRect(window,out var native)) return true;
            var rect=Rect.Intersect(taskbar,native.ToRect());
            if(rect.IsEmpty||rect.Width<=0||rect.Height<=0) return true;
            string cls=WindowClass(window);
            if(containers.Contains(cls,StringComparer.Ordinal)) return true;
            // Unknown native children are existing widgets, not empty taskbar space.
            occupied.Add(rect); names.Add(cls);
            return true;
        },IntPtr.Zero);
        // Z-order changes do not change free space. Keep equivalent native obstacles in stable order.
        return new(occupied.Distinct().OrderBy(rect=>rect.Left).ThenBy(rect=>rect.Top)
            .ThenBy(rect=>rect.Width).ThenBy(rect=>rect.Height).ToList(),names);
    }

    private void InvalidateLayout()
    {
        if(!CanShow) return;
        Interlocked.Increment(ref _automationLayoutVersion);
        CancelDrag(); HideOwnWindow("正在确认任务栏布局"); QueueRefresh();
    }
    private Probe ReadAutomationOccupied(IntPtr parent,Rect taskbar,NativeProbe native)
    {
        var occupied=new List<Rect>(native.Occupied); var names=new List<string>(native.Names);
        if(!CanObserveAutomation) return new(false,occupied,names);
        try
        {
            var cache=new CacheRequest { TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full };
            foreach(var property in new[]{AutomationElement.BoundingRectangleProperty,AutomationElement.ControlTypeProperty,AutomationElement.NameProperty,
                AutomationElement.IsOffscreenProperty,AutomationElement.ProcessIdProperty,AutomationElement.IsKeyboardFocusableProperty,AutomationElement.ClassNameProperty}) cache.Add(property);
            using(cache.Activate())
            {
                var root=AutomationElement.FromHandle(parent);
                if(!CanObserveAutomation) return new(false,occupied,names);
                Interlocked.Increment(ref _automationProbeCount);
                var elements=root.FindAll(TreeScope.Descendants,Automation.ControlViewCondition);
                if(elements.Count==0||elements.Count>700) return new(false,occupied,names);
                for(int i=0;i<elements.Count&&i<700;i++)
                {
                    if(!CanObserveAutomation) return new(false,occupied,names);
                    var element=elements[i]; var data=element.Cached;
                    if(data.ProcessId==_ownProcess||data.IsOffscreen) continue;
                    Rect rect=Rect.Intersect(taskbar,data.BoundingRectangle);
                    if(rect.IsEmpty||rect.Width<=0||rect.Height<=0) continue;
                    var type=data.ControlType;
                    bool actionable=type==ControlType.Button||type==ControlType.MenuItem||type==ControlType.TabItem||type==ControlType.ListItem
                        ||type==ControlType.SplitButton||type==ControlType.Edit||type==ControlType.Slider||type==ControlType.CheckBox||data.IsKeyboardFocusable;
                    bool text=type==ControlType.Text||type==ControlType.Image;
                    bool namedCustom=type==ControlType.Custom&&!string.IsNullOrWhiteSpace(data.Name)&&rect.Width<taskbar.Width*.6;
                    if(!actionable&&!text&&!namedCustom) continue;
                    occupied.Add(rect); names.Add(data.Name.Length>0?data.Name:data.ClassName);
                }
            }
            return new(true,occupied,names);
        }
        catch(Exception ex) when(ex is ElementNotAvailableException or COMException or InvalidOperationException or NotSupportedException)
        { return new(false,occupied,names); }
    }

    public Rect GetChooserPlacement(Size desiredDipSize)=>GetChooserPlacementDetails(desiredDipSize).BoundsDip;
    public TaskbarWidgetChooserPlacement GetChooserPlacementDetails(Size desiredDipSize)
    {
        Rect anchor=_bounds.IsEmpty?_barBounds:_bounds;
        if(anchor.IsEmpty)
        {
            // A tray command can arrive before the first layout probe; use a finite fallback until attached.
            Rect fallback=SystemParameters.WorkArea;
            double fallbackWidth=Math.Min(desiredDipSize.Width,Math.Max(1,fallback.Width-16));
            double fallbackHeight=Math.Min(desiredDipSize.Height,Math.Max(1,fallback.Height-16));
            var fallbackBounds=new Rect(fallback.Right-fallbackWidth-8,fallback.Bottom-fallbackHeight-8,fallbackWidth,fallbackHeight);
            return new(fallbackBounds,fallbackBounds,fallback,1);
        }
        var point=new NativePoint { X=(int)Math.Round(anchor.Left+anchor.Width/2),Y=(int)Math.Round(anchor.Top+anchor.Height/2) };
        var info=new MonitorInfo { Size=Marshal.SizeOf<MonitorInfo>() };
        Rect work=GetMonitorInfo(MonitorFromPoint(point,2),ref info)?info.Work.ToRect():SystemParameters.WorkArea;
        double margin=8*_scale;
        double width=Math.Min(desiredDipSize.Width*_scale,Math.Max(1,work.Width-2*margin));
        double height=Math.Min(desiredDipSize.Height*_scale,Math.Max(1,work.Height-2*margin));
        double x=Math.Clamp(anchor.Left+anchor.Width/2-width/2,work.Left+margin,Math.Max(work.Left+margin,work.Right-width-margin));
        bool taskbarAtTop=_barBounds.Top<work.Top;
        double y=taskbarAtTop?_barBounds.Bottom+margin:_barBounds.Top-height-margin;
        y=Math.Clamp(y,work.Top+margin,Math.Max(work.Top+margin,work.Bottom-height-margin));
        var bounds=new Rect(x,y,width,height);
        return new(new Rect(x/_scale,y/_scale,width/_scale,height/_scale),bounds,work,_scale);
    }
    public void PositionChooser(Window popup,TaskbarWidgetChooserPlacement placement)
    {
        var handle=new WindowInteropHelper(popup).EnsureHandle(); var bounds=placement.BoundsPixels;
        SetWindowPos(handle,new IntPtr(-1),(int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),(int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height),0x0010|0x0200);
        uint dpi=GetDpiForWindow(handle); double scale=dpi>0?dpi/96d:placement.DpiScale;
        if(Math.Abs(scale-placement.DpiScale)>.005)
        {
            // The HWND has moved onto the taskbar monitor; remeasure its real DPI before final placement.
            double width=Math.Min(placement.BoundsDip.Width*scale,placement.WorkAreaPixels.Width-16*scale);
            double height=Math.Min(placement.BoundsDip.Height*scale,placement.WorkAreaPixels.Height-16*scale);
            double x=Math.Clamp(bounds.Left+bounds.Width/2-width/2,placement.WorkAreaPixels.Left+8*scale,
                Math.Max(placement.WorkAreaPixels.Left+8*scale,placement.WorkAreaPixels.Right-width-8*scale));
            double y=_barBounds.Top<placement.WorkAreaPixels.Top?_barBounds.Bottom+8*scale:_barBounds.Top-height-8*scale;
            y=Math.Clamp(y,placement.WorkAreaPixels.Top+8*scale,Math.Max(placement.WorkAreaPixels.Top+8*scale,placement.WorkAreaPixels.Bottom-height-8*scale));
            bounds=new Rect(x,y,Math.Max(1,width),Math.Max(1,height));
        }
        SetWindowPos(handle,new IntPtr(-1),(int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),(int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height),0x0010|0x0200);
    }

    private TaskbarWidgetSnapshot GetSnapshot()
    {
        IntPtr handle=_source?.Handle??IntPtr.Zero;
        if(_parent!=IntPtr.Zero&&GetWindowRect(_parent,out var barRect)) _barBounds=barRect.ToRect();
        if(handle!=IntPtr.Zero&&GetWindowRect(handle,out var widgetRect)) _bounds=widgetRect.ToRect();
        bool child=handle!=IntPtr.Zero&&IsWindow(handle)&&((long)GetWindowLongPtr(handle,-16)&WsChild)!=0;
        bool parentVerified=child&&GetParent(handle)==_parent&&IsChild(_parent,handle);
        bool transparent=handle!=IntPtr.Zero&&((long)GetWindowLongPtr(handle,-20)&0x00080000)!=0;
        var point=new NativePoint();
        if(!_bounds.IsEmpty) { point.X=(int)Math.Round(_bounds.Left+_bounds.Width/2); point.Y=(int)Math.Round(_bounds.Top+_bounds.Height/2); }
        IntPtr hit=!_bounds.IsEmpty?WindowFromPoint(point):IntPtr.Zero;
        bool hitOwn=handle!=IntPtr.Zero&&(hit==handle||IsChild(handle,hit));
        uint parentDpi=_parent!=IntPtr.Zero?GetDpiForWindow(_parent):0,childDpi=handle!=IntPtr.Zero?GetDpiForWindow(handle):0;
        bool contextMatched=parentVerified&&AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(_parent),GetWindowDpiAwarenessContext(handle));
        return new(IsEmbedded,_status,(long)_parent,(long)handle,WindowClass(_parent),child,parentVerified,!_bounds.IsEmpty&&_barBounds.Contains(_bounds),transparent,
            _barBounds.IsEmpty?new Rect():_barBounds,_bounds.IsEmpty?new Rect():_bounds,_scale,_bounds.IsEmpty?0:_bounds.Width/_scale,_occupiedCount,_refreshCount,_protectedNames,
            hitOwn,(long)hit,contextMatched,parentDpi,childDpi,_creationContextRestored,_sourceGeneration,_destroyedOwnSourceCount,
            DoesNotOverlapProtectedControls(_bounds),ProtectedLayoutCurrent,_enabled,_dockPosition==TaskbarDockPosition.Tray?"tray":"left",_dragging,_dropPending,
            _previewSlot is null?null:_previewSlot.Position==TaskbarDockPosition.Tray?"tray":"left",_dockSlots.Available.ToArray(),
            _hint?.IsVisible??false,(long)(_hint?.Handle??IntPtr.Zero),_hint?.BoundsPixels is Rect hintBounds&&!hintBounds.IsEmpty?hintBounds:new Rect(),
            _hint is { IsVisible:true }&&SameSize(_hint.BoundsPixels,_bounds),_hint?.InputTransparent??false,_hint?.NoActivate??false,_hint?.NoTaskbarButton??false,
            _successfulDrops,_canceledDrags,_temporarilySuspended,_watchdog.IsEnabled,_watchdog.Interval.TotalSeconds,AutomationProbeCount,AutomationWatcherActive,
            _nativeMoveCount,_nativeShowCount,_nativeHideCount,_windowPositionMessageCount,_visibilityMessageCount);
    }
    private bool ProtectedLayoutCurrent=>_protectedParent!=IntPtr.Zero&&_protectedParent==_parent
        &&_protectedBarBounds==_barBounds&&Math.Abs(_protectedScale-_scale)<.005
        &&_protectedLayoutVersion==Volatile.Read(ref _automationLayoutVersion)
        &&DateTimeOffset.UtcNow-_protectedLayoutRead<=ProtectedLayoutLifetime;
    private bool DoesNotOverlapProtectedControls(Rect bounds)=>ProtectedLayoutCurrent&&!bounds.IsEmpty
        &&_protectedBounds.All(protectedBounds=>Rect.Intersect(bounds,protectedBounds) is var intersection
            &&(intersection.IsEmpty||intersection.Width<=0||intersection.Height<=0));
    private void HideOwnWindow(string status)
    {
        if((_dragging||_dropPending)&&!_canceling) CancelDrag();
        if(_source is not null&&!_source.IsDisposed&&IsWindow(_source.Handle)&&IsWindowVisible(_source.Handle))
        { ShowWindow(_source.Handle,0); _nativeHideCount++; }
        _hint?.Hide(); _status=!CanShow&&!_disposed?InactiveStatus:status;
    }
    private void DestroyOwnSource()
    {
        var source=_source; _source=null;
        if(source is not null) { if(!source.IsDisposed) source.RootVisual=null; source.Dispose(); _destroyedOwnSourceCount++; }
        _parent=IntPtr.Zero; _parentProcess=0; _bounds=Rect.Empty;
    }
    private static string WindowClass(IntPtr window) { var text=new StringBuilder(120); if(window!=IntPtr.Zero) GetClassName(window,text,text.Capacity); return text.ToString(); }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;
        CancelDrag(); _watchdog.Stop(); _watchdog.Tick-=WatchdogTick;
        _dragWatchdog.Stop(); _dragWatchdog.Tick-=CheckDragState;
        foreach(var window in _watchedWindows) window.SourceInitialized-=WatchSourceInitialized;
        foreach(var source in _watchedSources) if(!source.IsDisposed) source.RemoveHook(ShellMonitorHook);
        _watchedSources.Clear(); _watchedWindows.Clear();
        DestroyOwnSource(); WidgetForVerification.OpenAccountsRequested-=OpenAccounts; WidgetForVerification.OpenManagerRequested-=OpenManager;
        WidgetForVerification.SwitchRequested-=SwitchAccount;
        WidgetForVerification.DragStarted-=DragStarted; WidgetForVerification.DragMoved-=DragMoved;
        WidgetForVerification.DragCompleted-=DragCompleted; WidgetForVerification.DragCanceled-=DragCanceled;
        _hint?.Dispose(); _hint=null; WidgetForVerification.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; public readonly Rect ToRect()=>new(Left,Top,Math.Max(0,Right-Left),Math.Max(0,Bottom-Top)); }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    private delegate bool EnumWindowsCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string? className,string? title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string? className,string? title);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent,EnumWindowsCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent,IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first,IntPtr second);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool ScreenToClient(IntPtr window,ref NativePoint point);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}


