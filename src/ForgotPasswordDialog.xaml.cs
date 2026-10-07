using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Gameorder;

public partial class ForgotPasswordDialog : Window
{
    private readonly ForgotPasswordFlow _flow;
    private readonly DispatcherTimer _timer=new(DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(50)};
    private bool _holdingWithMouse,_holdingWithKeyboard,_submitting,_closed,_cancelling;
    public MainViewModel ViewModel {get;}
    public ForgotPasswordFlow FlowForVerification=>_flow;
    public ForgotPasswordDialog(MainViewModel viewModel)
    {
        ViewModel=viewModel; _flow=viewModel.CreateForgotPasswordFlow(); InitializeComponent(); DataContext=_flow;
        _timer.Tick+=async(_,_)=>await ObservePressAsync();
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { e.Handled=true; CancelAndClose(); } };
        Deactivated+=(_,_)=>CancelAndClose();
        Closing+=(_,e)=> { if(_submitting) { e.Cancel=true; return; } _cancelling=true;_holdingWithMouse=false;_holdingWithKeyboard=false;_flow.Cancel(); _timer.Stop(); };
        Closed+=(_,_)=> { _closed=true; _timer.Stop(); _flow.Cancel(); };
        Loaded+=(_,_)=>ConfirmationBox.Focus();
    }
    private void ContinueClick(object sender,RoutedEventArgs e)
    {
        if(!_flow.StartCooling()) return;
        _timer.Start(); HoldButton.Focus();
    }
    private void HoldMouseDown(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;
        if(!_flow.BeginHold()) return;
        _holdingWithMouse=true; HoldButton.Focus(); HoldButton.CaptureMouse();
    }
    private void HoldMouseUp(object sender,MouseButtonEventArgs e) { e.Handled=true; if(_holdingWithMouse&&!_submitting) CancelAndClose(); }
    private void HoldMouseMove(object sender,MouseEventArgs e)
    {
        if(!_holdingWithMouse||_submitting) return;
        var position=e.GetPosition(HoldButton);
        if(position.X<0||position.Y<0||position.X>HoldButton.ActualWidth||position.Y>HoldButton.ActualHeight) CancelAndClose();
    }
    private void HoldKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Space or Key.Enter)) return;
        e.Handled=true;
        if(!e.IsRepeat&&_flow.BeginHold()) _holdingWithKeyboard=true;
    }
    private void HoldKeyUp(object sender,KeyEventArgs e)
    { if(e.Key is Key.Space or Key.Enter) { e.Handled=true; if(_holdingWithKeyboard&&!_submitting) CancelAndClose(); } }
    private void HoldLostCapture(object sender,MouseEventArgs e) { if(_holdingWithMouse&&!_submitting) CancelAndClose(); }
    private void HoldLostFocus(object sender,KeyboardFocusChangedEventArgs e) { if(_flow.IsHolding&&!_submitting) CancelAndClose(); }
    private async Task ObservePressAsync()
    {
        if(_closed||_submitting) return;
        _flow.Tick();
        if(!_flow.IsHolding) return;
        if(!_holdingWithMouse&&!_holdingWithKeyboard) return;
        if(_holdingWithMouse&&Mouse.LeftButton!=MouseButtonState.Pressed || _holdingWithKeyboard&&
            !Keyboard.IsKeyDown(Key.Space)&&!Keyboard.IsKeyDown(Key.Enter)) { CancelAndClose(); return; }
        var consent=_flow.CompleteHold();
        if(consent is null) return;
        _submitting=true; _timer.Stop(); HoldButton.ReleaseMouseCapture(); CancelButton.IsEnabled=false;
        try
        {
            if(await ViewModel.ResetLocalDataAsync(consent))
            { _submitting=false; DialogResult=true; }
            else { ResetError.Text=ViewModel.AppLockMessage; _submitting=false; HoldButton.IsEnabled=false; CancelButton.IsEnabled=true; }
        }
        catch { ResetError.Text="清空未完成。请保留当前窗口并重试，应用锁状态不会被静默解除。"; _submitting=false; CancelButton.IsEnabled=true; }
    }
    private void CancelClick(object sender,RoutedEventArgs e)=>CancelAndClose();
    public void CancelHold()=>CancelAndClose();
    private void CancelAndClose()
    {
        if(_closed||_submitting||_cancelling) return;
        _cancelling=true;_holdingWithMouse=false;_holdingWithKeyboard=false;
        _flow.Cancel(); _timer.Stop();HoldButton.ReleaseMouseCapture(); Close();
    }
}
