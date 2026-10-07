using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Gameorder;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private AppLockDialog? _securityDialog;
    private ForgotPasswordDialog? _forgotPasswordDialog;
    private PasswordRevealController? _unlockReveal;
    private ResetTestWidgetPreview? _testWidgetPreview;
    private PendingWidgetSwitch? _pendingWidgetSwitch;
    private sealed record PendingWidgetSwitch(string SteamId,string AccountName,SwitchMode Mode);
    private readonly bool _persistTaskbarPosition;
    private TrayService? _tray;
    private TaskbarWidgetHost? _widget;
    private QuickAccountWindow? _quickAccounts;
    private bool _exitRequested;
    private readonly DispatcherTimer _viewReleaseTimer=new(DispatcherPriority.Background) {Interval=TimeSpan.FromSeconds(10)};
    private bool _viewResourcesReleased;
    private int _viewReleaseCount,_viewRestoreCount;
    public bool ViewResourcesReleased=>_viewResourcesReleased;
    public int ViewReleaseCount=>_viewReleaseCount;
    public int ViewRestoreCount=>_viewRestoreCount;
    private HwndSource? _chromeSource;
    private nint _chromeHandle;
    private int _blockedMaximizeRequests,_blockedCaptionDoubleClicks;
    public bool CanMaximize=>false;
    public double CustomCaptionHeight=>WindowChrome.GetWindowChrome(this)?.CaptionHeight??0;
    public int CustomCaptionButtonCount=>2;
    public MainWindowChromeVerification NativeChromeVerification=>CaptureChromeVerification();
    public bool ShellIntegrationEnabled { get; }
    public bool TrayIconVisible=>_tray?.IsVisible==true;
    public bool QuickAccountsVisible=>_quickAccounts?.IsVisible==true;
    public QuickAccountWindow? QuickAccountsWindowForVerification=>_quickAccounts;
    public TaskbarWidgetHost? TaskbarWidgetForVerification=>_widget;
    public ResetTestWidgetPreview? ResetTestWidgetPreviewForVerification=>_testWidgetPreview;
    public TrayMenuVerification PrepareQuickMenuForVerification()=>_tray?.PrepareMenuForVerification()??new(0,0,0,0,0);
    public void SaveNativeTrayMenuPreviewForVerification(string output)=>_tray?.SaveMenuPreviewForVerification(output);

    public MainWindow(bool demo=false,bool allowAvatarNetwork=true,bool enableShellIntegration=true,bool persistTaskbarPosition=true,AppLockService? appLockService=null,TimeProvider? appLockTimeProvider=null,GameorderDataContext? dataContext=null)
    {
        InitializeComponent();
        InitializeUnlockReveal();
        _viewModel=new MainViewModel(demo,allowAvatarNetwork,appLockService,appLockTimeProvider,dataContext:dataContext);
        DataContext=_viewModel;
        _persistTaskbarPosition=persistTaskbarPosition;
        _viewReleaseTimer.Tick+=(_,_)=> { _viewReleaseTimer.Stop(); ReleaseGameViewResources(); };
        _viewModel.PropertyChanged+=OnTaskbarPreferenceChanged;
        _viewModel.UnlockRequested+=OnUnlockRequested;
        _viewModel.AppLockSettingsRequested+=OnAppLockSettingsRequested;
        _viewModel.ForgotPasswordRequested+=OnForgotPasswordRequested;
        PreviewMouseDown+=(_,_)=>_viewModel.RecordAppActivity();
        PreviewMouseWheel+=(_,_)=>_viewModel.RecordAppActivity();
        PreviewKeyDown+=(_,_)=>_viewModel.RecordAppActivity();
        if(!demo) SystemEvents.SessionSwitch+=OnWindowsSessionSwitch;
        ShellIntegrationEnabled=enableShellIntegration&&!demo;
        SourceInitialized+=(_,_)=>InitializeNativeChrome();
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { _viewModel.CloseTransient(); e.Handled=true; } };
        Closing+=OnClosing;
        Deactivated+=(_,_)=>_unlockReveal?.Hide();
        Application.Current.SessionEnding+=OnSessionEnding;
        StateChanged+=(_,_)=>
        {
            // Native commands are blocked below; this also catches direct WPF assignments or snap paths.
            if(WindowState==WindowState.Maximized) { _blockedMaximizeRequests++; WindowState=WindowState.Normal; return; }
            if(WindowState==WindowState.Minimized&&!_exitRequested)
            {
                if(ShellIntegrationEnabled||_viewModel.IsResetTest) HideToBackground();
                else _viewModel.BeginBackgroundLockCountdown();
            }
            else if(WindowState==WindowState.Normal&&IsVisible) _viewModel.CancelBackgroundLockCountdown();
        };
        Loaded+=(_,_)=>
        {
            if(_viewModel.IsResetTest&&_testWidgetPreview is null)
            { _testWidgetPreview=new ResetTestWidgetPreview(_viewModel,this); _testWidgetPreview.Show(); }
            if(!ShellIntegrationEnabled||_tray is not null) return;
            try { _tray=new TrayService(_viewModel,this); }
            catch(Exception ex) { _tray?.Dispose(); _tray=null; _viewModel.ShowStatus("托盘暂不可用",ex.Message,"info"); }
            try
            {
                _widget=new TaskbarWidgetHost(_viewModel,ShowQuickAccounts,(account,mode)=>_ = RequestWidgetSwitchAsync(account,mode),ShowFromTray);
                _widget.WatchWindow(this); _widget.PlacementChanged+=OnTaskbarPlacementChanged;
                _widget.DockPositionCommitted+=OnTaskbarDockPositionCommitted;
                _widget.SetDockPosition(ReadTaskbarDockPosition());
                _widget.SetTemporarilySuspended(_viewModel.IsGameRunning);
                _widget.SetEnabled(_viewModel.TaskbarWidgetEnabled);
            }
            catch(Exception ex) { _viewModel.ShowStatus("任务栏控件暂不可用",ex.Message,"info"); }
        };
        Closed+=(_,_)=>
        {
            _viewReleaseTimer.Stop();
            _unlockReveal?.Dispose(); _unlockReveal=null;
            UnlockPassword?.Clear();
            if(_chromeSource is { IsDisposed:false }) _chromeSource.RemoveHook(ChromeMessageHook);
            _chromeSource=null; _chromeHandle=0;
            _viewModel.PropertyChanged-=OnTaskbarPreferenceChanged;
            _viewModel.UnlockRequested-=OnUnlockRequested;
            _viewModel.AppLockSettingsRequested-=OnAppLockSettingsRequested;
            _viewModel.ForgotPasswordRequested-=OnForgotPasswordRequested;
            if(!_viewModel.IsDemo) SystemEvents.SessionSwitch-=OnWindowsSessionSwitch;
            _securityDialog?.Close(); _securityDialog=null;
            _forgotPasswordDialog?.Close(); _forgotPasswordDialog=null;
            _testWidgetPreview?.CloseFromOwner(); _testWidgetPreview=null;
            _pendingWidgetSwitch=null;
            CloseQuickAccounts(); _widget?.Dispose(); _widget=null;
            Application.Current.SessionEnding-=OnSessionEnding;
            _tray?.Dispose(); _tray=null; _viewModel.Dispose();
        };
        // WPF's native input provider otherwise retains the last focused control after Hide.
        // Capture None for this source only, then restore the default before other windows are created.
        var restoreFocusMode=Keyboard.DefaultRestoreFocusMode;
        try
        {
            Keyboard.DefaultRestoreFocusMode=RestoreFocusMode.None;
            _=new WindowInteropHelper(this).EnsureHandle();
        }
        finally { Keyboard.DefaultRestoreFocusMode=restoreFocusMode; }
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_exitRequested||Dispatcher.HasShutdownStarted) return;
        if(_viewModel.IsResetTest||ShellIntegrationEnabled&&(_tray is not null||_widget is not null))
        { e.Cancel=true; CloseQuickAccounts(); HideToBackground(); return; }
        if(_viewModel.IsBusy) { e.Cancel=true; _viewModel.ShowStatus("请稍候","Steam 正在切换，完成后可退出。","info"); }
    }
    private void OnSessionEnding(object sender,SessionEndingCancelEventArgs e)=>_exitRequested=true;
    public void HideToBackground()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(HideToBackground); return; }
        _pendingWidgetSwitch=null;
        _unlockReveal?.Hide();
        _viewModel.BeginBackgroundLockCountdown();
        ShowInTaskbar=false; Hide(); UpdateForegroundPolicy(); ScheduleViewRelease();
    }
    public void ShowFromTray()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowFromTray); return; }
        EnsureViewResources(); CloseQuickAccounts(); ShowInTaskbar=true;
        WindowState=WindowState.Normal;
        Show(); Activate(); Focus(); UpdateForegroundPolicy();
        _viewModel.CancelBackgroundLockCountdown();
        FocusUnlockPassword();
    }
    public void ShowQuickAccounts()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowQuickAccounts); return; }
        if(_exitRequested||Dispatcher.HasShutdownStarted) return;
        _viewModel.RecordAppActivity();
        if(_viewModel.IsLocked) { ShowFromTray(); return; }
        if(_quickAccounts?.IsVisible==true) { _quickAccounts.Activate(); return; }
        CloseQuickAccounts();
        var panel=new QuickAccountWindow(_viewModel,this) { SwitchRequestHandler=RequestWidgetSwitchAsync };
        panel.PreviewMouseDown+=(_,_)=>_viewModel.RecordAppActivity();
        panel.PreviewMouseWheel+=(_,_)=>_viewModel.RecordAppActivity();
        panel.PreviewKeyDown+=(_,_)=>_viewModel.RecordAppActivity();
        _quickAccounts=panel;
        panel.Closed+=(_,_)=> { if(ReferenceEquals(_quickAccounts,panel)) _quickAccounts=null; UpdateForegroundPolicy(); ScheduleViewRelease(); };
        var work=SystemParameters.WorkArea;
        var bounds=_widget?.GetChooserPlacement(new Size(660,410))??new Rect(work.Left+(work.Width-660)/2,Math.Max(work.Top,work.Bottom-418),Math.Min(660,work.Width),Math.Min(410,work.Height));
        panel.MinWidth=Math.Min(panel.MinWidth,bounds.Width); panel.MinHeight=Math.Min(panel.MinHeight,bounds.Height);
        _viewReleaseTimer.Stop(); panel.ShowAtTaskbar(bounds); UpdateForegroundPolicy();
        if(_widget is not null) _widget.PositionChooser(panel,_widget.GetChooserPlacementDetails(new Size(660,410)));
    }
    private TaskbarDockPosition ReadTaskbarDockPosition()=>_viewModel.TaskbarWidgetDock=="left"?TaskbarDockPosition.Left:TaskbarDockPosition.Tray;
    private void OnTaskbarPreferenceChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainViewModel.IsLocked)) _unlockReveal?.Hide();
        if(e.PropertyName==nameof(MainViewModel.IsLocked)&&_viewModel.IsLocked)
        {
            CloseQuickAccounts(); _securityDialog?.Close();
            _unlockReveal?.Hide();
            if(UnlockPassword is not null) UnlockPassword.Clear();
            FocusUnlockPassword();
        }
        else if(e.PropertyName==nameof(MainViewModel.IsLocked)&&!_viewModel.IsLocked&&_pendingWidgetSwitch is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async()=>await ResumePendingWidgetSwitchAsync()));
        if(e.PropertyName==nameof(MainViewModel.IsResetting)&&_viewModel.IsResetting) _pendingWidgetSwitch=null;
        if(e.PropertyName is nameof(MainViewModel.IsGameRunning) or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.PendingConfirmation)
            or nameof(MainViewModel.IsAddAccountOpen) or nameof(MainViewModel.HasManagementDialog)) ScheduleViewRelease();
        if(_widget is null) return;
        if(e.PropertyName==nameof(MainViewModel.TaskbarWidgetEnabled))
        {
            if(!_viewModel.TaskbarWidgetEnabled) CloseQuickAccounts();
            _widget.SetEnabled(_viewModel.TaskbarWidgetEnabled);
        }
        else if(e.PropertyName==nameof(MainViewModel.TaskbarWidgetDock)) _widget.SetDockPosition(ReadTaskbarDockPosition());
        else if(e.PropertyName==nameof(MainViewModel.IsGameRunning))
            _widget.SetTemporarilySuspended(_viewModel.IsGameRunning);
    }
    private void OnTaskbarDockPositionCommitted(object? sender,TaskbarDockPositionCommittedEventArgs e)
    {
        if(!_persistTaskbarPosition) return;
        _viewModel.SetTaskbarWidgetDockPosition(e.Position==TaskbarDockPosition.Left?"left":"tray");
        // A failed atomic settings save leaves the old preference intact; restore its anchor too.
        if(_widget is not null&&ReadTaskbarDockPosition()!=e.Position) _widget.SetDockPosition(ReadTaskbarDockPosition());
    }
    private void OnTaskbarPlacementChanged(object? sender,EventArgs e)
    {
        if(_widget is not null&&_quickAccounts?.IsVisible==true)
            _widget.PositionChooser(_quickAccounts,_widget.GetChooserPlacementDetails(new Size(660,410)));
    }
    public void CloseQuickAccounts()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CloseQuickAccounts); return; }
        var panel=_quickAccounts; _quickAccounts=null; panel?.Close(); UpdateForegroundPolicy(); ScheduleViewRelease();
    }
    private bool CanReleaseGameView=>!_exitRequested&&_viewModel.IsGameRunning&&!IsVisible&&!QuickAccountsVisible
        &&!_viewModel.IsBusy&&!_viewModel.PendingConfirmation&&!_viewModel.IsAddAccountOpen&&!_viewModel.HasManagementDialog;
    private void ScheduleViewRelease()
    {
        _viewReleaseTimer.Stop();
        if(!_viewModel.IsDemo&&!_viewResourcesReleased&&CanReleaseGameView) _viewReleaseTimer.Start();
    }
    private bool ReleaseGameViewResources()
    {
        if(_viewResourcesReleased||!CanReleaseGameView) return false;
        // All editable state is held by the existing view model. Drop only the hidden visual resources.
        if(Keyboard.FocusedElement is DependencyObject focused&&ReferenceEquals(Window.GetWindow(focused),this))
            Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(this,null);
        var oldView=Content as DependencyObject;
        Content=null;
        // Hidden content presenters can retain the previous visual until layout resumes.
        // Tear down this window's template too; opening the view restores its theme template.
        Template=null;
        NameScope.SetNameScope(this,null);
        // A BAML dictionary cannot be reinitialized, even after Clear. Discard its deferred content too.
        Resources=new ResourceDictionary();
        TitleBar=null!;
        TitleMinimizeButton=null!;
        TitleCloseButton=null!;
        AboutNavigationButton=null!;
        CurrentSessionText=null!;
        MainRunningGameText=null!;
        AccountSearchBox=null!;
        FolderStrip=null!;
        ActivitySearchBox=null!;
        ClearActivityButton=null!;
        OperationRecordsList=null!;
        TaskbarWidgetToggle=null!;
        TaskbarDockLeftButton=null!;
        TaskbarDockTrayButton=null!;
        GamePauseSettingsNotice=null!;
        ExitApplicationButton=null!;
        AboutPage=null!;
        AboutHeroCard=null!;
        AppVersionText=null!;
        CreatorAvatar=null!;
        CreatorNameText=null!;
        ReleaseNotesList=null!;
        DevelopmentHistoryExpander=null!;
        DevelopmentStagesList=null!;
        FolderDialog=null!;
        AccountEditDialog=null!;
        _unlockReveal?.Dispose(); _unlockReveal=null;
        if(UnlockPassword is not null) UnlockPassword.Clear();
        UnlockPassword=null!; UnlockPasswordPlain=null!; UnlockPasswordReveal=null!;
        UnlockInputGroup=null!; UnlockErrorBanner=null!; UnlockErrorText=null!;
        PasswordHintBanner=null!; PasswordHintText=null!; PasswordHintSettingsButton=null!;
        LockOverlay=null!; ProtectedContent=null!; SecuritySettingsCard=null!; AppLockDelaySelector=null!;
        _contentLoaded=false;
        _viewResourcesReleased=true; _viewReleaseCount++;
        // A cleanup failure must still leave the next Open able to reconstruct this view.
        try { if(oldView is not null) DetachHiddenView(oldView); }
        finally { AvatarImageConverter.TrimMemoryCache(); }
        return true;
    }
    private static void DetachHiddenView(DependencyObject root)
    {
        // An input service may briefly retain one control. Break its path to the rest of this discarded view.
        var nodes=new List<DependencyObject>();
        var seen=new HashSet<DependencyObject>();
        var pending=new Stack<DependencyObject>(); pending.Push(root);
        while(pending.TryPop(out var node))
        {
            if(!seen.Add(node)) continue;
            nodes.Add(node);
            if(node is Visual or System.Windows.Media.Media3D.Visual3D)
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) pending.Push(VisualTreeHelper.GetChild(node,i));
            foreach(var child in LogicalTreeHelper.GetChildren(node)) if(child is DependencyObject dependency) pending.Push(dependency);
        }
        // Detach every binding before resetting selections/content; changes cannot flow back into saved state.
        foreach(var node in nodes) BindingOperations.ClearAllBindings(node);
        foreach(var node in nodes)
        {
            if(node is FrameworkElement element) element.DataContext=null;
            if(node is Image image) image.Source=null;
            if(node is ItemsControl items) items.ItemsSource=null;
            if(node is Panel {IsItemsHost:false} panel) panel.Children.Clear();
            if(node is Decorator decorator) decorator.Child=null;
            if(node is ContentControl content) content.Content=null;
        }
    }
    internal bool ReleaseGameViewResourcesForVerification()
    {
        if(!_viewModel.IsDemo) throw new InvalidOperationException("界面回收验证只允许隔离演示。");
        _viewReleaseTimer.Stop(); return ReleaseGameViewResources();
    }
    private void EnsureViewResources()
    {
        _viewReleaseTimer.Stop();
        if(!_viewResourcesReleased) return;
        double savedWidth=Width,savedHeight=Height,savedLeft=Left,savedTop=Top;
        ClearValue(TemplateProperty);
        InitializeComponent();
        InitializeUnlockReveal();
        Width=savedWidth; Height=savedHeight; Left=savedLeft; Top=savedTop;
        DataContext=_viewModel;
        _viewResourcesReleased=false; _viewRestoreCount++;
    }
    private void UpdateForegroundPolicy()=>_viewModel.SetForegroundInteractive(IsVisible||QuickAccountsVisible);
    public async Task RequestWidgetSwitchAsync(SteamAccount account,SwitchMode mode)
    {
        _viewModel.RecordAppActivity();
        if(_exitRequested||_viewModel.IsBusy||_viewModel.PendingConfirmation||_viewModel.IsAddAccountOpen||_viewModel.HasManagementDialog) return;
        if(!_viewModel.AllAccounts.Any(a=>ReferenceEquals(a,account))||mode==SwitchMode.Offline&&!account.HasCachedLogin) return;
        if(_viewModel.IsLocked)
        {
            _pendingWidgetSwitch=new(account.SteamId,account.AccountName,mode);
            _viewModel.EnsureUnlocked(); return;
        }
        try
        {
            ShowQuickAccounts();
            _widget?.WidgetForVerification.ViewModel?.SelectAccount(account);
            _viewModel.SelectedAccount=account;
            await _viewModel.SelectAndSwitchAsync(account.SteamId,mode);
        }
        catch(Exception ex) { _viewModel.ShowStatus("切换未完成",ex.Message,"error"); }
    }
    private async Task ResumePendingWidgetSwitchAsync()
    {
        if(_viewModel.IsLocked||_pendingWidgetSwitch is not { } request) return;
        _pendingWidgetSwitch=null;
        if(_exitRequested||_viewModel.IsResetting||_viewModel.IsGameRunning) return;
        var matches=_viewModel.AllAccounts.Where(a=>a.AccountName.Equals(request.AccountName,StringComparison.OrdinalIgnoreCase)
            &&a.SteamId==request.SteamId).Take(2).ToArray();
        if(matches.Length!=1) { _viewModel.ShowStatus("账号已变更","请重新选择需要切换的账号。","info"); return; }
        await RequestWidgetSwitchAsync(matches[0],request.Mode);
    }
    public async Task<InstanceResponse> HandleInstanceCommandAsync(InstanceCommand command,bool allowTestCancel=false)
    {
        if(!InstanceMessageService.IsValid(command,allowTestCancel)) return new(false,"invalid-command");
        if(_viewModel.IsLocked&&command.Action is not ("activate" or "exit"))
        { ShowFromTray(); return new(false,"locked"); }
        if(command.Action=="test-cancel")
        {
            if(!allowTestCancel||!_viewModel.IsDemo) return new(false,"invalid-command");
            _viewModel.CancelConfirmCommand.Execute(null); return new(true,"cancelled");
        }
        if(command.Action=="exit") return _viewModel.IsBusy?new(false,"busy"):new(true,"exiting");
        if(command.Action=="activate") { ShowFromTray(); return new(true,"activated"); }
        if(command.Action=="quick-menu") { ShowQuickAccounts(); return new(true,"menu-opened"); }
        if(_viewModel.IsBusy||_viewModel.PendingConfirmation||_viewModel.IsAddAccountOpen||_viewModel.HasManagementDialog) return new(false,"busy");
        var matches=command.Identity!.StartsWith("account:",StringComparison.Ordinal)
            ?_viewModel.AllAccounts.Where(a=>a.AccountName==command.Identity[8..]).Take(2).ToArray()
            :_viewModel.AllAccounts.Where(a=>a.SteamId==command.Identity).Take(2).ToArray();
        if(matches.Length!=1) return new(false,"account-unavailable");
        var account=matches[0];
        if(command.Mode=="offline"&&!account.HasCachedLogin) return new(false,"offline-unavailable");
        await RequestWidgetSwitchAsync(account,command.Mode=="offline"?SwitchMode.Offline:SwitchMode.Online);
        return new(_viewModel.PendingConfirmation&&ReferenceEquals(_viewModel.SelectedAccount,account),_viewModel.PendingConfirmation?"confirmation-requested":"request-unavailable");
    }
    public void RequestExit()
    {
        if(_viewModel.IsBusy) { ShowQuickAccounts(); _viewModel.ShowStatus("请稍候","Steam 正在切换，完成后可退出。","info"); return; }
        _exitRequested=true; Close(); Application.Current.Shutdown();
    }

    public void MinimizeFromTitlebar()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(MinimizeFromTitlebar); return; }
        WindowState=WindowState.Minimized;
    }
    public void CloseFromTitlebar()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CloseFromTitlebar); return; }
        Close();
    }
    private void TitleMinimizeClick(object sender,RoutedEventArgs e)=>MinimizeFromTitlebar();
    private void TitleCloseClick(object sender,RoutedEventArgs e)=>CloseFromTitlebar();
    private void ExitApplicationClick(object sender,RoutedEventArgs e)=>RequestExit();

    private void OnUnlockRequested(object? sender,EventArgs e)=>ShowFromTray();
    private void OnForgotPasswordRequested(object? sender,EventArgs e)
    {
        if(_forgotPasswordDialog is not null||_viewModel.AppLockChecking||_viewModel.IsBusy) return;
        _pendingWidgetSwitch=null; CloseQuickAccounts();
        _unlockReveal?.Hide(); UnlockPassword?.Clear();
        var dialog=new ForgotPasswordDialog(_viewModel) {Owner=this}; _forgotPasswordDialog=dialog;
        try { dialog.ShowDialog(); }
        finally { if(ReferenceEquals(_forgotPasswordDialog,dialog)) _forgotPasswordDialog=null; }
    }
    private void OnWindowsSessionSwitch(object sender,SessionSwitchEventArgs e)
    {
        if(e.Reason==SessionSwitchReason.SessionLock&&!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(()=> { _pendingWidgetSwitch=null; _viewModel.RequestAppLock("windows-session"); }));
    }
    private void FocusUnlockPassword()
    {
        if(IsVisible&&_viewModel.IsLocked&&_forgotPasswordDialog is null&&UnlockPassword is not null&&!_viewModel.AppLockConfigurationError)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=> { if(IsVisible&&_viewModel.IsLocked&&_forgotPasswordDialog is null) UnlockPassword?.Focus(); }));
    }
    private void InitializeUnlockReveal()=>_unlockReveal=new(UnlockPassword,UnlockPasswordPlain,UnlockPasswordReveal);
    public bool HasRevealedUnlockPassword=>_unlockReveal?.HasRevealed==true;
    private async void UnlockClick(object sender,RoutedEventArgs e)
    {
        if(UnlockPassword is null||_viewModel.AppLockChecking) return;
        _unlockReveal?.Hide();
        string password=UnlockPassword.Password; UnlockPassword.Clear();
        bool unlocked=await _viewModel.TryUnlockAsync(password);
        if(!unlocked&&IsVisible&&UnlockInputGroup?.RenderTransform is TranslateTransform transform)
        {
            var shake=new DoubleAnimationUsingKeyFrames {Duration=TimeSpan.FromMilliseconds(240)};
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-5,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(5,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(-3,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(135))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(3,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
            transform.BeginAnimation(TranslateTransform.XProperty,shake);
        }
        FocusUnlockPassword();
    }
    private void UnlockPasswordKeyDown(object sender,KeyEventArgs e)
    { if(e.Key==Key.Enter) { e.Handled=true; UnlockClick(sender,e); } }
    private void OnAppLockSettingsRequested(object? sender,AppLockSettingsRequestedEventArgs e)
    {
        if(_viewModel.IsLocked||_securityDialog is not null) return;
        var dialog=new AppLockDialog(_viewModel,e.Action) {Owner=this}; _securityDialog=dialog;
        try { dialog.ShowDialog(); }
        finally { if(ReferenceEquals(_securityDialog,dialog)) _securityDialog=null; }
    }
    private const int WmSysCommand=0x0112,WmNcLeftDoubleClick=0x00A3,WmStyleChanging=0x007C,WmInitMenu=0x0116;
    private const int ScMaximize=0xF030,HtCaption=2,GwlStyle=-16;
    private const int WsMaximizeBox=0x00010000,WsMinimizeBox=0x00020000,WsThickFrame=0x00040000,WsSystemMenu=0x00080000;
    private void InitializeNativeChrome()
    {
        _chromeHandle=new WindowInteropHelper(this).Handle;
        _chromeSource=HwndSource.FromHwnd(_chromeHandle);
        _chromeSource?.AddHook(ChromeMessageHook);
        int style=GetNativeWindowStyle(_chromeHandle,GwlStyle);
        SetNativeWindowStyle(_chromeHandle,GwlStyle,(style|WsThickFrame|WsSystemMenu|WsMinimizeBox)&~WsMaximizeBox);
        DisableSystemMaximize();
        // A normal HWND with a native frame keeps DWM's shadow and per-monitor DPI behavior.
        int rounded=2,nonClientEnabled=2;
        DwmSetWindowAttribute(_chromeHandle,33,ref rounded,sizeof(int));
        DwmSetWindowAttribute(_chromeHandle,2,ref nonClientEnabled,sizeof(int));
        SetNativeWindowPosition(_chromeHandle,0,0,0,0,0,0x0020|0x0001|0x0002|0x0004|0x0010);
    }
    private void DisableSystemMaximize()
    {
        if(_chromeHandle==0) return;
        var menu=GetNativeSystemMenu(_chromeHandle,false);
        if(menu!=0) RemoveNativeMenu(menu,ScMaximize,0);
    }
    private nint ChromeMessageHook(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(HandleChromeMessage(message,wParam,lParam)) handled=true;
        return 0;
    }
    private bool HandleChromeMessage(int message,nint wParam,nint lParam)
    {
        if(message==WmSysCommand&&((long)wParam&0xFFF0)==ScMaximize)
        { _blockedMaximizeRequests++; return true; }
        if(message==WmNcLeftDoubleClick&&(int)wParam==HtCaption)
        { _blockedCaptionDoubleClicks++; return true; }
        if(message==WmStyleChanging&&(int)wParam==GwlStyle&&lParam!=0)
        {
            // WPF/Windows can rewrite styles while restoring, so keep the maximize box removed.
            int nextStyle=Marshal.ReadInt32(lParam,4);
            Marshal.WriteInt32(lParam,4,nextStyle&~WsMaximizeBox);
        }
        if(message==WmInitMenu) DisableSystemMaximize();
        return false;
    }
    public bool DispatchChromeMessageForVerification(int message,nint wParam,nint lParam=default)=>HandleChromeMessage(message,wParam,lParam);
    public bool RequestNativeMaximizeForVerification()
    {
        if(_chromeHandle==0) return false;
        int previous=_blockedMaximizeRequests;
        SendNativeWindowMessage(_chromeHandle,WmSysCommand,ScMaximize,0);
        return WindowState!=WindowState.Maximized&&_blockedMaximizeRequests>previous;
    }
    public int GetNativeHitTestForVerification(Point windowPoint)
    {
        if(_chromeHandle==0) return 0;
        var point=PointToScreen(windowPoint);
        uint packed=(uint)(ushort)(short)Math.Round(point.X)|((uint)(ushort)(short)Math.Round(point.Y)<<16);
        return (int)SendNativeWindowMessage(_chromeHandle,0x0084,0,unchecked((int)packed));
    }
    private MainWindowChromeVerification CaptureChromeVerification()
    {
        int style=_chromeHandle==0?0:GetNativeWindowStyle(_chromeHandle,GwlStyle);
        var menu=_chromeHandle==0?0:GetNativeSystemMenu(_chromeHandle,false);
        uint menuState=menu==0?uint.MaxValue:GetNativeMenuState(menu,ScMaximize,0);
        int cornerPreference=0;
        if(_chromeHandle!=0) DwmGetWindowAttribute(_chromeHandle,33,out cornerPreference,sizeof(int));
        return new(WindowStyle==WindowStyle.None&&CustomCaptionHeight==44,AllowsTransparency,CustomCaptionHeight,
            _chromeHandle!=0&&(style&WsMaximizeBox)==0,menuState==uint.MaxValue||((menuState&3)!=0),
            _chromeHandle!=0&&(style&WsThickFrame)!=0,cornerPreference,CustomCaptionButtonCount,
            _blockedMaximizeRequests,_blockedCaptionDoubleClicks,menu!=0&&menuState==uint.MaxValue);
    }
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetNativeWindowStyle(nint window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] private static extern int SetNativeWindowStyle(nint window,int index,int value);
    [DllImport("user32.dll",EntryPoint="GetSystemMenu")] private static extern nint GetNativeSystemMenu(nint window,[MarshalAs(UnmanagedType.Bool)]bool revert);
    [DllImport("user32.dll",EntryPoint="RemoveMenu")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveNativeMenu(nint menu,int position,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMenuState")] private static extern uint GetNativeMenuState(nint menu,int id,uint flags);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetNativeWindowPosition(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendNativeWindowMessage(nint window,int message,nint wParam,nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int size);
}

public sealed record MainWindowChromeVerification(bool HasCustomTitleBar,bool UsesPerPixelTransparency,double CaptionHeight,
    bool NativeMaximizeBoxRemoved,bool SystemMaximizeDisabled,bool NativeResizeAvailable,int CornerPreference,
    int CaptionButtonCount,int BlockedMaximizeRequests,int BlockedCaptionDoubleClicks,bool SystemMaximizeEntryRemoved);
