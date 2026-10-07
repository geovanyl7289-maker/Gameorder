using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Gameorder;

// A desktop preview uses the same widget without touching Explorer's taskbar or real Steam.
public sealed class ResetTestWidgetPreview : Window
{
    private readonly MainViewModel _model;
    private readonly MainWindow _main;
    private readonly TaskbarAccountWidget _widget;
    private bool _ownerClosing;
    public TaskbarAccountWidget WidgetForVerification=>_widget;
    public ResetTestWidgetPreview(MainViewModel model,MainWindow main)
    {
        if(!model.IsResetTest) throw new InvalidOperationException("测试预览只接受隔离测试数据。");
        _model=model; _main=main;
        Title="Gameorder · 测试小组件"; Width=460; Height=132;
        WindowStyle=WindowStyle.None; ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true; Background=Brushes.Transparent; ShowInTaskbar=false;
        Topmost=true; ShowActivated=false;
        var outer=new Border {CornerRadius=new CornerRadius(18),Background=Brushes.White,
            BorderBrush=new SolidColorBrush(Color.FromRgb(218,229,245)),BorderThickness=new Thickness(1),Padding=new Thickness(17,12,17,12)};
        var stack=new StackPanel(); outer.Child=stack; Content=outer;
        var header=new DockPanel();
        var exit=new Button {Content="退出测试",FontSize=11,Padding=new Thickness(8,3,8,3),Background=Brushes.Transparent,
            BorderBrush=Brushes.Transparent,Foreground=new SolidColorBrush(Color.FromRgb(126,144,165))};
        DockPanel.SetDock(exit,Dock.Right); exit.Click+=(_,_)=>_main.RequestExit(); header.Children.Add(exit);
        var label=new TextBlock {Text="虚拟任务栏 · 不连接 Steam",FontSize=11,FontWeight=FontWeights.SemiBold,
            Foreground=new SolidColorBrush(Color.FromRgb(159,117,48)),VerticalAlignment=VerticalAlignment.Center};
        label.MouseLeftButtonDown+=(_,e)=> { if(e.ClickCount==1) DragMove(); }; header.Children.Add(label); stack.Children.Add(header);
        _widget=new TaskbarAccountWidget(model) {HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,3,0,0)};
        _widget.OpenAccountsRequested+=(_,_)=>_main.ShowQuickAccounts();
        _widget.OpenManagerRequested+=(_,_)=>_main.ShowFromTray();
        _widget.SwitchRequested+=async (_,e)=>await _main.RequestWidgetSwitchAsync(e.Account,e.Mode);
        stack.Children.Add(_widget);
        var hint=new TextBlock {Text="可测试延迟锁、解锁后切换及清空重置",FontSize=10,
            Foreground=new SolidColorBrush(Color.FromRgb(143,158,178)),HorizontalAlignment=HorizontalAlignment.Center}; stack.Children.Add(hint);
        PreviewMouseDown+=(_,_)=>_model.RecordAppActivity();
        PreviewKeyDown+=(_,_)=>_model.RecordAppActivity();
        Loaded+=(_,_)=> { var work=SystemParameters.WorkArea; Left=Math.Max(work.Left,work.Right-Width-20); Top=Math.Max(work.Top,work.Bottom-Height-12); };
        Closing+=(_,e)=> { if(!_ownerClosing) { e.Cancel=true; Dispatcher.BeginInvoke(new Action(_main.RequestExit)); } };
        Closed+=(_,_)=> { _model.PropertyChanged-=ModelChanged; _widget.Dispose(); };
        _model.PropertyChanged+=ModelChanged;
    }
    private void ModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainViewModel.IsGameRunning)) _widget.Visibility=_model.IsGameRunning?Visibility.Hidden:Visibility.Visible;
    }
    public void CloseFromOwner() { _ownerClosing=true; Close(); }
}
