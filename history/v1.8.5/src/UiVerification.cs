using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Windows.Input;

namespace Gameorder;

internal static class UiVerification
{
    private static readonly List<string> BindingErrors = [];
    public static void EnableBindingChecks()
    {
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
    }
    private static void CheckBindings(string output)
    {
        File.WriteAllText(Path.Combine(output,"binding-errors.json"),JsonSerializer.Serialize(BindingErrors));
        if(BindingErrors.Count>0) throw new InvalidOperationException("界面绑定错误："+string.Join("\n",BindingErrors));
    }
    private sealed class BindingErrorListener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
        public override void TraceEvent(TraceEventCache? cache,string source,TraceEventType type,int id,string? message)
        { if(type is TraceEventType.Error or TraceEventType.Critical) BindingErrors.Add(message??"未知绑定错误"); }
        public override void TraceEvent(TraceEventCache? cache,string source,TraceEventType type,int id,string? format,params object?[]? args)
        { TraceEvent(cache,source,type,id,args is {Length:>0} ? string.Format(System.Globalization.CultureInfo.InvariantCulture,format??"",args) : format); }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakViewReference(MainWindow window)
        =>new(window.Content??throw new InvalidOperationException("主界面内容尚未建立"));
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference FocusSearchForVerification(MainWindow window)
    {
        var search=(TextBox)window.FindName("AccountSearchBox");
        search.Focus(); Keyboard.Focus(search); FocusManager.SetFocusedElement(window,search);
        return new(search);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateWeakNamedViewReference(MainWindow window,string name)
        =>new(window.FindName(name)??throw new InvalidOperationException("验证控件未创建："+name));
    private sealed record ViewDetachmentSnapshot(bool RootCollected,int RootVisualChildren,int RootLogicalControlChildren,
        bool RootResourcesCleared,bool RootDataContextCleared,bool CreatorCollected,bool CreatorImageSourceCleared,
        bool SearchCollected,bool SearchParentsDetached,int SearchLocalBindingCount,bool SearchDataContextCleared);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static ViewDetachmentSnapshot CaptureDetachedViewSnapshot(WeakReference rootReference,WeakReference creatorReference,WeakReference searchReference)
    {
        var root=rootReference.Target as FrameworkElement;
        var creator=creatorReference.Target as Image;
        var search=searchReference.Target as TextBox;
        int bindings=0;
        if(search is not null)
        {
            var values=search.GetLocalValueEnumerator();
            while(values.MoveNext()) if(System.Windows.Data.BindingOperations.IsDataBound(search,values.Current.Property)) bindings++;
        }
        // Grid row/column definitions may remain; they are layout data, not retained child controls.
        return new(root is null,root is null?0:VisualTreeHelper.GetChildrenCount(root),
            root is null?0:LogicalTreeHelper.GetChildren(root).OfType<UIElement>().Count(),
            root is null||root.Resources.Count==0,root?.DataContext is null,
            creator is null,creator?.Source is null,search is null,
            search is null||(VisualTreeHelper.GetParent(search) is null&&LogicalTreeHelper.GetParent(search) is null),
            bindings,search?.DataContext is null);
    }
    private static async Task CapturePanel(Window window,string path)
    {
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var root=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path); encoder.Save(stream);
    }
    public static async Task RunQuick(MainWindow window,string output)
    {
        Directory.CreateDirectory(output);
        var vm=(MainViewModel)window.DataContext;
        var checks=new List<string>();
        void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); checks.Add(message); }
        async Task Idle()=>await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        QuickAccountWindow Panel()
        {
            var panel=window.QuickAccountsWindowForVerification??throw new InvalidOperationException("头像栏未创建。");
            panel.DismissOnDeactivate=false; return panel;
        }
        Check(vm.IsDemo&&!window.ShellIntegrationEnabled,"控件验证采用隔离演示账号");
        var dockLayoutProofs=new List<object>();
        var dockCases=new[]
        {
            (Name:"应用、音乐控件和托盘均受保护",Bar:new Rect(0,0,1600,48),Tray:new Rect(1350,0,250,48),Scale:1d,
                Occupied:new[]{new Rect(0,0,220,48),new Rect(550,0,400,48),new Rect(1350,0,250,48)},HasTray:true,HasLeft:true),
            (Name:"共享380像素空位保留两个可辨识锚点",Bar:new Rect(0,0,900,48),Tray:new Rect(696,0,204,48),Scale:1d,
                Occupied:new[]{new Rect(0,0,300,48),new Rect(696,0,204,48)},HasTray:true,HasLeft:true),
            (Name:"重叠保护区域取并集",Bar:new Rect(0,0,1000,48),Tray:new Rect(860,0,140,48),Scale:1d,
                Occupied:new[]{new Rect(0,0,300,48),new Rect(250,0,200,48),new Rect(860,0,140,48)},HasTray:true,HasLeft:true),
            (Name:"150%缩放和负坐标显示器",Bar:new Rect(-1920,-72,1920,72),Tray:new Rect(-300,-72,300,72),Scale:1.5d,
                Occupied:new[]{new Rect(-1920,-72,540,72),new Rect(-1000,-72,350,72),new Rect(-300,-72,300,72)},HasTray:true,HasLeft:true),
            (Name:"没有80DIP安全空位则暂停",Bar:new Rect(0,0,1000,48),Tray:new Rect(860,0,140,48),Scale:1d,
                Occupied:new[]{new Rect(0,0,420,48),new Rect(500,0,500,48)},HasTray:false,HasLeft:false),
            (Name:"整条任务栏被占用则暂停",Bar:new Rect(0,0,1000,48),Tray:new Rect(860,0,140,48),Scale:1d,
                Occupied:new[]{new Rect(0,0,1000,48)},HasTray:false,HasLeft:false),
            (Name:"未知托盘位置不猜测托盘锚点",Bar:new Rect(0,0,1000,48),Tray:Rect.Empty,Scale:1d,
                Occupied:new[]{new Rect(0,0,220,48)},HasTray:false,HasLeft:true),
            (Name:"竖向任务栏不覆盖控件",Bar:new Rect(0,0,48,1000),Tray:new Rect(0,900,48,100),Scale:1d,
                Occupied:Array.Empty<Rect>(),HasTray:false,HasLeft:false),
            (Name:"过低任务栏不挤压控件",Bar:new Rect(0,0,1000,20),Tray:new Rect(860,0,140,20),Scale:1d,
                Occupied:Array.Empty<Rect>(),HasTray:false,HasLeft:false)
        };
        static bool PositiveOverlap(Rect first,Rect second)
        {
            var intersection=Rect.Intersect(first,second);
            return !intersection.IsEmpty&&intersection.Width>0&&intersection.Height>0;
        }
        foreach(var fixture in dockCases)
        {
            var slots=TaskbarWidgetDockLayout.FindSlots(fixture.Bar,fixture.Occupied,fixture.Tray,fixture.Scale);
            var protectedRects=fixture.Occupied.Append(fixture.Tray).Where(rect=>!rect.IsEmpty).ToArray();
            Check((slots.Tray is not null)==fixture.HasTray&&(slots.Left is not null)==fixture.HasLeft&&
                slots.Available.All(slot=>fixture.Bar.Contains(slot.BoundsPixels)&&slot.BoundsPixels.Width>=80*fixture.Scale&&
                    slot.BoundsPixels.Width<=360*fixture.Scale&&slot.BoundsPixels.Height>=24*fixture.Scale&&
                    protectedRects.All(rect=>!PositiveOverlap(rect,slot.BoundsPixels))),"双停靠点布局："+fixture.Name);
            Check(slots.Tray is null||slots.Tray.Position==TaskbarDockPosition.Tray,"托盘锚点保留明确停靠身份："+fixture.Name);
            Check(slots.Left is null||slots.Left.Position==TaskbarDockPosition.Left,"左侧锚点保留明确停靠身份："+fixture.Name);
            if(fixture.Name.StartsWith("共享380",StringComparison.Ordinal))
                Check(slots.Tray is not null&&slots.Left is not null&&slots.Tray.BoundsPixels!=slots.Left.BoundsPixels&&
                    slots.Tray.BoundsPixels.Width>=200&&slots.Left.BoundsPixels.Width>=200&&
                    Math.Abs(slots.Tray.CenterPixels.X-slots.Left.CenterPixels.X)>=160,
                    "共享空位的两锚点至少200DIP宽且中心相距160DIP");
            if(fixture.Name.StartsWith("重叠保护",StringComparison.Ordinal))
            {
                var reverse=TaskbarWidgetDockLayout.FindSlots(fixture.Bar,fixture.Occupied.Reverse().ToArray(),fixture.Tray,fixture.Scale);
                Check(slots.Left is not null&&slots.Left.BoundsPixels.Left>=458&&slots.Tray?.BoundsPixels==reverse.Tray?.BoundsPixels&&
                    slots.Left.BoundsPixels==reverse.Left?.BoundsPixels,"保护区并集不依赖输入顺序且保留8像素间隔");
            }
            if(fixture.Scale==1.5d)
                Check(slots.Available.All(slot=>slot.BoundsPixels.Left<0&&slot.BoundsPixels.Top<0&&
                    Math.Abs(slot.BoundsPixels.Height/fixture.Scale-46)<0.01),"负坐标150%缩放按物理像素计算并保留46DIP高度");
            dockLayoutProofs.Add(new {fixture.Name,fixture.Scale,Tray=slots.Tray,Left=slots.Left});
        }
        foreach(double scale in new[]{0d,-1d,double.NaN,double.PositiveInfinity,double.NegativeInfinity})
        {
            var slots=TaskbarWidgetDockLayout.FindSlots(new Rect(0,0,1000,48),Array.Empty<Rect>(),new Rect(860,0,140,48),scale);
            Check(!slots.Available.Any(),"非法DPI比例拒绝布局："+scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
            dockLayoutProofs.Add(new {Name="非法DPI比例",Scale=scale.ToString(System.Globalization.CultureInfo.InvariantCulture),Available=slots.Available.Count()});
        }
        File.WriteAllText(Path.Combine(output,"dock-layout-result.json"),JsonSerializer.Serialize(new {Passed=true,Cases=dockLayoutProofs},new JsonSerializerOptions {WriteIndented=true}));

        var cached=vm.AllAccounts.First(a=>a.HasCachedLogin);
        var uncached=vm.AllAccounts.First(a=>!a.HasCachedLogin);
        vm.CreateFolderCommand.Execute(null); vm.FolderDraftName="常用账号"; vm.SaveFolderCommand.Execute(null);
        string folderId=vm.ActiveFolderId;
        vm.SelectedAccount=cached; vm.EditAccountCommand.Execute(null);
        vm.EditDisplayName="Northwind"; vm.EditNote="朋友联机 · 周末常用";
        vm.EditFolderChoice=vm.FolderChoices.First(f=>f.Id==folderId); vm.SaveEditAccountCommand.Execute(null);
        Check(cached.Note.Contains("朋友联机")&&cached.FolderId==folderId,"演示备注与分组准备成功");
        using var widget=new TaskbarAccountWidget(vm);
        widget.ConfigureSize(360,48); widget.SetTaskbarTheme(true);
        var widgetModel=widget.ViewModel!;
        window.HideToBackground();
        var previewSurface=new Grid { Background=new SolidColorBrush(Color.FromRgb(83,47,73)) };
        previewSurface.Children.Add(widget);
        var preview=new Window {Content=previewSurface,Width=360,Height=48,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        preview.Show(); await Idle();
        async Task VerifyWidgetGestures()
        {
            var gestureProofs=new List<object>();
            int opens=0,managers=0,switches=0,started=0,moved=0,completed=0,canceled=0;
            EventHandler onOpen=(_,_)=>opens++;
            EventHandler onManager=(_,_)=>managers++;
            EventHandler<TaskbarAccountSwitchEventArgs> onSwitch=(_,_)=>switches++;
            EventHandler<TaskbarWidgetDragEventArgs> onStart=(_,_)=>started++;
            EventHandler<TaskbarWidgetDragEventArgs> onMove=(_,_)=>moved++;
            EventHandler<TaskbarWidgetDragEventArgs> onComplete=(_,_)=>completed++;
            EventHandler onCancel=(_,_)=>canceled++;
            widget.OpenAccountsRequested+=onOpen; widget.OpenManagerRequested+=onManager; widget.SwitchRequested+=onSwitch;
            widget.DragStarted+=onStart; widget.DragMoved+=onMove; widget.DragCompleted+=onComplete; widget.DragCanceled+=onCancel;
            static IEnumerable<FrameworkElement> Elements(DependencyObject parent)
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                {
                    var child=VisualTreeHelper.GetChild(parent,i);
                    if(child is FrameworkElement element) yield return element;
                    foreach(var nested in Elements(child)) yield return nested;
                }
            }
            Rect Bounds(FrameworkElement element)=>new(element.TranslatePoint(new Point(),widget),element.RenderSize);
            void NoActions(string reason)=>Check(opens==0&&managers==0&&switches==0&&!vm.PendingConfirmation&&!vm.IsBusy,reason);
            try
            {
                var source=PresentationSource.FromVisual(widget) as System.Windows.Interop.HwndSource;
                Check(source is not null&&source.Handle!=0&&preview.IsVisible,"拖动验证使用程序自有可见预览HWND");
                foreach(double width in new[]{80d,200d,360d})
                {
                    widget.ConfigureSize(width,48); preview.Width=width; await Idle(); preview.UpdateLayout(); widget.UpdateLayout();
                    var grip=(Border)widget.FindName("DragGrip");
                    var buttons=Elements(widget).OfType<Button>().Where(button=>button.IsVisible).ToArray();
                    var area=new Rect(-0.5,-0.5,widget.ActualWidth+1,widget.ActualHeight+1);
                    Check(Math.Abs(widget.ActualWidth-width)<0.5&&grip.IsVisible&&Math.Abs(grip.ActualWidth-8)<0.01&&
                        area.Contains(Bounds(grip))&&buttons.Length>=2&&buttons.All(button=>button.ActualWidth>0&&area.Contains(Bounds(button))),
                        $"{width:0}DIP宽度保留握柄和完整按钮命中区域");
                    Check(width==80?!widget.ShowLabels&&!widget.ShowSwitchControls:widget.ShowLabels&&widget.ShowSwitchControls,
                        $"{width:0}DIP宽度使用对应紧凑级别");
                    gestureProofs.Add(new {Scenario="layout",WidthDip=width,Grip=Bounds(grip),Buttons=buttons.Select(Bounds).ToArray()});
                }
                widget.ConfigureSize(360,48); preview.Width=360; await Idle(); preview.UpdateLayout(); widget.UpdateLayout();
                var avatar=(Button)widget.FindName("AccountAvatarButton");
                var avatarGlyph=Elements(avatar).OfType<TextBlock>().First();
                avatarGlyph.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                    {RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
                Check(!widget.HasDragCandidate&&!widget.IsDragging,"按钮内部原始source沿父级识别为交互元素并拒绝拖动");
                var dragGrip=(Border)widget.FindName("DragGrip");
                Point press=dragGrip.PointToScreen(new Point(dragGrip.ActualWidth/2,dragGrip.ActualHeight/2));
                Check(!widget.RequestDragPressForVerification(press,true)&&!widget.HasDragCandidate,"交互源验证路由不建立拖动候选");
                Check(widget.RequestDragPressForVerification(press)&&widget.HasDragCandidate,"空白握柄按下建立候选");
                var threshold=widget.DragVerification;
                var small=press+new Vector(threshold.HorizontalThresholdPixels/2,threshold.VerticalThresholdPixels/2);
                Check(!widget.RequestDragMoveForVerification(small)&&!widget.IsDragging&&!widget.IsMouseCaptured&&started==0&&moved==0,
                    "系统阈值以内的小移动不抓取鼠标或开始拖动");
                Check(!widget.RequestSwitch(SwitchMode.Online),"候选按下期间按钮切换请求被阻止");
                widget.RequestOpenAccounts(); widget.RequestOpenManager();
                Check(!widget.RequestDragReleaseForVerification(small)&&completed==0&&canceled==0&&widget.DragVerification.ButtonsEnabled,
                    "未越过阈值的松手仅恢复交互而不发完成事件");
                NoActions("空白轻点没有展开账号栏、管理窗口或发出切换");
                gestureProofs.Add(new {Scenario="below-threshold",State=widget.DragVerification});
                Point far=press+new Vector(threshold.HorizontalThresholdPixels+2,threshold.VerticalThresholdPixels+2);
                bool pressAccepted=widget.RequestDragPressForVerification(press);
                bool dragAdvanced=pressAccepted&&widget.RequestDragMoveForVerification(far);
                File.WriteAllText(Path.Combine(output,"widget-drag-capture-diagnostic.json"),JsonSerializer.Serialize(new {
                    PressAccepted=pressAccepted,DragAdvanced=dragAdvanced,State=widget.DragVerification,
                    WidgetIsDragging=widget.IsDragging,WidgetIsMouseCaptured=widget.IsMouseCaptured,
                    CaptureOwnedByWidget=ReferenceEquals(Mouse.Captured,widget),MouseCaptureOwnerType=Mouse.Captured?.GetType().FullName,
                    AvatarIsEnabled=avatar.IsEnabled,Started=started,Moved=moved,Completed=completed,Canceled=canceled,
                    MouseLeftButton=Mouse.LeftButton.ToString(),PreviewIsActive=preview.IsActive,
                    ExternalInputInjected=false},new JsonSerializerOptions {WriteIndented=true}));
                Check(pressAccepted&&dragAdvanced,"超过系统阈值后开始真实鼠标捕获");
                Check(widget.IsDragging,"活动拖动状态保持");
                Check(widget.IsMouseCaptured&&ReferenceEquals(Mouse.Captured,widget),"活动拖动持有自身WPF鼠标捕获");
                Check(!widget.DragVerification.ButtonsEnabled&&!avatar.IsEnabled,"活动拖动禁用实际按钮");
                Check(started==1&&moved==1,"单次阈值转换只发送一次Started和Move");
                Check(!widget.RequestSwitch(SwitchMode.Online)&&!widget.RequestSwitch(SwitchMode.Offline),"活动拖动阻止在线和离线切换");
                widget.RequestOpenAccounts(); widget.RequestOpenManager(); NoActions("活动拖动不打开面板或请求切换");
                gestureProofs.Add(new {Scenario="dragging",State=widget.DragVerification});
                Check(widget.RequestDragReleaseForVerification(far)&&!widget.RequestDragReleaseForVerification(far)&&
                    completed==1&&canceled==0&&!widget.IsDragging&&!widget.IsMouseCaptured&&widget.DragVerification.ButtonsEnabled,
                    "松手释放自身捕获且完成事件只触发一次");
                Check(widget.RequestDragPressForVerification(press)&&widget.RequestDragMoveForVerification(far),"Esc案例进入活动拖动");
                widget.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source!,Environment.TickCount,Key.Escape)
                    {RoutedEvent=UIElement.PreviewKeyDownEvent});
                widget.RequestDragEscapeForVerification();
                Check(!widget.IsDragging&&!widget.IsMouseCaptured&&completed==1&&canceled==1&&widget.DragVerification.ButtonsEnabled,
                    "自身Esc路由取消拖动且重复取消不重复发事件");
                Check(widget.RequestDragPressForVerification(press)&&widget.RequestDragMoveForVerification(far),"丢失捕获案例进入活动拖动");
                widget.ReleaseMouseCapture(); widget.RequestDragEscapeForVerification();
                Check(!widget.IsDragging&&!widget.IsMouseCaptured&&completed==1&&canceled==2&&widget.DragVerification.ButtonsEnabled,
                    "真实释放自身捕获触发取消且不误发完成");
                int movedBeforeReject=moved;
                EventHandler<TaskbarWidgetDragEventArgs> rejectStart=(_,_)=>widget.CancelDragGesture();
                widget.DragStarted+=rejectStart;
                try { Check(widget.RequestDragPressForVerification(press)&&!widget.RequestDragMoveForVerification(far),"宿主拒绝Started时同步取消控件手势"); }
                finally { widget.DragStarted-=rejectStart; }
                Check(moved==movedBeforeReject&&canceled==3&&!widget.IsDragging&&!widget.IsMouseCaptured,
                    "宿主拒绝后不继续发送Move且恢复捕获状态");
                widget.EnableInteraction(false);
                Check(!widget.RequestDragPressForVerification(press)&&!widget.RequestSwitch(SwitchMode.Online)&&!avatar.IsEnabled,
                    "停用交互期间拒绝新的拖动及切换请求");
                widget.RequestOpenAccounts(); widget.RequestOpenManager(); widget.EnableInteraction(true);
                NoActions("全部拖动、取消和停用案例均未触发业务动作");
                gestureProofs.Add(new {Scenario="completed-and-canceled",Started=started,Moved=moved,Completed=completed,Canceled=canceled,State=widget.DragVerification});
                File.WriteAllText(Path.Combine(output,"widget-drag-result.json"),JsonSerializer.Serialize(new {Passed=true,OwnHwnd=source!.Handle.ToInt64(),ExternalInputInjected=false,
                    OpenRequests=opens,ManagerRequests=managers,SwitchRequests=switches,Cases=gestureProofs},new JsonSerializerOptions {WriteIndented=true}));
            }
            finally
            {
                widget.CancelDragGesture(); widget.EnableInteraction(true);
                widget.OpenAccountsRequested-=onOpen; widget.OpenManagerRequested-=onManager; widget.SwitchRequested-=onSwitch;
                widget.DragStarted-=onStart; widget.DragMoved-=onMove; widget.DragCompleted-=onComplete; widget.DragCanceled-=onCancel;
                widget.ConfigureSize(360,48); preview.Width=360;
            }
        }
        try
        {
            Check(ReferenceEquals(widgetModel.SelectedAccount,cached)&&widgetModel.IsOnline,"控件初始选择本机当前在线账号");
            Check(widgetModel.Note==cached.NotePreview,"迷你控件直接显示备注");
            var mainSelection=vm.SelectedAccount;
            widgetModel.NextCommand.Execute(null);
            Check(!ReferenceEquals(widgetModel.SelectedAccount,cached)&&ReferenceEquals(vm.SelectedAccount,mainSelection)&&!vm.PendingConfirmation,"下一账号只改变任务栏预览，不启动切换或操控管理窗口");
            widgetModel.PreviousCommand.Execute(null);
            Check(ReferenceEquals(widgetModel.SelectedAccount,cached),"上一个账号精确返回原账号");
            Check(!widgetModel.SelectAccount(new SteamAccount {AccountName=cached.AccountName,SteamId=cached.SteamId}),"控件拒绝外来或失效账号对象");
            Check(widgetModel.SelectAccount(uncached)&&!widgetModel.CanOffline&&!widget.RequestSwitch(SwitchMode.Offline),"无本机缓存的账号禁用离线直控");
            widgetModel.SelectAccount(cached);
            await VerifyWidgetGestures();
            await CapturePanel(preview,Path.Combine(output,"01-widget-transparent.png"));
            var frame=(Border)widget.FindName("WidgetFrame");
            frame.Background=(Brush)widget.Resources["WidgetHover"];
            await CapturePanel(preview,Path.Combine(output,"02-widget-selected.png"));
            frame.ClearValue(Border.BackgroundProperty);
            widget.ConfigureSize(220,48); preview.Width=220; await CapturePanel(preview,Path.Combine(output,"03-widget-compact.png"));
            Check(widget.ShowLabels&&widget.ShowSwitchControls&&!widget.ShowNavigation,"紧凑任务栏控件保留头像、备注和在线离线直控");
            widget.ConfigureSize(80,48); preview.Width=80; await CapturePanel(preview,Path.Combine(output,"04-widget-small.png"));
            Check(!widget.ShowLabels&&!widget.ShowSwitchControls&&widget.ActualWidth<=80,"空间较小时使用真正的小控件入口");
            widget.ConfigureSize(360,48); preview.Width=360; widget.SetTaskbarTheme(false); previewSurface.Background=new SolidColorBrush(Color.FromRgb(232,235,242));
            await CapturePanel(preview,Path.Combine(output,"05-widget-light.png"));
            widget.OpenAccountsRequested+=(_,_)=>window.ShowQuickAccounts();
            Task requested=Task.CompletedTask;
            widget.SwitchRequested+=(_,e)=>requested=window.RequestWidgetSwitchAsync(e.Account,e.Mode);
            widget.RequestOpenAccounts(); await Idle(); var popup=Panel();
            Check(!window.IsVisible&&!window.ShowInTaskbar&&popup.VisibleAccountCount==3,"从任务栏控件展开账号栏时管理窗口保持隐藏");
            await CapturePanel(popup,Path.Combine(output,"06-account-chooser.png"));
            popup.ViewModel.SearchText="朋友联机";
            Check(popup.VisibleAccountCount==1&&ReferenceEquals(popup.ViewModel.VisibleAccounts[0],cached),"快捷栏备注搜索保持精确账号");
            popup.ViewModel.SearchText=""; popup.ViewModel.SelectFolder(folderId);
            Check(popup.VisibleAccountCount==1,"快捷栏支持软件内分组筛选");
            popup.ViewModel.SelectFolder("*"); popup.ViewModel.SearchText="__no_match__";
            Check(popup.ViewModel.IsEmpty,"无匹配账号显示空状态"); popup.ViewModel.SearchText="";
            Check(widget.RequestSwitch(SwitchMode.Online),"迷你控件在线按钮发出请求"); await requested; await Idle();
            Check(vm.PendingConfirmation&&popup.ConfirmationVisible&&!window.IsVisible&&window.QuickAccountsVisible&&!vm.IsBusy,"切换确认显示在快捷面板内，不打开管理窗口");
            await CapturePanel(popup,Path.Combine(output,"07-inline-confirmation.png"));
            vm.CancelConfirmCommand.Execute(null);
            await popup.RequestSwitchAsync(uncached,SwitchMode.Offline);
            Check(!vm.PendingConfirmation,"快捷栏再次拒绝无缓存离线请求");
            await popup.RequestSwitchAsync(cached,SwitchMode.Offline);
            Check(vm.PendingConfirmation&&popup.ConfirmationVisible&&!window.IsVisible&&ReferenceEquals(vm.SelectedAccount,cached),"缓存账号离线确认也在快捷栏内完成");
            vm.CancelConfirmCommand.Execute(null);
            window.CloseQuickAccounts();
            for(int i=0;i<25;i++)
            {
                vm.AddAccountCommand.Execute(null); vm.NewAccountName=$"widget_manual_{i:00}"; vm.NewDisplayName=$"演示账号 {i+1:00}"; vm.SaveNewAccountCommand.Execute(null);
                vm.NoteDraft=$"测试备注 {i+1:00}"; vm.SaveNoteCommand.Execute(null);
            }
            var manual=vm.SelectedAccount!;
            window.ShowQuickAccounts(); await Idle(); popup=Panel();
            Check(popup.VisibleAccountCount==28&&popup.ScrollableHeightForVerification>1,"大量账号在头像栏内滚动");
            popup.ScrollToEndForVerification(); await Idle();
            Check(Math.Abs(popup.ScrollOffsetForVerification-popup.ScrollableHeightForVerification)<1,"最后一个账号可达");
            await CapturePanel(popup,Path.Combine(output,"08-many-accounts.png"));
            await popup.RequestSwitchAsync(manual,SwitchMode.Online);
            Check(vm.PendingConfirmation&&ReferenceEquals(vm.SelectedAccount,manual)&&!window.IsVisible,"空 SteamID 手动账号精确选择且不打开管理窗口");
            popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(popup)!,0,Key.Escape) {RoutedEvent=UIElement.PreviewKeyDownEvent});
            Check(!vm.PendingConfirmation&&window.QuickAccountsVisible,"Esc 取消当前确认而保留账号栏");
            popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(popup)!,0,Key.Escape) {RoutedEvent=UIElement.PreviewKeyDownEvent}); await Idle();
            Check(!window.QuickAccountsVisible&&!window.IsVisible,"再次 Esc 收起账号栏，管理窗口继续隐藏");
            widget.OpenManagerRequested+=(_,_)=>window.ShowFromTray(); widget.RequestOpenManager();
            Check(window.IsVisible&&window.ShowInTaskbar,"明确的管理入口打开完整窗口");
            Check(!vm.IsBusy&&!vm.PendingConfirmation&&!vm.Activity.Any(entry=>entry.EffectiveCategory=="login"),"测试没有确认或执行任何 Steam 切换");
            CheckBindings(output);
            File.WriteAllText(Path.Combine(output,"quick-result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks,RealSteamStarted=false,SteamConfigurationChanged=false,PhysicalTaskbarClickTested=false},new JsonSerializerOptions {WriteIndented=true}));
        }
        finally { previewSurface.Children.Clear(); preview.Content=null; preview.Close(); window.CloseQuickAccounts(); }
    }
    public static async Task RunShell(MainWindow window,string output)
    {
        Directory.CreateDirectory(output);
        var vm=(MainViewModel)window.DataContext;
        var config=Path.Combine(vm.SteamPath,"config","loginusers.vdf");
        string? HashConfig()=>File.Exists(config)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(config))):null;
        var metadata=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FloatOrder","accounts.json");
        string? HashMetadata()=>File.Exists(metadata)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(metadata))):null;
        var originalMetadata=HashMetadata();
        var originalConfig=HashConfig(); var activity=vm.Activity.Count;
        var checks=new List<string>();
        void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); checks.Add(message); }
        await vm.AvatarLoadTask.WaitAsync(TimeSpan.FromSeconds(30));
        window.HideToBackground();
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(window.ShellIntegrationEnabled&&window.TrayIconVisible,"实际托盘已创建");
        Check(window.TaskbarItemInfo is null,"旧的应用图标预览按钮已移除");
        var host=window.TaskbarWidgetForVerification??throw new InvalidOperationException("任务栏宿主没有创建。");
        host.SetEnabled(true);
        host.SetDockPosition(TaskbarDockPosition.Tray);
        File.WriteAllText(Path.Combine(output,"shell-model-diagnostic.json"),JsonSerializer.Serialize(new {
            IsDemo=vm.IsDemo,Session=vm.SessionSummary,GameRunning=vm.IsGameRunning,GameName=vm.RunningGameName,GameHint=vm.GameModeHint,
            WidgetEnabled=vm.TaskbarWidgetEnabled,WidgetDock=vm.TaskbarWidgetDock,WidgetSuspended=host.TemporarilySuspended,
            UsesRegistryInstall=string.Equals(Path.TrimEndingDirectorySeparator(vm.SteamPath.Replace('/',Path.DirectorySeparatorChar)),Path.TrimEndingDirectorySeparator(new SteamService().DetectSteamPath().Replace('/',Path.DirectorySeparatorChar)),StringComparison.OrdinalIgnoreCase)
        },new JsonSerializerOptions {WriteIndented=true}));
        var snapshot=await host.RefreshNativeLayoutForVerificationAsync();
        for(int attempt=0;!snapshot.Embedded&&attempt<8;attempt++) { await Task.Delay(250); snapshot=await host.RefreshNativeLayoutForVerificationAsync(); }
        await window.Dispatcher.InvokeAsync(()=>host.WidgetForVerification.UpdateLayout(),DispatcherPriority.Render);
        await Task.Delay(150); snapshot=host.Snapshot;
        File.WriteAllText(Path.Combine(output,"native-widget.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions {WriteIndented=true}));
        Check(snapshot.Embedded,snapshot.Status);
        Check(snapshot.NativeChild&&snapshot.ParentVerified&&snapshot.ParentClass=="Shell_TrayWnd"&&snapshot.WithinTaskbar&&snapshot.TransparentChild,"控件是真正的透明任务栏子窗口");
        Check(snapshot.HitTestOwnWindow,"任务栏控件中心的原生命中窗口属于控件自身");
        Check(snapshot.DpiContextMatched&&snapshot.ParentDpi==snapshot.ChildDpi&&snapshot.CreationThreadContextRestored,"子窗口 DPI 与任务栏匹配且创建线程环境已恢复");
        Check(snapshot.DoesNotOverlapProtectedControls&&snapshot.ProtectedLayoutCurrent,"真实控件位置没有覆盖最近有效布局中的系统控件");
        Check(!window.IsVisible&&!window.ShowInTaskbar,"管理窗口隐藏时仅保留任务栏控件常驻");
        async Task<TaskbarWidgetSnapshot> Ready()
        {
            var current=await host.RefreshNativeLayoutForVerificationAsync();
            for(int attempt=0;!current.Embedded&&attempt<8;attempt++) { await Task.Delay(250); current=await host.RefreshNativeLayoutForVerificationAsync(); }
            return current;
        }
        bool SameBounds(Rect a,Rect b)=>!a.IsEmpty&&!b.IsEmpty&&Math.Abs(a.X-b.X)<=1&&Math.Abs(a.Y-b.Y)<=1&&Math.Abs(a.Width-b.Width)<=1&&Math.Abs(a.Height-b.Height)<=1;
        Point GripPoint(Rect bounds)=>new(bounds.Left+8*host.Snapshot.DpiScale,bounds.Top+bounds.Height/2);
        int committed=0; host.DockPositionCommitted+=(_,_)=>committed++;
        Check(host.DockSlots.Left is not null&&host.DockSlots.Tray is not null,"本机任务栏找到两个不同且不覆盖系统控件的预设落点");
        var originalBounds=host.BoundsPixels;
        var leftSlot=host.DockSlots.Left!;
        Check(host.BeginDrag(GripPoint(originalBounds)),"从左侧握柄位置开始原生拖动");
        // Preserve the actual grab fraction when the destination uses a different width.
        var leftPointer=new Point(leftSlot.BoundsPixels.Left+8*host.Snapshot.DpiScale/originalBounds.Width*leftSlot.BoundsPixels.Width,leftSlot.BoundsPixels.Top+leftSlot.BoundsPixels.Height/2);
        host.UpdateDrag(leftPointer);
        snapshot=host.Snapshot;
        Check(snapshot.IsDragging&&snapshot.PreviewDockPosition=="left"&&snapshot.HintVisible&&snapshot.HintMatchesWidgetSize,"靠近左侧落点显示与当前组件等大的占位框，握柄偏移正确");
        Check(snapshot.HintInputTransparent&&snapshot.HintNoActivate&&snapshot.HintNoTaskbarButton,"占位窗口不抢输入和焦点，也不产生任务栏按钮");
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
        Check(host.SaveDockHintPreviewForVerification(Path.Combine(output,"04-rounded-dashed-dock-hint.png")),"渲染实际圆角虚线占位框");
        File.WriteAllText(Path.Combine(output,"native-drag-hint.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions {WriteIndented=true}));
        Check(await host.EndDragAsync(leftPointer),"松手后通过新的任务栏布局确认并停靠左侧");
        snapshot=host.Snapshot;
        Check(snapshot.Embedded&&!snapshot.IsDragging&&!snapshot.DropPending&&!snapshot.HintVisible&&host.DockPosition==TaskbarDockPosition.Left&&committed==1&&SameBounds(snapshot.BoundsPixels,host.DockSlots.Left!.BoundsPixels),"成功落点只提交一次，隐藏占位框并保留真实子窗口");
        originalBounds=host.BoundsPixels;
        Check(host.BeginDrag(GripPoint(originalBounds)),"再次开始拖动");
        var outside=new Point(originalBounds.Left+8*host.Snapshot.DpiScale,host.TaskbarBoundsPixels.Top-240*host.Snapshot.DpiScale);
        host.UpdateDrag(outside);
        Check(!host.Snapshot.HintVisible&&!await host.EndDragAsync(outside),"在两个落点范围之外松手不会提交位置");
        snapshot=await Ready();
        Check(snapshot.Embedded&&host.DockPosition==TaskbarDockPosition.Left&&committed==1&&SameBounds(snapshot.BoundsPixels,originalBounds),"无效放下回到原位置");
        var widget=host.WidgetForVerification;
        var start=GripPoint(host.BoundsPixels); var traySlot=host.DockSlots.Tray!;
        var trayPointer=new Point(traySlot.BoundsPixels.Left+8*host.Snapshot.DpiScale/host.BoundsPixels.Width*traySlot.BoundsPixels.Width,traySlot.BoundsPixels.Top+traySlot.BoundsPixels.Height/2);
        var nativePressAccepted=widget.RequestDragPressForVerification(start);
        var nativeMoveAccepted=widget.RequestDragMoveForVerification(trayPointer);
        File.WriteAllText(Path.Combine(output,"native-gesture-diagnostic.json"),JsonSerializer.Serialize(new {Press=nativePressAccepted,Move=nativeMoveAccepted,Widget=widget.DragVerification,Host=host.Snapshot},new JsonSerializerOptions {WriteIndented=true}));
        Check(nativePressAccepted&&nativeMoveAccepted&&host.IsDragging&&widget.DragVerification.HasMouseCapture,"组件实际手势与宿主拖动连接，取得自身鼠标捕获");
        widget.RequestDragEscapeForVerification();
        Check(!host.IsDragging&&!host.Snapshot.HintVisible&&!widget.DragVerification.HasMouseCapture&&committed==1,"组件 Esc 取消宿主拖动并释放捕获");
        snapshot=await Ready(); start=GripPoint(host.BoundsPixels); traySlot=host.DockSlots.Tray!;
        trayPointer=new Point(traySlot.BoundsPixels.Left+8*host.Snapshot.DpiScale/host.BoundsPixels.Width*traySlot.BoundsPixels.Width,traySlot.BoundsPixels.Top+traySlot.BoundsPixels.Height/2);
        Check(widget.RequestDragPressForVerification(start)&&widget.RequestDragMoveForVerification(trayPointer)&&host.IsDragging,"关闭组件前存在拖动和占位框");
        host.SetEnabled(false);
        snapshot=host.Snapshot;
        Check(!snapshot.Enabled&&!snapshot.Embedded&&!snapshot.IsDragging&&!snapshot.HintVisible&&!widget.DragVerification.HasMouseCapture&&!widget.DragVerification.InteractionEnabled,"关闭组件立即隐藏自身窗口、占位框并释放鼠标，保留位置");
        await host.RefreshNativeLayoutForVerificationAsync();
        Check(!host.IsEmbedded&&host.DockPosition==TaskbarDockPosition.Left,"关闭状态刷新不会重新显示组件或丢失位置");
        host.SetEnabled(true); snapshot=await Ready();
        Check(snapshot.Embedded&&snapshot.Enabled&&host.DockPosition==TaskbarDockPosition.Left&&SameBounds(snapshot.BoundsPixels,host.DockSlots.Left!.BoundsPixels)&&widget.DragVerification.InteractionEnabled,"重新开启先确认空位，再显示记住的左侧位置");
        File.WriteAllText(Path.Combine(output,"native-dock-lifecycle.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions {WriteIndented=true}));
        var firstGeneration=snapshot.SourceGeneration; var firstDestroyed=snapshot.DestroyedOwnSourceCount; var taskbarHandle=snapshot.ParentHandle;
        await host.ForceRebindAsyncForVerification();
        await window.Dispatcher.InvokeAsync(()=>host.WidgetForVerification.UpdateLayout(),DispatcherPriority.Render);
        await Task.Delay(150); snapshot=host.Snapshot;
        Check(snapshot.Embedded&&snapshot.ParentHandle==taskbarHandle&&snapshot.ParentVerified&&snapshot.WithinTaskbar&&snapshot.HitTestOwnWindow&&
            snapshot.DoesNotOverlapProtectedControls&&snapshot.DpiContextMatched&&snapshot.SourceGeneration==firstGeneration+1&&snapshot.DestroyedOwnSourceCount==firstDestroyed+1,"仅重建自身任务栏子窗口后嵌入与输入命中恢复，未重启 Explorer");
        Check(host.DockPosition==TaskbarDockPosition.Left&&!snapshot.HintVisible&&!snapshot.IsDragging,"重建自身窗口保留落点，清理拖动和占位框");
        File.WriteAllText(Path.Combine(output,"native-rebind.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions {WriteIndented=true}));
        await window.Dispatcher.InvokeAsync(()=>host.WidgetForVerification.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var nativeVisual=host.WidgetForVerification;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(nativeVisual.ActualWidth),(int)Math.Ceiling(nativeVisual.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(nativeVisual);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(Path.Combine(output,"01-embedded-widget-content.png"))) encoder.Save(stream);
        var menu=window.PrepareQuickMenuForVerification(); var repeated=window.PrepareQuickMenuForVerification();
        Check(menu==repeated&&menu.AccountCount==vm.AllAccounts.Count,"托盘重建保留全部账号和头像");
        window.SaveNativeTrayMenuPreviewForVerification(output);
        using(var trayLayout=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"tray-layout.json"))))
        {
            Check(trayLayout.RootElement.GetProperty("AllMenusRounded").GetBoolean(),"托盘主菜单、分组和登录子菜单的原生区域均真实裁为圆角");
            Check(trayLayout.RootElement.GetProperty("Menus").EnumerateArray().All(menu=>menu.GetProperty("AllItemsInsideClientBounds").GetBoolean()&&
                menu.GetProperty("AllItemsInsideNativeRegion").GetBoolean()),"菜单所有操作行完整位于真实窗口及圆角区域内，末行不会被裁掉");
            Check(trayLayout.RootElement.GetProperty("Menus").EnumerateArray().All(menu=>menu.GetProperty("WidthDip").GetDouble()<=
                (menu.GetProperty("Level").GetString()=="main"?260:menu.GetProperty("Level").GetString()=="accounts"?300:200)&&
                menu.GetProperty("ActionHeightsDip").EnumerateArray().All(height=>height.GetDouble()<=34)&&
                menu.GetProperty("AccountHeightsDip").EnumerateArray().All(height=>height.GetDouble()<=46)),"紧凑菜单的宽度与行高缩小，账号备注仍保留双行");
        }
        window.ShowQuickAccounts();
        var popup=window.QuickAccountsWindowForVerification??throw new InvalidOperationException("快捷账号栏没有创建。"); popup.DismissOnDeactivate=false;
        Check(!window.IsVisible&&popup.VisibleAccountCount==vm.AllAccounts.Count,"本机账号从控件快捷入口显示，管理窗口不被打开");
        await CapturePanel(popup,Path.Combine(output,"02-real-account-chooser.png"));
        var account=vm.AllAccounts.FirstOrDefault(a=>!string.IsNullOrEmpty(a.SteamId));
        if(account is not null)
        {
            await window.RequestWidgetSwitchAsync(account,SwitchMode.Online);
            Check(vm.PendingConfirmation&&popup.ConfirmationVisible&&!window.IsVisible&&!vm.IsBusy,"真实账号请求仅在快捷栏内打开确认");
            await CapturePanel(popup,Path.Combine(output,"03-real-inline-confirmation.png"));
            vm.CancelConfirmCommand.Execute(null);
        }
        window.CloseQuickAccounts(); window.ShowFromTray();
        Check(window.IsVisible&&window.ShowInTaskbar&&host.IsEmbedded,"打开管理窗口时任务栏控件仍然存在");
        ((Button)window.FindName("TitleMinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.IsVisible&&!window.ShowInTaskbar&&host.IsEmbedded,"自定义最小化按钮隐藏主窗口并保留任务栏控件");
        window.ShowFromTray();
        ((Button)window.FindName("TitleCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!window.IsVisible&&!window.ShowInTaskbar&&host.IsEmbedded,"自定义关闭按钮隐藏后台，任务栏控件继续常驻");
        var realNativeSnapshot=host.Snapshot;
        host.Dispose();
        Check(host.NativeHandle==IntPtr.Zero&&!host.WatchdogRunning,"完成真实布局验证后销毁本次自有宿主，避免测试组件重叠");
        var offlineNativeWindow=new MainWindow(demo:true,allowAvatarNetwork:false,enableShellIntegration:false,persistTaskbarPosition:false);
        var offlineNativeVm=(MainViewModel)offlineNativeWindow.DataContext;
        TaskbarWidgetHost? offlineNativeHost=null;
        var offlineNativePhases=new List<object>();
        try
        {
            offlineNativeWindow.Show(); offlineNativeWindow.HideToBackground();
            var offlineNativeAccount=offlineNativeVm.AllAccounts.First(a=>a.IsActive);
            var offlineNativeSession=new SteamSessionSnapshot(true,offlineNativeAccount.SteamId,SteamSessionState.Offline,"本地离线登录（合成验证）");
            offlineNativeVm.SetDemoSessionForVerification(offlineNativeSession);
            offlineNativeVm.SetDemoGameForVerification(SteamGameSnapshot.None(offlineNativeAccount.SteamId,true));
            offlineNativeVm.TaskbarWidgetEnabled=true; offlineNativeVm.TaskbarWidgetDock="left";
            string offlineSavedDock=offlineNativeVm.TaskbarWidgetDock;
            offlineNativeHost=new TaskbarWidgetHost(offlineNativeVm,offlineNativeWindow.ShowQuickAccounts,(_,_)=>{},offlineNativeWindow.ShowFromTray);
            offlineNativeHost.WatchWindow(offlineNativeWindow);
            offlineNativeHost.SetDockPosition(TaskbarDockPosition.Left);
            offlineNativeHost.SetTemporarilySuspended(false); offlineNativeHost.SetEnabled(true);
            async Task<TaskbarWidgetSnapshot> OfflineReady()
            {
                var current=await offlineNativeHost.RefreshNativeLayoutForVerificationAsync();
                for(int attempt=0;!current.Embedded&&attempt<12;attempt++)
                { await Task.Delay(250); current=await offlineNativeHost.RefreshNativeLayoutForVerificationAsync(); }
                await window.Dispatcher.InvokeAsync(()=>offlineNativeHost.WidgetForVerification.UpdateLayout(),DispatcherPriority.ApplicationIdle);
                return offlineNativeHost.Snapshot;
            }
            var offlineInitial=await OfflineReady();
            offlineNativePhases.Add(new {Phase="offline-without-game",Snapshot=offlineInitial});
            Check(offlineNativeVm.IsSessionOffline&&!offlineNativeVm.IsGameRunning&&offlineNativeAccount.IsOffline&&
                offlineNativeVm.SessionSummary=="离线登录 · 演示"&&offlineInitial.Embedded&&offlineInitial.NativeChild&&
                offlineInitial.ParentVerified&&offlineInitial.ParentClass=="Shell_TrayWnd"&&offlineInitial.WithinTaskbar&&
                offlineInitial.DoesNotOverlapProtectedControls&&offlineInitial.ProtectedLayoutCurrent&&!offlineInitial.TemporarilySuspended,
                "合成当前离线账号无游戏时可创建真实任务栏子窗口，并避开系统控件");
            Check(offlineNativeHost.WidgetForVerification.ViewModel is {IsOffline:true,IsOnline:false} offlineWidgetVm&&
                offlineWidgetVm.AccountTooltip.Contains("离线登录",StringComparison.Ordinal)&&offlineInitial.WatchdogRunning&&
                offlineInitial.WatchdogIntervalSeconds==3&&!offlineInitial.AutomationWatcherActive,
                "原生离线组件状态和提示正确，采用定期检查而无UIA事件订阅");
            var offlineVisual=offlineNativeHost.WidgetForVerification;
            var offlineBitmap=new RenderTargetBitmap((int)Math.Ceiling(offlineVisual.ActualWidth),(int)Math.Ceiling(offlineVisual.ActualHeight),96,96,PixelFormats.Pbgra32);
            offlineBitmap.Render(offlineVisual);
            var offlineEncoder=new PngBitmapEncoder(); offlineEncoder.Frames.Add(BitmapFrame.Create(offlineBitmap));
            using(var stream=File.Create(Path.Combine(output,"05-offline-native-widget.png"))) offlineEncoder.Save(stream);
            offlineNativeVm.SetDemoGameForVerification(new SteamGameSnapshot(offlineNativeAccount.SteamId,730,"离线游戏（合成验证）",true,true));
            offlineNativeHost.SetTemporarilySuspended(offlineNativeVm.IsGameRunning);
            var offlinePaused=offlineNativeHost.Snapshot;
            offlineNativePhases.Add(new {Phase="offline-game-paused",Snapshot=offlinePaused});
            Check(offlineNativeVm.IsSessionOffline&&offlineNativeVm.IsGameRunning&&offlinePaused.TemporarilySuspended&&
                !offlinePaused.Embedded&&offlinePaused.WidgetHandle==0&&offlineNativeHost.NativeHandle==IntPtr.Zero&&
                offlinePaused.DestroyedOwnSourceCount==offlineInitial.DestroyedOwnSourceCount+1&&
                offlinePaused.SourceGeneration==offlineInitial.SourceGeneration&&!offlinePaused.WatchdogRunning&&!offlinePaused.AutomationWatcherActive,
                "合成离线游戏暂停销毁自有子窗口并停止布局检查，当前离线身份仍保留");
            Check(offlineNativeVm.TaskbarWidgetEnabled&&offlineNativeVm.TaskbarWidgetDock==offlineSavedDock&&
                offlineNativeHost.DockPosition==TaskbarDockPosition.Left,"游戏暂停保留组件显示开关和已保存的左侧偏好");
            offlineNativeHost.SetTemporarilySuspended(true);
            Check(offlineNativeHost.Snapshot.DestroyedOwnSourceCount==offlinePaused.DestroyedOwnSourceCount,
                "重复游戏暂停不会重复销毁同一个子窗口");
            offlineNativeVm.SetDemoGameForVerification(SteamGameSnapshot.None(offlineNativeAccount.SteamId,true));
            Check(offlineNativeVm.IsSessionOffline&&!offlineNativeVm.IsGameRunning&&offlineNativeAccount.PresenceLabel=="离线登录",
                "合成游戏结束后保留当前离线登录状态");
            offlineNativeHost.SetTemporarilySuspended(false);
            var offlineRestored=await OfflineReady();
            offlineNativePhases.Add(new {Phase="offline-after-game",Snapshot=offlineRestored});
            Check(offlineRestored.Embedded&&offlineRestored.WidgetHandle!=0&&!offlineRestored.TemporarilySuspended&&
                offlineRestored.SourceGeneration==offlineInitial.SourceGeneration+1&&
                offlineRestored.DestroyedOwnSourceCount==offlinePaused.DestroyedOwnSourceCount&&
                offlineRestored.ParentVerified&&offlineRestored.WithinTaskbar&&offlineRestored.DoesNotOverlapProtectedControls&&
                offlineRestored.ProtectedLayoutCurrent&&offlineRestored.WatchdogRunning&&!offlineRestored.AutomationWatcherActive,
                "离线游戏结束后重新创建自有窗口，经新布局确认恢复嵌入");
            Check(offlineNativeVm.TaskbarWidgetEnabled&&offlineNativeVm.TaskbarWidgetDock==offlineSavedDock&&
                offlineNativeHost.DockPosition==TaskbarDockPosition.Left,
                "恢复嵌入保留原开关与停靠偏好，安全位置回退不写设置");
            offlineNativeHost.Dispose();
            offlineNativePhases.Add(new {Phase="disposed",Snapshot=offlineNativeHost.Snapshot});
            Check(offlineNativeHost.NativeHandle==IntPtr.Zero&&!offlineNativeHost.IsEmbedded&&!offlineNativeHost.WatchdogRunning,
                "原生合成验证结束销毁自有窗口并停止计时器");
            File.WriteAllText(Path.Combine(output,"native-offline-game-lifecycle.json"),JsonSerializer.Serialize(new {
                Passed=true,Phases=offlineNativePhases,CurrentSessionSynthetic=true,GameSnapshotsSynthetic=true,
                UserPreferencesPreserved=true,RealSteamStarted=false,SteamConfigurationWritten=false,
                Mode="synthetic account state with owned native taskbar child; no Steam authentication or settings writes"
            },new JsonSerializerOptions {WriteIndented=true}));
        }
        finally
        {
            offlineNativeHost?.Dispose(); offlineNativeWindow.CloseQuickAccounts(); offlineNativeWindow.Close();
        }
        Check(HashConfig()==originalConfig&&HashMetadata()==originalMetadata&&vm.Activity.Count==activity&&!vm.IsBusy,"验证没有更改 Steam 配置、真实账号备注或任务栏偏好，也没有启动真实切换");
        CheckBindings(output);
        File.WriteAllText(Path.Combine(output,"shell-result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks,NativeWidget=realNativeSnapshot,OfflineNativeGameLifecycleVerified=true,OfflineNativePhases=offlineNativePhases,AccountCount=vm.AllAccounts.Count,LoadedAvatars=vm.AllAccounts.Count(a=>a.HasAvatar),SteamConfigurationUnchanged=true,AccountMetadataUnchanged=true,RealSteamStarted=false,PhysicalTaskbarClickTested=false,PhysicalMouseDragTested=false},new JsonSerializerOptions {WriteIndented=true}));
    }
    public static async Task RunAvatars(MainWindow window,string output)
    {
        Directory.CreateDirectory(output);
        var vm=(MainViewModel)window.DataContext;
        await vm.AvatarLoadTask.WaitAsync(TimeSpan.FromSeconds(45));
        var accounts=vm.Accounts.Cast<SteamAccount>().ToArray();
        if(accounts.Length>0&&!accounts.Any(a=>a.HasAvatar)) throw new InvalidOperationException("本机账号未加载任何头像。");
        if(accounts.Any(a=>a.AvatarLoading)) throw new InvalidOperationException("头像加载任务未结束。");
        async Task<int> Capture(string name)
        {
            await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            var root=(FrameworkElement)window.Content;
            var list=Descendants(root).OfType<ListBox>().First();
            if(!Descendants(list).OfType<WrapPanel>().Any()) throw new InvalidOperationException("账号区没有使用头像网格布局。");
            var positions=Enumerable.Range(0,accounts.Length).Select(i=>list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<ListBoxItem>().Select(i=>i.TransformToAncestor(list).Transform(new Point(0,0))).ToArray();
            int columns=positions.Length==0 ? 0 : positions.Count(p=>Math.Abs(p.Y-positions[0].Y)<1);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(output,name+".png")); encoder.Save(stream);
            return columns;
        }
        int desktopColumns=await Capture("01-avatar-grid");
        window.Width=1000; window.Height=680;
        int narrowColumns=await Capture("02-avatar-grid-narrow");
        if(accounts.Length>1&&(desktopColumns<2||narrowColumns<2)) throw new InvalidOperationException("账号头像未形成多列网格。");
        var avatarList=Descendants((FrameworkElement)window.Content).OfType<ListBox>().First();
        var scroller=Descendants(avatarList).OfType<ScrollViewer>().First();
        scroller.ScrollToBottom();
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        if(scroller.ScrollableHeight>1&&Math.Abs(scroller.VerticalOffset-scroller.ScrollableHeight)>1) throw new InvalidOperationException("头像网格无法滚动至最后一个账号。");
        await Capture("03-avatar-grid-scrolled");
        CheckBindings(output);
        File.WriteAllText(Path.Combine(output,"avatar-result.json"),JsonSerializer.Serialize(new { Passed=true,AccountCount=accounts.Length,LoadedAvatarCount=accounts.Count(a=>a.HasAvatar),DefaultAvatarCount=accounts.Count(a=>a.IsDefaultAvatar),DesktopColumns=desktopColumns,NarrowColumns=narrowColumns,SteamConfigurationReadOnly=true,RealSteamStarted=false },new JsonSerializerOptions { WriteIndented=true }));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
    public static async Task Run(MainWindow window,string output)
    {
        Directory.CreateDirectory(output);
        var vm=(MainViewModel)window.DataContext;
        var checks=new List<string>();
        int screenshotCount=0;
        void Check(bool condition,string label) { if(!condition) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Capture(string name)
        {
            await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            // Layout and bindings can settle before retained visual drawing catches up.
            // Drain a render pass, then the following idle, before taking the bitmap.
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
            await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            var root=(FrameworkElement)window.Content;
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(output,name+".png")); encoder.Save(stream);
            screenshotCount++;
        }
        Check(vm.IsDemo,"UI 测试使用隔离演示模式");
        Check(!window.ShellIntegrationEnabled,"UI 验证不创建系统托盘或任务栏快捷入口");
        Check(window.Title.Contains("Gameorder",StringComparison.Ordinal),"窗口标题统一 Gameorder");
        Check(window.WindowStyle==WindowStyle.None&&!window.AllowsTransparency&&window.ResizeMode==ResizeMode.CanResize&&
            Descendants((FrameworkElement)window.Content).OfType<Button>().Count(button=>button.Name.StartsWith("Title",StringComparison.Ordinal))==2&&
            window.FindName("TitleMinimizeButton") is Button&&window.FindName("TitleCloseButton") is Button,"自定义标题栏只有最小化和关闭，并保留窗口边缘调整大小");
        window.WindowState=WindowState.Maximized;
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(window.WindowState==WindowState.Normal,"程序或系统请求最大化时恢复为普通窗口");
        var chrome=window.NativeChromeVerification;
        Check(chrome.HasCustomTitleBar&&chrome.NativeMaximizeBoxRemoved&&chrome.SystemMaximizeEntryRemoved&&chrome.NativeResizeAvailable,"原生窗口禁用最大化且保留边缘调整大小");
        Check(window.RequestNativeMaximizeForVerification()&&window.DispatchChromeMessageForVerification(0x00A3,2)&&window.WindowState==WindowState.Normal,"原生最大化命令和标题栏双击均被阻止");
        var minButton=(Button)window.FindName("TitleMinimizeButton");
        var minCenter=minButton.TransformToAncestor(window).Transform(new Point(minButton.ActualWidth/2,minButton.ActualHeight/2));
        Check(window.GetNativeHitTestForVerification(new Point(180,22))==2&&window.GetNativeHitTestForVerification(minCenter)==1&&
            window.GetNativeHitTestForVerification(new Point(1,window.ActualHeight/2)) is 10 or 13 or 16,"标题栏原生命中支持拖动，按钮接收点击，边缘支持调整大小");
        File.WriteAllText(Path.Combine(output,"window-chrome.json"),JsonSerializer.Serialize(window.NativeChromeVerification,new JsonSerializerOptions {WriteIndented=true}));
        Check(vm.Accounts.Cast<SteamAccount>().Count()==3,"加载三个明确标注的演示账号");
        bool widgetAtStartup=vm.TaskbarWidgetEnabled;
        vm.SetDemoSessionForVerification(new SteamSessionSnapshot(false,null,SteamSessionState.Stopped,"Steam 未启动（演示验证）"));
        Check(!vm.IsSessionOnline&&vm.IsSessionOffline&&vm.SessionSummary=="离线 · 演示"&&
            vm.AllAccounts.All(account=>!account.IsOnline&&!account.IsActive),
            "启动确认无在线会话时显示离线，不给任何账号伪造在线状态");
        Check(vm.TaskbarWidgetEnabled==widgetAtStartup&&!vm.TaskbarWidgetTemporarilyHidden&&vm.AllAccounts.Count==3,
            "无在线账号仍保留组件显示偏好与全部账号头像资料");
        await Capture("26-offline-status");
        var currentSessionText=(TextBlock)window.FindName("CurrentSessionText");
        Check(currentSessionText.IsVisible&&currentSessionText.Text=="离线 · 演示"&&
            Descendants((FrameworkElement)window.Content).OfType<ListBox>().First().IsVisible,
            "无在线会话时主界面显示离线，账号头像区域继续可见");
        vm.SetDemoSessionForVerification(null);
        Check(vm.IsSessionOnline&&vm.SessionSummary=="在线 · 演示"&&vm.AllAccounts.Count(account=>account.IsOnline)==1,
            "恢复演示会话后仅当前账号显示在线");
        var offlineAccount=vm.AllAccounts.First(account=>account.IsActive);
        var offlineSnapshot=new SteamSessionSnapshot(true,offlineAccount.SteamId,SteamSessionState.Offline,"已确认离线登录（演示验证）");
        using var quietWidgetModel=new TaskbarWidgetViewModel(vm);
        try
        {
            vm.SetDemoSessionForVerification(offlineSnapshot);
            Check(vm.IsSessionOffline&&!vm.IsSessionOnline&&vm.SessionSummary=="离线登录 · 演示"&&
                offlineAccount.IsActive&&offlineAccount.IsOffline&&!offlineAccount.IsOnline&&offlineAccount.PresenceLabel=="离线登录"&&
                vm.AllAccounts.Where(account=>!ReferenceEquals(account,offlineAccount)).All(account=>!account.IsOffline&&!account.IsOnline),
                "已登录的离线会话确认当前身份，不与Steam未运行或其他账号混淆");
            await Capture("27-confirmed-offline-accounts");
            Check(currentSessionText.Text.Contains("离线登录",StringComparison.Ordinal)&&
                Descendants((FrameworkElement)window.Content).OfType<TextBlock>().Any(block=>block.IsVisible&&block.Text=="离线登录"),
                "主界面与当前账号头像卡片实际显示离线登录");
            Check(quietWidgetModel.SelectedAccount==offlineAccount&&quietWidgetModel.IsOffline&&!quietWidgetModel.IsOnline&&
                quietWidgetModel.PresenceLabel=="离线登录"&&quietWidgetModel.AccountTooltip.Contains("离线登录",StringComparison.Ordinal),
                "任务栏状态点为离线，悬停提示明确包含离线登录");
            var offlineTrayEntry=TrayService.BuildMenuSnapshot(vm).Single(entry=>entry.SteamId==offlineAccount.SteamId);
            Check(offlineTrayEntry.IsActive&&offlineTrayEntry.IsOffline&&!offlineTrayEntry.IsOnline&&offlineTrayEntry.PresenceLabel=="离线登录",
                "托盘账号入口保留同一离线身份与离线登录状态");
            using(var offlineTray=new TrayService(vm,window,enableShell:false))
            {
                Check(!offlineTray.IsVisible&&!offlineTray.GameOptimizationVerification.ShellEnabled&&
                    offlineTray.GameOptimizationVerification.Tooltip.Contains("离线登录",StringComparison.Ordinal),
                    "隔离托盘提示显示离线登录，不加入系统托盘");
                offlineTray.SaveMenuPreviewForVerification(Path.Combine(output,"30-offline-tray"));
            }

            var vmNotifications=new List<string>();
            var accountNotifications=new List<string>();
            var widgetNotifications=new List<string>();
            System.ComponentModel.PropertyChangedEventHandler vmChanged=(_,e)=>vmNotifications.Add(e.PropertyName??"");
            System.ComponentModel.PropertyChangedEventHandler accountChanged=(sender,e)=>accountNotifications.Add(((SteamAccount)sender!).AccountName+":"+e.PropertyName);
            System.ComponentModel.PropertyChangedEventHandler widgetChanged=(_,e)=>widgetNotifications.Add(e.PropertyName??"");
            var observedAccounts=vm.AllAccounts.ToArray();
            vm.PropertyChanged+=vmChanged; quietWidgetModel.PropertyChanged+=widgetChanged;
            foreach(var account in observedAccounts) account.PropertyChanged+=accountChanged;
            try { await vm.RefreshSessionAsync(); await vm.RefreshSessionAsync(); await vm.RefreshSessionAsync(); }
            finally
            {
                vm.PropertyChanged-=vmChanged; quietWidgetModel.PropertyChanged-=widgetChanged;
                foreach(var account in observedAccounts) account.PropertyChanged-=accountChanged;
            }
            File.WriteAllText(Path.Combine(output,"unchanged-session-notifications.json"),JsonSerializer.Serialize(new {
                SyntheticSession=true,Reads=3,ViewModelNotifications=vmNotifications,AccountNotifications=accountNotifications,
                WidgetNotifications=widgetNotifications},new JsonSerializerOptions {WriteIndented=true}));
            Check(vmNotifications.Count==0&&accountNotifications.Count==0&&widgetNotifications.Count==0,
                "相同会话快照连续读取三次，不通知主界面、账号或任务栏组件重复重绘");
            string noteBeforeRefresh=offlineAccount.Note;
            offlineAccount.Note="离线备注同步验证";
            Check(quietWidgetModel.Note=="离线备注同步验证"&&quietWidgetModel.HasNote&&
                quietWidgetModel.AccountTooltip.Contains("离线备注同步验证",StringComparison.Ordinal),
                "减少状态刷新后备注变化仍立即同步到组件文字与悬停提示");
            offlineAccount.Note=noteBeforeRefresh;

            window.HideToBackground();
            Check(!window.IsVisible&&!window.QuickAccountsVisible&&vm.EffectiveMonitoringIntervalSeconds==15&&vm.IsBackgroundEfficiencyRequested,
                "两个界面都隐藏后采用15秒后台刷新并请求自身节能");
            window.ShowQuickAccounts();
            var offlineQuick=window.QuickAccountsWindowForVerification!;
            offlineQuick.DismissOnDeactivate=false;
            await CapturePanel(offlineQuick,Path.Combine(output,"28-confirmed-offline-quick.png")); screenshotCount++;
            Check(!window.IsVisible&&offlineQuick.IsVisible&&vm.EffectiveMonitoringIntervalSeconds==5&&!vm.IsBackgroundEfficiencyRequested,
                "仅打开快捷头像栏即可恢复5秒刷新与交互策略");
            Check(Descendants((FrameworkElement)offlineQuick.Content).OfType<TextBlock>().Any(block=>block.IsVisible&&block.Text=="离线登录"),
                "快捷头像栏的当前账号实际显示离线登录");
            window.CloseQuickAccounts();
            Check(!window.QuickAccountsVisible&&vm.EffectiveMonitoringIntervalSeconds==15&&vm.IsBackgroundEfficiencyRequested,
                "关闭快捷栏且主窗口仍隐藏时恢复后台节能");
            window.ShowFromTray();
            Check(window.IsVisible&&vm.EffectiveMonitoringIntervalSeconds==5&&!vm.IsBackgroundEfficiencyRequested,
                "打开主窗口恢复5秒刷新，不继续请求后台节能");
            using(var offlineWidget=new TaskbarAccountWidget(quietWidgetModel))
            {
                offlineWidget.ConfigureSize(360,48); offlineWidget.SetTaskbarTheme(false);
                var widgetPreview=new Window {Content=new Grid {Background=new SolidColorBrush(Color.FromRgb(232,235,242)),Children={offlineWidget}},
                    Width=360,Height=48,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,
                    WindowStartupLocation=WindowStartupLocation.CenterScreen};
                try
                {
                    widgetPreview.Show();
                    await CapturePanel(widgetPreview,Path.Combine(output,"29-confirmed-offline-widget.png")); screenshotCount++;
                    Check(offlineWidget.ViewModel!.IsOffline&&offlineWidget.ViewModel.AccountTooltip.Contains("离线登录",StringComparison.Ordinal),
                        "实际任务栏组件预览显示离线状态并保留明确的离线登录提示");
                }
                finally { widgetPreview.Close(); }
            }
        }
        finally
        {
            window.CloseQuickAccounts(); window.ShowFromTray();
            vm.SetDemoSessionForVerification(null); vm.SetForegroundInteractive(true);
        }
        Check(vm.AllAccounts.Count==3&&vm.TaskbarWidgetEnabled==widgetAtStartup&&vm.IsSessionOnline&&
            vm.EffectiveMonitoringIntervalSeconds==5&&!vm.IsBackgroundEfficiencyRequested,"离线与后台验证恢复演示会话和组件偏好");
        var trayMenu=TrayService.BuildMenuSnapshot(vm);
        Check(trayMenu.Count==3&&trayMenu.Count(a=>a.CanOffline)==2,"托盘菜单包含全部账号并禁用无缓存离线入口");
        var trayTarget=vm.AllAccounts.First(a=>a.HasCachedLogin&&!a.IsCurrentSelection);
        await TrayService.RequestSwitchForVerificationAsync(vm,trayTarget.SteamId,SwitchMode.Offline);
        Check(vm.PendingConfirmation&&vm.SelectedAccount==trayTarget&&!vm.IsBusy&&vm.Activity.Count==0,"托盘快捷切换先确认且未执行");
        vm.CancelConfirmCommand.Execute(null);
        Check(!vm.PendingConfirmation&&vm.Activity.Count==0,"取消快捷切换不产生请求记录");
        vm.SelectedAccount=vm.AllAccounts.First();
        Check(vm.OnlineCommand.CanExecute(null)&&vm.OfflineCommand.CanExecute(null),"缓存账号可请求在线和离线启动");
        await Capture("01-accounts");
        vm.SearchText="no_such_account"; Check(vm.IsEmpty,"搜索无结果时进入空状态"); await Capture("02-empty");
        vm.SearchText=""; vm.FilterCachedCommand.Execute(null); Check(vm.Accounts.Cast<SteamAccount>().Count()==2,"已记住登录筛选正确");
        vm.FilterFavoriteCommand.Execute(null); Check(vm.Accounts.Cast<SteamAccount>().Count()==1,"收藏筛选正确");
        vm.FilterAllCommand.Execute(null); vm.SelectedAccount=vm.Accounts.Cast<SteamAccount>().Last();
        Check(!vm.OfflineCommand.CanExecute(null),"无缓存账号禁用离线操作"); await Capture("03-needs-login");
        vm.AddAccountCommand.Execute(null); vm.NewAccountName="bad name"; vm.SaveNewAccountCommand.Execute(null);
        Check(vm.IsAddAccountOpen&&!string.IsNullOrEmpty(vm.AddFormError),"无效登录名有表单错误");
        vm.NewAccountName="qa_account"; vm.NewDisplayName="这是用于验证较长昵称布局的测试账号"; await Capture("04-add-account");
        vm.SaveNewAccountCommand.Execute(null); Check(vm.SelectedAccount?.AccountName=="qa_account","手动添加账号可选择");
        vm.NoteDraft="验证备注不会写入真实账号文件"; vm.SaveNoteCommand.Execute(null); Check(vm.SelectedAccount!.Note==vm.NoteDraft,"备注保存反馈");
        vm.OnlineCommand.Execute(null); Check(vm.PendingConfirmation,"切换显示明确确认提示");
        vm.ShowStatus("确认中的演示提示","等待用户确认时不自动收起提示。","info");
        Check(!vm.StatusDismissScheduled,"待确认时不调度普通提示自动消失");
        await Capture("05-switch-confirmation");
        window.HideToBackground();
        Check(vm.IsBackgroundEfficiencyRequested&&vm.EffectiveMonitoringIntervalSeconds==15,"隐藏界面后演示切换前仍使用后台策略");
        vm.ConfirmCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(vm.IsBusy&&vm.OperationVisible&&!vm.StatusDismissScheduled&&!vm.IsBackgroundEfficiencyRequested,"后台模拟切换期间恢复进程交互策略，保留进度而不自动关闭");
        vm.ShowStatus("处理中","演示切换仍在进行。","info");
        Check(!vm.StatusDismissScheduled,"加载期间新增普通提示也不调度自动关闭");
        while(vm.IsBusy) await Task.Delay(40);
        Check(vm.StatusTitle=="演示操作完成"&&vm.Activity.Count(entry=>entry.EffectiveCategory=="login"&&entry.Kind=="info"&&entry.Title.Contains("演示",StringComparison.Ordinal))==1,
            "模拟切换仅记录一条演示请求，同时保留账号与备注操作");
        Check(vm.IsBackgroundEfficiencyRequested,"后台模拟切换结束后恢复自身节能请求");
        window.ShowFromTray();
        vm.DismissStatusCommand.Execute(null); vm.ShowHistoryCommand.Execute(null); await Capture("06-history");
        Check(vm.IsHistoryView&&!vm.ActivityEmpty,"操作记录视图正常");
        vm.ShowSettingsCommand.Execute(null);
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var widgetToggle=(System.Windows.Controls.Primitives.ToggleButton)window.FindName("TaskbarWidgetToggle");
        Check(widgetToggle.IsChecked==true&&vm.TaskbarWidgetEnabled&&vm.TaskbarWidgetDock=="tray","旧版设置默认启用组件并选择托盘侧");
        widgetToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(!vm.TaskbarWidgetEnabled&&widgetToggle.IsChecked==false,"设置开关双向同步关闭状态");
        var leftDockButton=(Button)window.FindName("TaskbarDockLeftButton");
        leftDockButton.Command.Execute(leftDockButton.CommandParameter);
        Check(vm.TaskbarWidgetDock=="left"&&vm.TaskbarWidgetDockIsLeft&&!vm.TaskbarWidgetDockIsTray,"设置位置按钮选择左侧，组件关闭时仍可记住位置");
        widgetToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,true);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(vm.TaskbarWidgetEnabled&&vm.TaskbarWidgetDock=="left"&&vm.TaskbarWidgetSettingsMessage.Contains("演示"),"重新启用保留左侧位置，隔离演示不写真实文件");
        var trayDockButton=(Button)window.FindName("TaskbarDockTrayButton");
        trayDockButton.Command.Execute(trayDockButton.CommandParameter);
        Check(vm.TaskbarWidgetDockIsTray&&!vm.TaskbarWidgetDockIsLeft,"两个位置按钮的选中状态相互同步");
        leftDockButton.Command.Execute(leftDockButton.CommandParameter);
        await Capture("07-settings");
        vm.ShowAccountsCommand.Execute(null); window.Width=1000; window.Height=680;
        vm.SelectedAccount=vm.Accounts.Cast<SteamAccount>().First(); await Capture("08-narrow-window");
        Check(((FrameworkElement)window.Content).ActualWidth<=1000,"窄桌面窗口实际布局");
        int accountCount=vm.AllAccounts.Count;
        vm.CreateFolderCommand.Execute(null);
        vm.FolderDraftName=""; vm.SaveFolderCommand.Execute(null);
        Check(vm.IsFolderDialogOpen&&!string.IsNullOrWhiteSpace(vm.FolderFormError),"空分组名不会创建分组");
        vm.FolderDraftName="常用游戏 · 验证"; await Capture("09-create-folder");
        vm.SaveFolderCommand.Execute(null);
        var folder=vm.FolderEntries.Single(f=>f.Name=="常用游戏 · 验证");
        Check(!vm.IsFolderDialogOpen&&folder.Id!="*"&&!string.IsNullOrEmpty(folder.Id),"创建独立账号分组");
        vm.SelectFolderCommand.Execute("*");
        var managedAccount=vm.AllAccounts.First(); vm.SelectedAccount=managedAccount;
        vm.EditAccountCommand.Execute(null);
        vm.EditDisplayName="Northwind · 主账号";
        vm.EditNote="好友联机 / 周末常用\n直接显示在头像下方的账号备注";
        vm.EditFolderChoice=vm.FolderChoices.Single(f=>f.Id==folder.Id);
        await Capture("10-edit-account");
        var nicknameBox=Descendants((FrameworkElement)window.Content).OfType<TextBox>().Single(t=>System.Windows.Automation.AutomationProperties.GetName(t)=="编辑显示名称");
        var nicknameHost=(ScrollViewer)nicknameBox.Template.FindName("PART_ContentHost",nicknameBox);
        // ViewportHeight already excludes the TextBox padding; it must fit at least one text line.
        Check(nicknameHost.ViewportHeight>=nicknameBox.FontSize*1.2,"昵称输入框为可见文字保留足够高度");
        var folderSelector=Descendants((FrameworkElement)window.Content).OfType<ComboBox>().First(c=>c.IsVisible);
        Check(Descendants(folderSelector).OfType<TextBlock>().Any(t=>t.Text==folder.Name),"分组选择框显示分组名称而非类型名称");
        vm.SaveEditAccountCommand.Execute(null);
        Check(!vm.IsEditAccountOpen&&managedAccount.FolderId==folder.Id&&managedAccount.HasNote&&managedAccount.DisplayName=="Northwind · 主账号","昵称备注与账号移动同时保存");
        vm.SelectFolderCommand.Execute(folder.Id);
        Check(vm.Accounts.Cast<SteamAccount>().Single()==managedAccount,"分组只显示关联账号");
        await Capture("11-folder-notes");
        Check(vm.ActiveAccountName==managedAccount.DisplayName&&vm.CurrentSessionDisplay.Contains(managedAccount.DisplayName,StringComparison.Ordinal)&&currentSessionText.Text.Contains(managedAccount.DisplayName,StringComparison.Ordinal),"修改当前账号昵称后主界面会话提示立即同步新名称");
        string savedNote=managedAccount.Note;
        int operationsBeforeCancelEdit=vm.Activity.Count;
        vm.EditAccountCommand.Execute(null); vm.EditNote="取消的改动"; vm.CloseEditAccountCommand.Execute(null);
        Check(managedAccount.Note==savedNote&&vm.Activity.Count==operationsBeforeCancelEdit,"取消账号编辑不改动备注或新增操作记录");
        vm.RenameFolderCommand.Execute(null); vm.FolderDraftName="联机账号"; vm.SaveFolderCommand.Execute(null);
        Check(managedAccount.FolderName=="联机账号","分组重命名同步账号显示");
        vm.DeleteFolderCommand.Execute(null);
        Check(vm.PendingConfirmation,"删除分组先确认");
        vm.CancelConfirmCommand.Execute(null);
        Check(managedAccount.FolderId==folder.Id,"取消删除保留账号分组");
        vm.DeleteFolderCommand.Execute(null); vm.ConfirmCommand.Execute(null);
        while(vm.IsBusy) await Task.Delay(25);
        Check(vm.AllAccounts.Count==accountCount&&managedAccount.FolderId==""&&managedAccount.Note==savedNote,"删除分组仅解除关联而保留账号备注");
        vm.SelectFolderCommand.Execute("*"); await Capture("12-notes-overview");
        Check(vm.TaskbarWidgetEnabled&&vm.TaskbarWidgetDock=="left","账号编辑、分组创建和删除保留任务栏偏好");
        // Render the real About navigation and embedded author asset in the isolated demo.
        window.Width=1280; window.Height=820;
        var selectedBeforeAbout=vm.SelectedAccount;
        var aboutNavigation=(Button)window.FindName("AboutNavigationButton");
        Check(aboutNavigation.Command is not null&&aboutNavigation.Command.CanExecute(aboutNavigation.CommandParameter),"关于入口绑定可执行导航命令");
        aboutNavigation.Command!.Execute(aboutNavigation.CommandParameter);
        await Capture("13-about");
        var aboutPage=(FrameworkElement)window.FindName("AboutPage");
        var creatorName=(TextBlock)window.FindName("CreatorNameText");
        var creatorAvatar=(System.Windows.Controls.Image)window.FindName("CreatorAvatar");
        var appVersion=(TextBlock)window.FindName("AppVersionText");
        var releaseNotes=(ItemsControl)window.FindName("ReleaseNotesList");
        Check(vm.IsAboutView&&!vm.IsAccountsView&&!vm.IsHistoryView&&!vm.IsSettingsView&&aboutPage.IsVisible,"关于页独立显示且不叠加其他页面");
        Check(creatorName.Text=="XIAOYING223"&&creatorName.IsVisible,"关于页显示指定作者名字");
        Check(creatorAvatar.Source is BitmapSource bitmap&&bitmap.PixelWidth>0&&bitmap.PixelHeight>0&&creatorAvatar.IsVisible,"关于页成功加载内嵌作者头像");
        Check(appVersion.Text.Contains(AboutInfo.Version,StringComparison.Ordinal)&&releaseNotes.Items.Count==AboutInfo.ReleaseNotes.Count&&releaseNotes.Items.Count>=5,"关于页显示程序集当前版本与实际更新历史");
        window.Width=1000; window.Height=680;
        await Capture("14-about-narrow");
        var content=(FrameworkElement)window.Content;
        var nameBounds=creatorName.TransformToAncestor(content).TransformBounds(new Rect(creatorName.RenderSize));
        Check(nameBounds.Left>=0&&nameBounds.Right<=content.ActualWidth&&nameBounds.Top>=0&&nameBounds.Bottom<=content.ActualHeight,"窄窗口中作者署名保持可见");
        var aboutScroller=aboutPage as ScrollViewer??Descendants(aboutPage).OfType<ScrollViewer>().FirstOrDefault();
        Check(aboutScroller is not null&&aboutScroller.ViewportHeight>0&&aboutScroller.ScrollableWidth<1,"关于内容可纵向浏览并适配窄窗口");
        aboutScroller!.ScrollToBottom();
        await Capture("15-about-changelog");
        Check(vm.AllAccounts.Count==accountCount&&ReferenceEquals(vm.SelectedAccount,selectedBeforeAbout)&&managedAccount.Note==savedNote&&vm.TaskbarWidgetEnabled&&vm.TaskbarWidgetDock=="left","浏览关于和日志保留账号备注与任务栏设置");
        var developmentExpander=(Expander)window.FindName("DevelopmentHistoryExpander");
        var developmentStages=(ItemsControl)window.FindName("DevelopmentStagesList");
        Check(AboutInfo.ReleaseNotes.Count(note=>note.IsLatest)==1&&AboutInfo.ReleaseNotes.First().Version==AboutInfo.Version&&
            AboutInfo.ReleaseNotes.Any(note=>note.Version=="1.8.2")&&AboutInfo.ReleaseNotes.Any(note=>note.Version=="1.8.1")&&AboutInfo.ReleaseNotes.Any(note=>note.Version=="1.8.0")&&AboutInfo.ReleaseNotes.Any(note=>note.Version=="1.7.0"),
            "版本记录保留1.8.2、1.8.1、1.8.0与1.7.0，只有程序集当前版本标为最新");
        Check(developmentStages.Items.Count==18&&developmentStages.Items.Cast<ReleaseNote>().Select(stage=>stage.Version)
            .SequenceEqual(AboutInfo.DevelopmentStages.Select(stage=>stage.Version))&&
            AboutInfo.DevelopmentStages.First().Version=="0.1"&&AboutInfo.DevelopmentStages.Last().Version=="1.8",
            "开发历程绑定18个阶段，从0.1完整延伸到1.8");
        Check(AboutInfo.DevelopmentHistoryNotice==""&&AboutInfo.DevelopmentHistoryLabel=="开发历程 · 0.1–1.8",
            "开发历程仅显示标题与功能进展，去除额外解释文字");
        developmentExpander.IsExpanded=true;
        window.Width=1280; window.Height=820;
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        var firstStage=(FrameworkElement)developmentStages.ItemContainerGenerator.ContainerFromIndex(0);
        firstStage.BringIntoView();
        await Capture("16-development-stages");
        Check(developmentExpander.IsExpanded&&developmentStages.IsVisible&&firstStage.ActualHeight>0,
            "展开开发历程后阶段内容具有实际可见布局");
        window.Width=1000; window.Height=680;
        await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
        firstStage.BringIntoView();
        await Capture("17-development-stages-narrow");
        Check(aboutScroller.ScrollableWidth<1&&aboutScroller.ViewportWidth>0,"窄窗口开发历程仅纵向滚动而不产生横向溢出");
        var lastStage=(FrameworkElement)developmentStages.ItemContainerGenerator.ContainerFromIndex(developmentStages.Items.Count-1);
        lastStage.BringIntoView();
        await Capture("18-development-stages-end");
        var stageBounds=lastStage.TransformToAncestor(aboutScroller).TransformBounds(new Rect(lastStage.RenderSize));
        Check(stageBounds.Top>=-1&&stageBounds.Bottom<=aboutScroller.ActualHeight+1,"可以滚动查看最后的1.8游戏感知阶段");
        developmentExpander.IsExpanded=false;

        vm.ShowAccountsCommand.Execute(null);
        Check(vm.IsAccountsView&&!vm.IsAboutView,"关于页可返回账号管理");
        var activeAccount=vm.AllAccounts.First(account=>account.IsActive);
        var otherAccount=vm.AllAccounts.First(account=>!account.IsActive);
        var selectedBeforeGame=vm.SelectedAccount;
        bool enabledBeforeGame=vm.TaskbarWidgetEnabled;
        string dockBeforeGame=vm.TaskbarWidgetDock;
        const string demoGameName="Counter-Strike 2（演示）";
        using var isolatedTray=new TrayService(vm,window,enableShell:false);
        Check(!isolatedTray.IsVisible&&!isolatedTray.GameOptimizationVerification.ShellEnabled,
            "托盘验证只创建隔离菜单与图标，未加入系统托盘");
        try
        {
            vm.SetDemoGameForVerification(SteamGameSnapshot.None(activeAccount.SteamId,true));
            Check(!vm.IsGameRunning&&vm.GameMonitoringIntervalSeconds==5&&!vm.TaskbarWidgetTemporarilyHidden,
                "无游戏时保持正常5秒状态刷新并不临时隐藏组件");
            vm.SetDemoGameForVerification(new SteamGameSnapshot(otherAccount.SteamId,730,demoGameName,true,true,"非当前账号演示快照"));
            Check(!vm.IsGameRunning&&vm.AllAccounts.All(account=>!account.IsPlaying)&&vm.GameMonitoringIntervalSeconds==5,
                "忽略非当前Steam账号的运行快照，不误触发后台节能");
            vm.SetDemoGameForVerification(new SteamGameSnapshot(activeAccount.SteamId,730,demoGameName,true,true,"隔离演示游戏状态"));
            Check(vm.IsGameRunning&&vm.RunningGameName==demoGameName&&activeAccount.IsPlaying&&activeAccount.RunningGameName==demoGameName,
                "当前账号运行快照显示游戏名称并进入游戏状态");
            Check(vm.AllAccounts.Where(account=>!account.IsActive).All(account=>!account.IsPlaying&&account.RunningGameName==""),
                "运行游戏只标记当前账号，其余账号不显示游戏状态");
            Check(vm.GameMonitoringIntervalSeconds==20&&vm.TaskbarWidgetTemporarilyHidden,
                "游戏中将状态刷新放慢至20秒并临时隐藏任务栏组件");
            vm.SetForegroundInteractive(false);
            Check(vm.EffectiveMonitoringIntervalSeconds==20&&vm.IsBackgroundEfficiencyRequested&&vm.TaskbarWidgetTemporarilyHidden,
                "游戏后台保持20秒状态刷新与自身节能请求，组件继续临时隐藏");
            vm.SetForegroundInteractive(true);
            Check(vm.EffectiveMonitoringIntervalSeconds==20&&!vm.IsBackgroundEfficiencyRequested&&vm.TaskbarWidgetTemporarilyHidden,
                "游戏中打开界面恢复交互策略，仍保留20秒刷新与组件临时隐藏");
            Check(vm.TaskbarWidgetEnabled==enabledBeforeGame&&vm.TaskbarWidgetDock==dockBeforeGame,
                "游戏自动收起组件不改写显示开关或停靠偏好");
            var optimizedTray=isolatedTray.GameOptimizationVerification;
            Check(optimizedTray.IsGameOptimized&&optimizedTray.OptimizedIconSelected&&optimizedTray.Tooltip=="Gameorder · 后台优化"&&
                optimizedTray.EnabledMenuActions.SequenceEqual(new[]{"打开主界面"})&&optimizedTray.TemporaryIconHandleReleased&&
                !optimizedTray.ShellEnabled&&!optimizedTray.MenuVisible,
                "游戏期间隔离托盘使用绿色优化图标，菜单只有打开主界面操作");
            string optimizedTrayOutput=Path.Combine(output,"25-optimized-tray");
            isolatedTray.SaveMenuPreviewForVerification(optimizedTrayOutput);
            Check(File.Exists(Path.Combine(optimizedTrayOutput,"tray-menu.png"))&&File.Exists(Path.Combine(optimizedTrayOutput,"tray-icon.png")),
                "导出实际绿色托盘图标与紧凑圆角优化菜单");
            using(var trayLayout=JsonDocument.Parse(File.ReadAllText(Path.Combine(optimizedTrayOutput,"tray-layout.json"))))
                Check(trayLayout.RootElement.GetProperty("AllMenusRounded").GetBoolean(),"优化菜单仍应用实际圆角原生裁剪区域");
            var trayActivityBefore=vm.Activity.Count;
            await TrayService.RequestSwitchForVerificationAsync(vm,otherAccount.SteamId,SwitchMode.Online);
            Check(!vm.PendingConfirmation&&!vm.IsBusy&&vm.Activity.Count==trayActivityBefore,
                "游戏模式托盘不允许绕过精简菜单触发账号切换");
            vm.SelectedAccount=activeAccount;
            window.Width=1280; window.Height=820;
            await Capture("19-game-running");
            var runningGameText=(TextBlock)window.FindName("MainRunningGameText");
            void WriteGameHeaderDiagnostic(string name)
            {
                var root=(FrameworkElement)window.Content;
                var parents=new List<object>();
                DependencyObject? node=runningGameText;
                while(node is FrameworkElement element)
                {
                    var bounds=element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    parents.Add(new {Type=element.GetType().Name,element.Name,element.Visibility,element.IsVisible,
                        element.ActualWidth,element.ActualHeight,element.DesiredSize,element.RenderSize,BoundsInRoot=bounds,
                        element.ClipToBounds,HasClip=element.Clip is not null});
                    if(ReferenceEquals(element,root)) break;
                    node=VisualTreeHelper.GetParent(node);
                }
                File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(new {
                    SyntheticGameName=demoGameName,Text=runningGameText.Text,WindowWidth=window.ActualWidth,
                    WindowHeight=window.ActualHeight,Ancestors=parents},new JsonSerializerOptions {WriteIndented=true}));
            }
            WriteGameHeaderDiagnostic("game-header-layout-1280");
            Check(runningGameText.IsVisible&&runningGameText.Text.Contains(demoGameName,StringComparison.Ordinal)&&
                runningGameText.ActualHeight>8&&runningGameText.ActualWidth>10,
                "主界面运行游戏文字有非零绘制布局并显示演示游戏");
            vm.ShowSettingsCommand.Execute(null);
            await Capture("20-game-settings");
            var gameNotice=(Border)window.FindName("GamePauseSettingsNotice");
            Check(gameNotice.IsVisible&&widgetToggle.IsChecked==enabledBeforeGame,
                "设置显示游戏暂停组件说明，开关仍保留原值");
            window.Width=1000; window.Height=680;
            vm.ShowAccountsCommand.Execute(null);
            await Capture("21-game-running-narrow");
            WriteGameHeaderDiagnostic("game-header-layout-1000");
            var runningBounds=runningGameText.TransformToAncestor((FrameworkElement)window.Content).TransformBounds(new Rect(runningGameText.RenderSize));
            Check(runningGameText.IsVisible&&runningGameText.ActualHeight>8&&runningGameText.ActualWidth>10&&
                runningBounds.Left>=0&&runningBounds.Right<=((FrameworkElement)window.Content).ActualWidth+1,
                "窄窗口运行游戏文字有非零绘制布局且不超出主界面");
            vm.ShowSettingsCommand.Execute(null);
            await Capture("22-game-settings-narrow");
            Check(gameNotice.IsVisible&&gameNotice.ActualHeight>0,"窄窗口游戏暂停组件说明正常显示");
            vm.SetDemoGameForVerification(SteamGameSnapshot.None(activeAccount.SteamId,true));
            await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            Check(!vm.IsGameRunning&&!activeAccount.IsPlaying&&activeAccount.RunningGameName==""&&
                vm.GameMonitoringIntervalSeconds==5&&!vm.TaskbarWidgetTemporarilyHidden&&!gameNotice.IsVisible,
                "游戏结束清除账号游戏状态，恢复5秒刷新并解除组件临时隐藏");
            Check(vm.TaskbarWidgetEnabled==enabledBeforeGame&&vm.TaskbarWidgetDock==dockBeforeGame,
                "游戏结束仍保留原组件开关与停靠位置");
            var normalTray=isolatedTray.GameOptimizationVerification;
            Check(!normalTray.IsGameOptimized&&!normalTray.OptimizedIconSelected&&normalTray.EnabledMenuActions.Contains("退出")&&
                normalTray.EnabledMenuActions.Contains("账号头像栏")&&!normalTray.ShellEnabled,
                "游戏结束后恢复普通图标与常用托盘菜单，仍未加入系统托盘");
            vm.SetDemoGameForVerification(new SteamGameSnapshot(activeAccount.SteamId,730,demoGameName,true,true));
            widgetToggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,false);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(!vm.TaskbarWidgetEnabled&&widgetToggle.IsChecked==false,"游戏期间允许用户手动关闭组件");
            vm.SetDemoGameForVerification(SteamGameSnapshot.None(activeAccount.SteamId,true));
            Check(!vm.TaskbarWidgetEnabled&&!vm.TaskbarWidgetTemporarilyHidden&&vm.TaskbarWidgetDock==dockBeforeGame,
                "游戏结束不重新开启用户手动关闭的组件");
        }
        finally
        {
            vm.SetDemoGameForVerification(SteamGameSnapshot.None(activeAccount.SteamId,true));
            vm.TaskbarWidgetEnabled=enabledBeforeGame;
            vm.SelectedAccount=selectedBeforeGame;
            vm.ShowAccountsCommand.Execute(null);
            window.Width=1280; window.Height=820;
        }
        Check(vm.AllAccounts.Count==accountCount&&managedAccount.Note==savedNote&&vm.TaskbarWidgetEnabled==enabledBeforeGame&&
            vm.TaskbarWidgetDock==dockBeforeGame&&!vm.IsGameRunning,"游戏与开发历程验证结束保留账号资料并恢复演示设置");
        vm.ShowHistoryCommand.Execute(null);
        vm.FilterActivityCommand.Execute("all"); vm.ActivitySearchText="";
        await Capture("23-operations");
        Check(vm.Activity.Any(entry=>entry.EffectiveCategory=="account"&&entry.Kind=="success"&&entry.Title=="添加账号")&&
            vm.Activity.Any(entry=>entry.EffectiveCategory=="account"&&entry.Kind=="success"&&entry.Title=="修改备注")&&
            vm.Activity.Any(entry=>entry.EffectiveCategory=="group"&&entry.Kind=="success")&&
            vm.Activity.Any(entry=>entry.EffectiveCategory=="settings"&&entry.Kind=="success")&&
            vm.Activity.Any(entry=>entry.EffectiveCategory=="login"&&entry.Kind=="info"&&entry.Title.Contains("演示",StringComparison.Ordinal)),
            "操作记录包含成功的账号、备注、分组、设置操作及明确标注的演示切换请求");
        Check(vm.ActivityView.Cast<ActivityEntry>().Count()==vm.Activity.Count,"全部类型与空关键词展示完整操作记录");
        vm.FilterActivityCommand.Execute("account");
        Check(vm.ActivityCategory=="account"&&vm.ActivityView.Cast<ActivityEntry>().Any()&&
            vm.ActivityView.Cast<ActivityEntry>().All(entry=>entry.EffectiveCategory=="account"),
            "账号管理类型仅展示匹配的操作记录");
        string activityNeedle=vm.ActivityView.Cast<ActivityEntry>().First().Title;
        vm.ActivitySearchText=activityNeedle;
        await Capture("24-filtered-operations");
        var visibleActivities=vm.ActivityView.Cast<ActivityEntry>().ToArray();
        Check(visibleActivities.Length>0&&visibleActivities.All(entry=>entry.EffectiveCategory=="account"&&
            entry.Title.Contains(activityNeedle,StringComparison.OrdinalIgnoreCase)),"类型与关键词同时筛选匹配记录");
        static string ActivityKey(ActivityEntry entry)=>JsonSerializer.Serialize(new {entry.Time,entry.Title,entry.Description,entry.Kind,entry.Category});
        var allActivityKeys=vm.Activity.Select(ActivityKey).ToArray();
        var visibleActivityKeys=visibleActivities.Select(ActivityKey).ToHashSet(StringComparer.Ordinal);
        var expectedRemainingKeys=allActivityKeys.Where(key=>!visibleActivityKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);
        vm.ClearActivityCommand.Execute(null);
        Check(vm.PendingConfirmation&&vm.Activity.Select(ActivityKey).SequenceEqual(allActivityKeys),
            "清除当前结果先要求确认，确认前不删除任何记录");
        vm.CancelConfirmCommand.Execute(null);
        Check(!vm.PendingConfirmation&&vm.Activity.Select(ActivityKey).SequenceEqual(allActivityKeys),"取消清除保持全部操作记录");
        vm.ClearActivityCommand.Execute(null); vm.ConfirmCommand.Execute(null);
        while(vm.IsBusy) await Task.Delay(40);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(!vm.PendingConfirmation&&vm.Activity.Count==expectedRemainingKeys.Count&&
            expectedRemainingKeys.SetEquals(vm.Activity.Select(ActivityKey)),"确认清除仅删除当前匹配结果，保留其他类型记录");
        Check(vm.AllAccounts.Count==accountCount&&managedAccount.Note==savedNote&&vm.TaskbarWidgetEnabled==enabledBeforeGame&&
            vm.TaskbarWidgetDock==dockBeforeGame,"清除操作记录不修改账号、备注或任务栏偏好");
        vm.FilterActivityCommand.Execute("all"); vm.ActivitySearchText="";
        vm.ClearActivityCommand.Execute(null);
        Check(vm.PendingConfirmation&&vm.Activity.Count==expectedRemainingKeys.Count,"清除全部记录仍须用户确认");
        vm.ConfirmCommand.Execute(null);
        while(vm.IsBusy) await Task.Delay(40);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(vm.Activity.Count==0&&vm.ActivityView.IsEmpty&&vm.ActivityEmpty&&!vm.PendingConfirmation,
            "确认清除全部后记录为空，清除动作不生成新的操作记录");
        vm.FilterActivityCommand.Execute("all"); vm.ActivitySearchText=""; vm.ShowAccountsCommand.Execute(null);

        vm.ShowStatus("自动关闭旧提示","成功提示验证。","success");
        Check(vm.OperationVisible&&vm.StatusDismissScheduled&&vm.StatusDismissSeconds==6,
            "成功提示按6秒策略调度自动消失");
        await Task.Delay(2100);
        vm.ShowStatus("自动关闭新提示","新提示替换旧提示并重新计时。","info");
        Check(vm.OperationVisible&&vm.StatusDismissScheduled&&vm.StatusDismissSeconds==6,"普通提示替换旧提示后重新计时6秒");
        await Task.Delay(4100);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(vm.OperationVisible&&vm.StatusTitle=="自动关闭新提示"&&vm.StatusDismissScheduled,
            "超过旧提示截止时间后新提示仍保持显示");
        await Task.Delay(2100);
        await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(!vm.OperationVisible&&!vm.StatusDismissScheduled,"真实调度在新提示6秒后自动收起状态区域");
        vm.ShowStatus("错误提示验证","错误保留更长阅读时间。","error");
        Check(vm.OperationVisible&&vm.StatusDismissScheduled&&vm.StatusDismissSeconds==12,"错误提示使用12秒自动关闭策略");
        vm.DismissStatusCommand.Execute(null);
        Check(!vm.OperationVisible&&!vm.StatusDismissScheduled,"手动关闭提示同时取消自动关闭调度");
        var globalRestoreFocusBefore=Keyboard.DefaultRestoreFocusMode;
        var lifecycleWindow=new MainWindow(demo:true,allowAvatarNetwork:false,enableShellIntegration:false,persistTaskbarPosition:false)
            { Width=1120,Height=740,Left=80,Top=80,WindowStartupLocation=WindowStartupLocation.Manual };
        var lifecycleVm=(MainViewModel)lifecycleWindow.DataContext;
        bool oldViewCollected=false,oldSearchCollected=false,resourceGraphDetached=false;
        try
        {
            lifecycleWindow.Show();
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>lifecycleWindow.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            Check(lifecycleVm.IsDemo&&!lifecycleWindow.ShellIntegrationEnabled,"界面生命周期使用独立隔离演示窗口");
            var lifecycleAccount=lifecycleVm.AllAccounts.First(a=>a.IsActive);
            lifecycleVm.SelectedAccount=lifecycleAccount;
            lifecycleVm.FilterFavoriteCommand.Execute(null); lifecycleVm.SearchText="northwind";
            const string unsavedNote="未保存备注 · 后台界面回收验证";
            string persistedNote=lifecycleAccount.Note;
            lifecycleVm.NoteDraft=unsavedNote;
            lifecycleVm.TaskbarWidgetEnabled=false; lifecycleVm.TaskbarWidgetDock="left";
            var oldSearch=FocusSearchForVerification(lifecycleWindow);
            lifecycleVm.ShowAboutCommand.Execute(null);
            lifecycleWindow.HideToBackground();
            Check(!lifecycleWindow.ReleaseGameViewResourcesForVerification()&&lifecycleWindow.Content is not null&&
                lifecycleWindow.ViewReleaseCount==0,"没有运行游戏时不卸载主界面");
            lifecycleWindow.ShowFromTray();
            lifecycleVm.SetDemoGameForVerification(new SteamGameSnapshot(lifecycleAccount.SteamId,730,"界面回收游戏（演示）",true,true));
            Check(!lifecycleWindow.ReleaseGameViewResourcesForVerification(),"主界面可见时运行游戏也不卸载资源");
            lifecycleWindow.HideToBackground(); lifecycleWindow.ShowQuickAccounts();
            lifecycleWindow.QuickAccountsWindowForVerification!.DismissOnDeactivate=false;
            Check(!lifecycleWindow.ReleaseGameViewResourcesForVerification(),"快捷头像栏可见时不卸载游戏主界面");
            lifecycleWindow.CloseQuickAccounts(); lifecycleWindow.ShowFromTray();
            double width=lifecycleWindow.Width,height=lifecycleWindow.Height,left=lifecycleWindow.Left,top=lifecycleWindow.Top;
            lifecycleWindow.HideToBackground();
            var oldView=CreateWeakViewReference(lifecycleWindow);
            var oldCreator=CreateWeakNamedViewReference(lifecycleWindow,"CreatorAvatar");
            Check(lifecycleWindow.ReleaseGameViewResourcesForVerification()&&lifecycleWindow.ViewResourcesReleased&&
                lifecycleWindow.Content is null&&lifecycleWindow.Resources.Count==0&&lifecycleWindow.FindName("CreatorAvatar") is null&&
                lifecycleWindow.ViewReleaseCount==1&&ReferenceEquals(lifecycleWindow.DataContext,lifecycleVm),
                "隐藏游戏主界面卸载视觉树、资源和名称引用，保留同一账号视图模型");
            Check(AvatarImageConverter.CachedImageCountForVerification==0,"卸载清除头像强引用缓存");
            Check(FocusManager.GetFocusedElement(lifecycleWindow) is null,"卸载清除主窗口逻辑焦点对旧控件的引用");
            Check(!lifecycleWindow.ReleaseGameViewResourcesForVerification()&&lifecycleWindow.ViewReleaseCount==1,
                "重复卸载不重复计数或破坏状态");
            Check(ReferenceEquals(lifecycleVm.SelectedAccount,lifecycleAccount)&&lifecycleVm.FilterFavorite&&
                lifecycleVm.SearchText=="northwind"&&lifecycleVm.IsAboutView&&lifecycleVm.NoteDraft==unsavedNote&&
                lifecycleAccount.Note==persistedNote&&!lifecycleVm.TaskbarWidgetEnabled&&lifecycleVm.TaskbarWidgetDock=="left",
                "卸载保留选中账号、筛选、关于页、未保存草稿及组件开关和位置");
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            // This collection is restricted to the isolated verification fixture; production never forces GC.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            oldViewCollected=!oldView.IsAlive; oldSearchCollected=!oldSearch.IsAlive;
            var detachedView=CaptureDetachedViewSnapshot(oldView,oldCreator,oldSearch);
            Check(detachedView.RootVisualChildren==0&&detachedView.RootLogicalControlChildren==0&&
                detachedView.RootResourcesCleared&&detachedView.RootDataContextCleared,
                "旧主界面根节点已无视觉或逻辑子控件，资源与数据上下文已清空");
            Check(detachedView.CreatorImageSourceCleared,"旧作者头像图像源已释放，或头像对象已被回收");
            Check(detachedView.SearchParentsDetached&&detachedView.SearchLocalBindingCount==0&&detachedView.SearchDataContextCleared,
                "旧输入控件已脱离父链并移除本地绑定和数据上下文，不再保留账号界面");
            resourceGraphDetached=true;
            lifecycleWindow.ShowQuickAccounts();
            var lifecycleQuick=lifecycleWindow.QuickAccountsWindowForVerification??throw new InvalidOperationException("回收后头像栏未创建");
            lifecycleQuick.DismissOnDeactivate=false;
            lifecycleQuick.ViewModel.SearchText="northwind";
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>lifecycleQuick.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            Check(lifecycleQuick.IsVisible&&lifecycleQuick.ViewModel.Accounts.Cast<SteamAccount>().Count()==1&&
                lifecycleWindow.ViewResourcesReleased&&lifecycleWindow.Content is null&&lifecycleWindow.ViewRestoreCount==0,
                "回收后头像栏可独立打开并筛选，主界面保持未重建");
            lifecycleQuick.ViewModel.ClearSearchCommand.Execute(null);
            Check(lifecycleQuick.ViewModel.Accounts.Cast<SteamAccount>().Count()==3,"回收后的头像栏清除搜索正常操作");
            lifecycleWindow.CloseQuickAccounts(); lifecycleWindow.ShowFromTray();
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>lifecycleWindow.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            Check(!lifecycleWindow.ViewResourcesReleased&&lifecycleWindow.Content is not null&&
                lifecycleWindow.ViewRestoreCount==1&&ReferenceEquals(lifecycleWindow.DataContext,lifecycleVm),
                "打开主界面重新加载BAML，绑定原有视图模型");
            Check(Math.Abs(lifecycleWindow.Width-width)<1&&Math.Abs(lifecycleWindow.Height-height)<1&&
                Math.Abs(lifecycleWindow.Left-left)<1&&Math.Abs(lifecycleWindow.Top-top)<1&&
                lifecycleVm.IsAboutView&&lifecycleVm.NoteDraft==unsavedNote&&lifecycleVm.SearchText=="northwind"&&
                lifecycleVm.FilterFavorite&&!lifecycleVm.TaskbarWidgetEnabled&&lifecycleVm.TaskbarWidgetDock=="left",
                "重建恢复原窗口尺寸位置、选中页面、草稿与全部筛选和组件偏好");
            var restoredAvatar=(Image)lifecycleWindow.FindName("CreatorAvatar");
            Check(restoredAvatar.IsVisible&&restoredAvatar.Source is BitmapSource { PixelWidth:>0 and <=208,PixelHeight:>0 and <=208 },
                "重建关于页作者头像按不超过208像素解码");
            await CapturePanel(lifecycleWindow,Path.Combine(output,"31-view-restored-about.png")); screenshotCount++;
            lifecycleVm.ShowSettingsCommand.Execute(null); lifecycleWindow.HideToBackground();
            Check(lifecycleWindow.ReleaseGameViewResourcesForVerification()&&lifecycleWindow.ViewReleaseCount==2,
                "再次隐藏游戏窗口可卸载设置页");
            lifecycleWindow.ShowFromTray();
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>lifecycleWindow.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            var restoredToggle=(System.Windows.Controls.Primitives.ToggleButton)lifecycleWindow.FindName("TaskbarWidgetToggle");
            Check(lifecycleWindow.ViewRestoreCount==2&&lifecycleVm.IsSettingsView&&restoredToggle.IsChecked==false&&
                lifecycleVm.NoteDraft==unsavedNote&&lifecycleVm.TaskbarWidgetDock=="left",
                "设置页重建保留选中页面、组件关闭状态和未保存备注");
            var restoredChrome=lifecycleWindow.NativeChromeVerification;
            Check(!lifecycleWindow.CanMaximize&&lifecycleWindow.WindowStyle==WindowStyle.None&&!lifecycleWindow.AllowsTransparency&&
                lifecycleWindow.ResizeMode==ResizeMode.CanResize&&restoredChrome.HasCustomTitleBar&&
                restoredChrome.NativeMaximizeBoxRemoved&&restoredChrome.SystemMaximizeEntryRemoved&&restoredChrome.NativeResizeAvailable&&
                Descendants((FrameworkElement)lifecycleWindow.Content!).OfType<Button>().Count(button=>button.Name.StartsWith("Title",StringComparison.Ordinal))==2,
                "两次BAML重建后自定义标题栏、双按钮及禁用最大化和边缘缩放仍保留");
            Check(lifecycleWindow.RequestNativeMaximizeForVerification()&&lifecycleWindow.DispatchChromeMessageForVerification(0x00A3,2)&&
                lifecycleWindow.WindowState==WindowState.Normal,"两次重建后原生最大化和标题栏双击继续被阻止");
            var restoredMinButton=(Button)lifecycleWindow.FindName("TitleMinimizeButton");
            var restoredMinCenter=restoredMinButton.TransformToAncestor(lifecycleWindow).Transform(new Point(restoredMinButton.ActualWidth/2,restoredMinButton.ActualHeight/2));
            Check(lifecycleWindow.GetNativeHitTestForVerification(new Point(180,22))==2&&
                lifecycleWindow.GetNativeHitTestForVerification(restoredMinCenter)==1&&
                lifecycleWindow.GetNativeHitTestForVerification(new Point(1,lifecycleWindow.ActualHeight/2)) is 10 or 13 or 16,
                "两次重建后标题栏拖动、按钮点击及边缘原生命中仍正确");
            File.WriteAllText(Path.Combine(output,"view-restored-window-chrome.json"),JsonSerializer.Serialize(lifecycleWindow.NativeChromeVerification,new JsonSerializerOptions {WriteIndented=true}));
            lifecycleVm.ShowAccountsCommand.Execute(null);
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>lifecycleWindow.UpdateLayout(),DispatcherPriority.ApplicationIdle);
            Check(lifecycleWindow.Content is DependencyObject restoredContent&&Descendants(restoredContent).OfType<TextBox>().Any(box=>box.IsVisible&&box.Text==unsavedNote),
                "账号页重建后未保存备注重新出现在编辑框中");
            var restoredNoteEditor=Descendants((FrameworkElement)lifecycleWindow.Content!).OfType<TextBox>().Single(box=>
                box.IsVisible&&System.Windows.Automation.AutomationProperties.GetName(box)=="账号备注");
            Check(restoredNoteEditor.Focus()&&ReferenceEquals(Keyboard.Focus(restoredNoteEditor),restoredNoteEditor)&&
                ReferenceEquals(Keyboard.FocusedElement,restoredNoteEditor),"两次重建后备注编辑框可取得自身键盘焦点");
            const string editedRestoredDraft="未保存备注 · 后台界面回收验证 · 恢复后输入";
            restoredNoteEditor.SelectAll(); restoredNoteEditor.SelectedText=editedRestoredDraft;
            await lifecycleWindow.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Check(restoredNoteEditor.Text==editedRestoredDraft&&lifecycleVm.NoteDraft==editedRestoredDraft&&
                lifecycleAccount.Note==persistedNote&&System.Windows.Data.BindingOperations.IsDataBound(restoredNoteEditor,TextBox.TextProperty),
                "重建后输入正常更新双向绑定草稿，保留未保存状态与原账号备注");
            await CapturePanel(lifecycleWindow,Path.Combine(output,"32-view-restored-draft.png")); screenshotCount++;
            lifecycleVm.SetDemoGameForVerification(SteamGameSnapshot.None(lifecycleAccount.SteamId,true));
            lifecycleWindow.HideToBackground();
            Check(!lifecycleWindow.ReleaseGameViewResourcesForVerification()&&!lifecycleWindow.ViewResourcesReleased&&
                lifecycleWindow.Content is not null,"游戏结束后隐藏界面保持正常资源，不再次卸载");
            File.WriteAllText(Path.Combine(output,"view-resource-lifecycle.json"),JsonSerializer.Serialize(new {
                Passed=true,ReleaseCount=lifecycleWindow.ViewReleaseCount,RestoreCount=lifecycleWindow.ViewRestoreCount,
                OldViewCollectedInIsolatedTest=oldViewCollected,OldSearchCollectedInIsolatedTest=oldSearchCollected,
                ResourceGraphDetachedVerified=resourceGraphDetached,RetainedSentinelsAreEmpty=resourceGraphDetached,DetachedView=detachedView,ProductionForcesGc=false,UnsavedDraftPreserved=true,RestoredKeyboardEditingVerified=true,
                MainViewRebuiltFromBaml=true,QuickChooserWorksWithoutRebuild=true,AvatarMaximumDecodedPixels=208,
                Mode="isolated demo window; no Steam, native taskbar, network or user settings changes"
            },new JsonSerializerOptions {WriteIndented=true}));
        }
        finally
        {
            lifecycleWindow.CloseQuickAccounts();
            lifecycleVm.SetDemoGameForVerification(SteamGameSnapshot.None(lifecycleVm.AllAccounts.First(a=>a.IsActive).SteamId,true));
            lifecycleWindow.Close(); window.ShowFromTray();
        }
        CheckBindings(output);
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { Passed=true,Checks=checks,CheckCount=checks.Count,
            Screenshots=screenshotCount,AboutVerified=true,DevelopmentStages=developmentStages.Items.Count,GameAwarenessVerified=true,
            NormalGameMonitoringIntervalSeconds=5,InGameMonitoringIntervalSeconds=20,GameSnapshotsSynthetic=true,
            OperationRecordsVerified=true,StatusAutoDismissVerified=true,StartupOfflineStatusVerified=true,ConfirmedOfflineLoginVerified=true,BackgroundEfficiencyPolicyVerified=true,UnchangedSnapshotNotificationsVerified=true,
            ViewResourceLifecycleVerified=true,ResourceGraphDetachedVerified=resourceGraphDetached,RetainedSentinelsAreEmpty=resourceGraphDetached,RestoredKeyboardEditingVerified=true,OldViewCollectedInIsolatedTest=oldViewCollected,OptimizedTrayVerified=true,TrayPreviewScreenshots=Directory.EnumerateFiles(Path.Combine(output,"25-optimized-tray"),"*.png").Count()+
                Directory.EnumerateFiles(Path.Combine(output,"30-offline-tray"),"*.png").Count(),
            Mode="isolated demo; no real Steam launch, game launch, network, shell integration, or config changes"},new JsonSerializerOptions { WriteIndented=true }));
    }
}

