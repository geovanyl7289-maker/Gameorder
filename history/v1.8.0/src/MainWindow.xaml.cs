using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Shell;

namespace Gameorder;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _persistTaskbarPosition;
    private TrayService? _tray;
    private TaskbarWidgetHost? _widget;
    private QuickAccountWindow? _quickAccounts;
    private bool _exitRequested;
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
    public TrayMenuVerification PrepareQuickMenuForVerification()=>_tray?.PrepareMenuForVerification()??new(0,0,0,0,0);
    public void SaveNativeTrayMenuPreviewForVerification(string output)=>_tray?.SaveMenuPreviewForVerification(output);

    public MainWindow(bool demo=false,bool allowAvatarNetwork=true,bool enableShellIntegration=true,bool persistTaskbarPosition=true)
    {
        InitializeComponent();
        _viewModel=new MainViewModel(demo,allowAvatarNetwork);
        DataContext=_viewModel;
        _persistTaskbarPosition=persistTaskbarPosition;
        _viewModel.PropertyChanged+=OnTaskbarPreferenceChanged;
        ShellIntegrationEnabled=enableShellIntegration&&!demo;
        SourceInitialized+=(_,_)=>InitializeNativeChrome();
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { _viewModel.CloseTransient(); e.Handled=true; } };
        Closing+=OnClosing;
        Application.Current.SessionEnding+=OnSessionEnding;
        StateChanged+=(_,_)=>
        {
            // Native commands are blocked below; this also catches direct WPF assignments or snap paths.
            if(WindowState==WindowState.Maximized) { _blockedMaximizeRequests++; WindowState=WindowState.Normal; return; }
            if(WindowState==WindowState.Minimized&&ShellIntegrationEnabled&&!_exitRequested) HideToBackground();
        };
        Loaded+=(_,_)=>
        {
            if(!ShellIntegrationEnabled||_tray is not null) return;
            try { _tray=new TrayService(_viewModel,this); }
            catch(Exception ex) { _tray?.Dispose(); _tray=null; _viewModel.ShowStatus("托盘暂不可用",ex.Message,"info"); }
            try
            {
                _widget=new TaskbarWidgetHost(_viewModel,ShowQuickAccounts,(account,mode)=>_ = RequestWidgetSwitchAsync(account,mode),ShowFromTray);
                _widget.WatchWindow(this); _widget.PlacementChanged+=OnTaskbarPlacementChanged;
                _widget.DockPositionCommitted+=OnTaskbarDockPositionCommitted;
                _widget.SetDockPosition(ReadTaskbarDockPosition());
                _widget.SetEnabled(_viewModel.TaskbarWidgetEnabled);
            }
            catch(Exception ex) { _viewModel.ShowStatus("任务栏控件暂不可用",ex.Message,"info"); }
        };
        Closed+=(_,_)=>
        {
            if(_chromeSource is { IsDisposed:false }) _chromeSource.RemoveHook(ChromeMessageHook);
            _chromeSource=null; _chromeHandle=0;
            _viewModel.PropertyChanged-=OnTaskbarPreferenceChanged;
            CloseQuickAccounts(); _widget?.Dispose(); _widget=null;
            Application.Current.SessionEnding-=OnSessionEnding;
            _tray?.Dispose(); _tray=null; _viewModel.Dispose();
        };
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_exitRequested||Dispatcher.HasShutdownStarted) return;
        if(ShellIntegrationEnabled&&(_tray is not null||_widget is not null))
        { e.Cancel=true; CloseQuickAccounts(); HideToBackground(); return; }
        if(_viewModel.IsBusy) { e.Cancel=true; _viewModel.ShowStatus("请稍候","Steam 正在切换，完成后可退出。","info"); }
    }
    private void OnSessionEnding(object sender,SessionEndingCancelEventArgs e)=>_exitRequested=true;
    public void HideToBackground()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(HideToBackground); return; }
        ShowInTaskbar=false; Hide();
    }
    public void ShowFromTray()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowFromTray); return; }
        CloseQuickAccounts(); ShowInTaskbar=true;
        WindowState=WindowState.Normal;
        Show(); Activate(); Focus();
    }
    public void ShowQuickAccounts()
    {
        if(!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ShowQuickAccounts); return; }
        if(_exitRequested||Dispatcher.HasShutdownStarted) return;
        if(_quickAccounts?.IsVisible==true) { _quickAccounts.Activate(); return; }
        CloseQuickAccounts();
        var panel=new QuickAccountWindow(_viewModel,this) { SwitchRequestHandler=RequestWidgetSwitchAsync };
        _quickAccounts=panel;
        panel.Closed+=(_,_)=> { if(ReferenceEquals(_quickAccounts,panel)) _quickAccounts=null; };
        var work=SystemParameters.WorkArea;
        var bounds=_widget?.GetChooserPlacement(new Size(660,410))??new Rect(work.Left+(work.Width-660)/2,Math.Max(work.Top,work.Bottom-418),Math.Min(660,work.Width),Math.Min(410,work.Height));
        panel.MinWidth=Math.Min(panel.MinWidth,bounds.Width); panel.MinHeight=Math.Min(panel.MinHeight,bounds.Height);
        panel.ShowAtTaskbar(bounds);
        if(_widget is not null) _widget.PositionChooser(panel,_widget.GetChooserPlacementDetails(new Size(660,410)));
    }
    private TaskbarDockPosition ReadTaskbarDockPosition()=>_viewModel.TaskbarWidgetDock=="left"?TaskbarDockPosition.Left:TaskbarDockPosition.Tray;
    private void OnTaskbarPreferenceChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_widget is null) return;
        if(e.PropertyName==nameof(MainViewModel.TaskbarWidgetEnabled))
        {
            if(!_viewModel.TaskbarWidgetEnabled) CloseQuickAccounts();
            _widget.SetEnabled(_viewModel.TaskbarWidgetEnabled);
        }
        else if(e.PropertyName==nameof(MainViewModel.TaskbarWidgetDock)) _widget.SetDockPosition(ReadTaskbarDockPosition());
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
        var panel=_quickAccounts; _quickAccounts=null; panel?.Close();
    }
    public async Task RequestWidgetSwitchAsync(SteamAccount account,SwitchMode mode)
    {
        if(_exitRequested||_viewModel.IsBusy||_viewModel.PendingConfirmation||_viewModel.IsAddAccountOpen||_viewModel.HasManagementDialog) return;
        if(!_viewModel.AllAccounts.Any(a=>ReferenceEquals(a,account))||mode==SwitchMode.Offline&&!account.HasCachedLogin) return;
        try
        {
            ShowQuickAccounts();
            _widget?.WidgetForVerification.ViewModel?.SelectAccount(account);
            _viewModel.SelectedAccount=account;
            await _viewModel.SelectAndSwitchAsync(account.SteamId,mode);
        }
        catch(Exception ex) { _viewModel.ShowStatus("切换未完成",ex.Message,"error"); }
    }
    public async Task<InstanceResponse> HandleInstanceCommandAsync(InstanceCommand command,bool allowTestCancel=false)
    {
        if(!InstanceMessageService.IsValid(command,allowTestCancel)) return new(false,"invalid-command");
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
