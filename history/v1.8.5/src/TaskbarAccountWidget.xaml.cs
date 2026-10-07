using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Gameorder;

public sealed class TaskbarAccountSwitchEventArgs(SteamAccount account,SwitchMode mode) : EventArgs
{
    public SteamAccount Account { get; }=account;
    public SwitchMode Mode { get; }=mode;
}

public sealed class TaskbarWidgetDragEventArgs(Point screenPixels,Point startScreenPixels) : EventArgs
{
    public Point ScreenPixels { get; }=screenPixels;
    public Point StartScreenPixels { get; }=startScreenPixels;
}

public sealed record TaskbarWidgetDragVerification(bool HasDragCandidate,bool IsDragging,bool HasMouseCapture,
    bool InteractionEnabled,bool ButtonsEnabled,double GripWidthDip,double HorizontalThresholdPixels,
    double VerticalThresholdPixels,int StartedCount,int MovedCount,int CompletedCount,int CanceledCount,
    int CaptureAttemptCount,int CaptureSuccessCount,int CaptureTransitionInputIgnored,bool MouseCapturedByWidget,string? MouseCaptureOwnerType);

public partial class TaskbarAccountWidget : UserControl,IDisposable
{
    private readonly bool _ownsViewModel;
    private bool _disposed,_interactionEnabled=true,_dragCandidate,_isDragging,_endingCapture,_acquiringCapture;
    private bool _sizeConfigured,_themeApplied;
    private double _configuredWidth,_configuredHeight;
    private Point _dragStartScreenPixels;
    private double _horizontalThresholdPixels,_verticalThresholdPixels;
    private int _dragStartedCount,_dragMovedCount,_dragCompletedCount,_dragCanceledCount;
    private int _captureAttemptCount,_captureSuccessCount,_captureTransitionInputIgnored;
    public TaskbarWidgetViewModel? ViewModel => DataContext as TaskbarWidgetViewModel;
    public Size DesiredDIPSize => new(360,48);
    public bool IsDarkTaskbar { get; private set; }
    public int SizeApplyCount { get; private set; }
    public int ThemeApplyCount { get; private set; }
    public event EventHandler? OpenAccountsRequested;
    public event EventHandler? OpenManagerRequested;
    public event EventHandler<TaskbarAccountSwitchEventArgs>? SwitchRequested;
    public event EventHandler<TaskbarWidgetDragEventArgs>? DragStarted;
    public event EventHandler<TaskbarWidgetDragEventArgs>? DragMoved;
    public event EventHandler<TaskbarWidgetDragEventArgs>? DragCompleted;
    public event EventHandler? DragCanceled;
    public bool IsDragging=>_isDragging;
    public bool HasDragCandidate=>_dragCandidate;
    public TaskbarWidgetDragVerification DragVerification=>new(_dragCandidate,_isDragging,IsMouseCaptured,
        _interactionEnabled,WidgetContent.IsEnabled,DragGrip.Width,_horizontalThresholdPixels,
        _verticalThresholdPixels,_dragStartedCount,_dragMovedCount,_dragCompletedCount,_dragCanceledCount,
        _captureAttemptCount,_captureSuccessCount,_captureTransitionInputIgnored,ReferenceEquals(Mouse.Captured,this),Mouse.Captured?.GetType().FullName);
    public static readonly DependencyProperty ShowLabelsProperty=DependencyProperty.Register(nameof(ShowLabels),typeof(bool),typeof(TaskbarAccountWidget),new PropertyMetadata(true));
    public static readonly DependencyProperty ShowSwitchControlsProperty=DependencyProperty.Register(nameof(ShowSwitchControls),typeof(bool),typeof(TaskbarAccountWidget),new PropertyMetadata(true));
    public static readonly DependencyProperty ShowNavigationProperty=DependencyProperty.Register(nameof(ShowNavigation),typeof(bool),typeof(TaskbarAccountWidget),new PropertyMetadata(true));
    public static readonly DependencyProperty ShowManagerButtonProperty=DependencyProperty.Register(nameof(ShowManagerButton),typeof(bool),typeof(TaskbarAccountWidget),new PropertyMetadata(true));
    public static readonly DependencyProperty ShowNoteProperty=DependencyProperty.Register(nameof(ShowNote),typeof(bool),typeof(TaskbarAccountWidget),new PropertyMetadata(true));
    public static readonly DependencyProperty AvatarSizeProperty=DependencyProperty.Register(nameof(AvatarSize),typeof(double),typeof(TaskbarAccountWidget),new PropertyMetadata(32d));
    public bool ShowLabels { get=>(bool)GetValue(ShowLabelsProperty); private set=>SetValue(ShowLabelsProperty,value); }
    public bool ShowSwitchControls { get=>(bool)GetValue(ShowSwitchControlsProperty); private set=>SetValue(ShowSwitchControlsProperty,value); }
    public bool ShowNavigation { get=>(bool)GetValue(ShowNavigationProperty); private set=>SetValue(ShowNavigationProperty,value); }
    public bool ShowManagerButton { get=>(bool)GetValue(ShowManagerButtonProperty); private set=>SetValue(ShowManagerButtonProperty,value); }
    public bool ShowNote { get=>(bool)GetValue(ShowNoteProperty); private set=>SetValue(ShowNoteProperty,value); }
    public double AvatarSize { get=>(double)GetValue(AvatarSizeProperty); private set=>SetValue(AvatarSizeProperty,value); }

    public TaskbarAccountWidget()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown+=OnDragMouseDown;
        PreviewMouseMove+=OnDragMouseMove;
        PreviewMouseLeftButtonUp+=OnDragMouseUp;
        MouseLeave+=OnDragMouseLeave;
        LostMouseCapture+=OnDragCaptureLost;
        PreviewKeyDown+=OnDragKeyDown;
        PreviewMouseRightButtonDown+=(_,e)=> { if(!CanInteract) e.Handled=true; };
        ContextMenuOpening+=(_,e)=> { if(!CanInteract) e.Handled=true; };
        Unloaded+=(_,_)=>CancelDragGesture();
    }
    public TaskbarAccountWidget(TaskbarWidgetViewModel viewModel):this() { DataContext=viewModel; }
    public TaskbarAccountWidget(MainViewModel host):this(new TaskbarWidgetViewModel(host)) { _ownsViewModel=true; }

    public void ConfigureSize(double widthDip,double heightDip)
    {
        if(!double.IsFinite(widthDip)||!double.IsFinite(heightDip)||widthDip<=0||heightDip<=0) throw new ArgumentException("任务栏尺寸无效。");
        if(_sizeConfigured&&_configuredWidth==widthDip&&_configuredHeight==heightDip&&Width==widthDip&&Height==heightDip) return;
        Width=widthDip; Height=heightDip;
        ShowLabels=widthDip>=200; ShowSwitchControls=widthDip>=200;
        ShowNavigation=widthDip>=300; ShowManagerButton=widthDip>=340;
        ShowNote=heightDip>=40;
        AvatarSize=Math.Clamp(heightDip-14,20,32);
        WidgetFrame.Padding=new Thickness(widthDip<100?3:8,4,widthDip<100?3:8,4);
        AccountAvatarButton.Margin=new Thickness(0,0,widthDip<100?6:8,0);
        _configuredWidth=widthDip; _configuredHeight=heightDip; _sizeConfigured=true; SizeApplyCount++;
    }

    public void SetTaskbarTheme(bool isDark)
    {
        if(_themeApplied&&IsDarkTaskbar==isDark) return;
        IsDarkTaskbar=isDark;
        Resources["WidgetForeground"]=Brush(isDark?"#F1F5FC":"#293C57");
        Resources["WidgetMuted"]=Brush(isDark?"#B3C1D6":"#7588A3");
        Resources["WidgetHover"]=Brush(isDark?"#26FFFFFF":"#CCFFFFFF");
        Resources["WidgetButtonHover"]=Brush(isDark?"#28FFFFFF":"#85C8D8EE");
        Resources["WidgetFocus"]=Brush(isDark?"#84A8DA":"#6385B4");
        _themeApplied=true; ThemeApplyCount++;
    }
    private static SolidColorBrush Brush(string color)
    {
        var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush;
    }
    public bool RequestSwitch(SwitchMode mode)
    {
        if(!CanInteract) return false;
        var model=ViewModel;
        var account=model?.SelectedAccount;
        if(account is null||model is null||!(mode==SwitchMode.Online?model.CanOnline:mode==SwitchMode.Offline&&model.CanOffline)) return false;
        if(!model.Accounts.Any(a=>ReferenceEquals(a,account))) return false;
        SwitchRequested?.Invoke(this,new(account,mode)); return true;
    }
    private bool CanInteract=>!_disposed&&_interactionEnabled&&!_dragCandidate&&!_isDragging;
    public void RequestOpenAccounts() { if(CanInteract) OpenAccountsRequested?.Invoke(this,EventArgs.Empty); }
    public void RequestOpenManager() { if(CanInteract) OpenManagerRequested?.Invoke(this,EventArgs.Empty); }
    private void OpenAccountsClick(object sender,RoutedEventArgs e)=>RequestOpenAccounts();
    private void OpenManagerClick(object sender,RoutedEventArgs e)=>RequestOpenManager();
    private void ManagerMenuClick(object sender,RoutedEventArgs e)=>RequestOpenManager();
    private void FollowCurrentMenuClick(object sender,RoutedEventArgs e) { if(CanInteract) ViewModel?.FollowCurrentAccount(); }
    private void OnlineClick(object sender,RoutedEventArgs e)=>RequestSwitch(SwitchMode.Online);
    private void OfflineClick(object sender,RoutedEventArgs e)=>RequestSwitch(SwitchMode.Offline);
    public void EnableInteraction(bool enabled)
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(()=>EnableInteraction(enabled)); return; }
        _interactionEnabled=enabled;
        if(!enabled) CancelDragGesture();
        UpdateGestureAppearance();
    }
    public void CancelDragGesture()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CancelDragGesture); return; }
        bool wasDragging=_isDragging;
        bool hadGesture=_dragCandidate||wasDragging;
        _dragCandidate=false; _isDragging=false;
        ReleaseGestureCapture(); UpdateGestureAppearance();
        if(hadGesture)
        {
            _dragCanceledCount++;
            // A pending press has not called the host yet, so only active drags need a host cancellation.
            if(wasDragging) DragCanceled?.Invoke(this,EventArgs.Empty);
        }
    }
    private void OnDragMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(!CanInteract) { e.Handled=true; return; }
        if(IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if(TryGetScreenPoint(e,out var point)&&ArmDragGesture(point)) e.Handled=true;
    }
    private void OnDragMouseMove(object sender,MouseEventArgs e)
    {
        // WPF synchronizes mouse-over during CaptureMouse and can reenter with synthesized move input.
        // The outer threshold transition is still responsible for committing or rejecting the capture.
        if(_acquiringCapture) { _captureTransitionInputIgnored++; e.Handled=true; return; }
        if(!_dragCandidate&&!_isDragging) return;
        if(e.LeftButton!=MouseButtonState.Pressed) { CancelDragGesture(); return; }
        if(!TryGetScreenPoint(e,out var point)) { CancelDragGesture(); return; }
        AdvanceDragGesture(point);
        e.Handled=true;
    }
    private void OnDragMouseUp(object sender,MouseButtonEventArgs e)
    {
        if(!_dragCandidate&&!_isDragging) return;
        if(TryGetScreenPoint(e,out var point)) CompleteDragGesture(point);
        else CancelDragGesture();
        e.Handled=true;
    }
    private void OnDragMouseLeave(object sender,MouseEventArgs e)
    {
        if(_acquiringCapture) { _captureTransitionInputIgnored++; return; }
        if(!_dragCandidate||_isDragging) return;
        // A quick movement may leave the eight-DIP grip before the next mouse-move arrives.
        if(e.LeftButton==MouseButtonState.Pressed&&TryGetScreenPoint(e,out var point)) AdvanceDragGesture(point);
        if(_dragCandidate&&!_isDragging) CancelDragGesture();
    }
    private void OnDragCaptureLost(object sender,MouseEventArgs e)
    {
        if(!_endingCapture&&_isDragging&&!IsMouseCaptured) CancelDragGesture();
    }
    private void OnDragKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape&&(_dragCandidate||_isDragging)) { CancelDragGesture(); e.Handled=true; }
    }
    private bool ArmDragGesture(Point screenPixels)
    {
        if(!CanInteract||!IsFinite(screenPixels)) return false;
        _dragStartScreenPixels=screenPixels;
        var transform=PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice??Matrix.Identity;
        var threshold=transform.Transform(new Vector(SystemParameters.MinimumHorizontalDragDistance,SystemParameters.MinimumVerticalDragDistance));
        _horizontalThresholdPixels=Math.Max(1,Math.Abs(threshold.X));
        _verticalThresholdPixels=Math.Max(1,Math.Abs(threshold.Y));
        _dragCandidate=true;
        UpdateGestureAppearance();
        return true;
    }
    private bool AdvanceDragGesture(Point screenPixels)
    {
        if((!_dragCandidate&&!_isDragging)||!IsFinite(screenPixels)) return false;
        if(!_isDragging)
        {
            if(Math.Abs(screenPixels.X-_dragStartScreenPixels.X)<_horizontalThresholdPixels&&
                Math.Abs(screenPixels.Y-_dragStartScreenPixels.Y)<_verticalThresholdPixels) return false;
            bool captured;
            _captureAttemptCount++; _acquiringCapture=true;
            try { captured=CaptureMouse(); }
            finally { _acquiringCapture=false; }
            if(!captured||!IsMouseCaptured||!_dragCandidate||_disposed||!_interactionEnabled)
            { CancelDragGesture(); return false; }
            _captureSuccessCount++;
            _dragCandidate=false; _isDragging=true; UpdateGestureAppearance(); _dragStartedCount++;
            DragStarted?.Invoke(this,new(screenPixels,_dragStartScreenPixels));
            // The host may reject a stale or unavailable destination in the Started callback.
            if(!_isDragging) return false;
        }
        _dragMovedCount++;
        DragMoved?.Invoke(this,new(screenPixels,_dragStartScreenPixels));
        return _isDragging;
    }
    private bool CompleteDragGesture(Point screenPixels)
    {
        if((!_dragCandidate&&!_isDragging)||!IsFinite(screenPixels)) return false;
        bool wasDragging=_isDragging;
        _dragCandidate=false; _isDragging=false;
        ReleaseGestureCapture(); UpdateGestureAppearance();
        if(wasDragging) { _dragCompletedCount++; DragCompleted?.Invoke(this,new(screenPixels,_dragStartScreenPixels)); }
        return wasDragging;
    }
    private void ReleaseGestureCapture()
    {
        _endingCapture=true;
        try { if(IsMouseCaptured) ReleaseMouseCapture(); }
        finally { _endingCapture=false; }
    }
    private void UpdateGestureAppearance()
    {
        WidgetContent.IsEnabled=CanInteract;
        if(_isDragging) Cursor=Cursors.SizeAll;
        else ClearValue(CursorProperty);
    }
    private bool TryGetScreenPoint(MouseEventArgs e,out Point point)
    {
        point=default;
        if(PresentationSource.FromVisual(this) is null) return false;
        try { point=PointToScreen(e.GetPosition(this)); return IsFinite(point); }
        catch(InvalidOperationException) { return false; }
    }
    private bool IsInteractiveSource(DependencyObject? source)
    {
        for(var current=source;current is not null;current=InputParent(current))
        {
            if(current is ButtonBase or TextBoxBase or PasswordBox or MenuItem or Hyperlink or Selector) return true;
            if(ReferenceEquals(current,this)) break;
        }
        return false;
    }
    private static DependencyObject? InputParent(DependencyObject current)=>current switch
    {
        Visual or Visual3D=>VisualTreeHelper.GetParent(current),
        FrameworkContentElement content=>content.Parent??ContentOperations.GetParent(content),
        _=>LogicalTreeHelper.GetParent(current)
    };
    private static bool IsFinite(Point point)=>double.IsFinite(point.X)&&double.IsFinite(point.Y);

    // These use the same gesture state and the widget's own capture; they never inject mouse or keyboard input.
    public bool RequestDragPressForVerification(Point screenPixels,bool interactiveSource=false)=>!interactiveSource&&ArmDragGesture(screenPixels);
    public bool RequestDragMoveForVerification(Point screenPixels)=>AdvanceDragGesture(screenPixels);
    public bool RequestDragReleaseForVerification(Point screenPixels)=>CompleteDragGesture(screenPixels);
    public void RequestDragEscapeForVerification()=>CancelDragGesture();
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true; CancelDragGesture(); UpdateGestureAppearance();
        if(_ownsViewModel) ViewModel?.Dispose();
    }
}

