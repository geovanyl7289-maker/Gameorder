using System.Windows;
using System.Windows.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Gameorder;

public partial class AppLockDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppLockSettingsAction _action;
    private readonly PasswordRevealController[] _passwordReveals;
    public AppLockDialog(MainViewModel viewModel,AppLockSettingsAction action)
    {
        InitializeComponent(); _viewModel=viewModel; _action=action; DataContext=viewModel;
        MaxHeight=Math.Max(160,SystemParameters.WorkArea.Height-32);
        _passwordReveals=[new(CurrentPasswordBox,CurrentPasswordPlain,CurrentPasswordReveal),new(NewPasswordBox,NewPasswordPlain,NewPasswordReveal),new(RepeatPasswordBox,RepeatPasswordPlain,RepeatPasswordReveal)];
        DialogTitle.Text=action switch {AppLockSettingsAction.Enable=>"设置应用锁",AppLockSettingsAction.ChangePassword=>"更新应用密码",AppLockSettingsAction.ChangeHint=>"修改密码提示",_=>"关闭应用锁"};
        DialogHint.Text=action switch {AppLockSettingsAction.Disable=>"验证当前密码后关闭。",AppLockSettingsAction.ChangeHint=>"验证当前密码后保存提示。",_=>"至少 8 个字符 · 用于解密本机资料\n忘记密码后只能清空资料并重置。"};
        CurrentPasswordSection.Visibility=action==AppLockSettingsAction.Enable?Visibility.Collapsed:Visibility.Visible;
        NewPasswordSection.Visibility=action is AppLockSettingsAction.Enable or AppLockSettingsAction.ChangePassword?Visibility.Visible:Visibility.Collapsed;
        PasswordHintSection.Visibility=action==AppLockSettingsAction.Disable?Visibility.Collapsed:Visibility.Visible;
        PasswordHintBox.Text=action is AppLockSettingsAction.ChangePassword or AppLockSettingsAction.ChangeHint?viewModel.CurrentPasswordHint:"";
        ForgotButton.Visibility=action==AppLockSettingsAction.Enable?Visibility.Collapsed:Visibility.Visible;
        SubmitButton.Content=action==AppLockSettingsAction.Disable?"关闭":"保存";
        PreviewKeyDown+=(_,e)=> { _viewModel.RecordAppActivity(); if(e.Key==Key.Escape) { e.Handled=true; Close(); } };
        PreviewMouseDown+=(_,_)=>_viewModel.RecordAppActivity();
        PreviewMouseWheel+=(_,_)=>_viewModel.RecordAppActivity();
        Loaded+=(_,_)=> { LimitToWorkArea();if(action==AppLockSettingsAction.Enable) NewPasswordBox.Focus(); else CurrentPasswordBox.Focus(); };
        Deactivated+=(_,_)=>HideRevealedPasswords();
        _viewModel.PropertyChanged+=OnViewModelPropertyChanged;
        Closed+=(_,_)=> {ClearPasswords();PasswordHintBox.Clear();_viewModel.PropertyChanged-=OnViewModelPropertyChanged;foreach(var reveal in _passwordReveals)reveal.Dispose();};
    }
    public bool HasRevealedPassword=>_passwordReveals.Any(reveal=>reveal.HasRevealed);
    private void LimitToWorkArea()
    {
        var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
        if(GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle,2),ref info))
            MaxHeight=Math.Max(160,(info.Work.Bottom-info.Work.Top)/VisualTreeHelper.GetDpi(this).DpiScaleY-32);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    private void HideRevealedPasswords() {foreach(var reveal in _passwordReveals)reveal.Hide();}
    private void ClearPasswords() { HideRevealedPasswords();CurrentPasswordBox.Clear(); NewPasswordBox.Clear(); RepeatPasswordBox.Clear(); }
    private void OnViewModelPropertyChanged(object? sender,PropertyChangedEventArgs e)
    {if(e.PropertyName==nameof(MainViewModel.IsLocked)&&_viewModel.IsLocked)ClearPasswords();}
    private void CancelClick(object sender,RoutedEventArgs e)=>Close();
    private void ForgotClick(object sender,RoutedEventArgs e)
    {
        if(!_viewModel.ForgotPasswordCommand.CanExecute(null)) return;
        Close();_viewModel.ForgotPasswordCommand.Execute(null);
    }
    private async void SubmitClick(object sender,RoutedEventArgs e)
    {
        if(_viewModel.IsLocked||_viewModel.AppLockChecking) return;
        HideRevealedPasswords();
        SubmitButton.IsEnabled=false;
        try
        {
            bool success=await _viewModel.ConfigureAppLockAsync(_action,CurrentPasswordBox.Password,NewPasswordBox.Password,RepeatPasswordBox.Password,PasswordHintBox.Text);
            if(success&&IsVisible) DialogResult=true;
        }
        finally { ClearPasswords(); if(IsVisible) SubmitButton.IsEnabled=true; }
    }
}
