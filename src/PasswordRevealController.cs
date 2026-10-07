using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Gameorder;

// The visible copy lives only in this input control while the user requests it.
internal sealed class PasswordRevealController : IDisposable
{
    private readonly PasswordBox _password;
    private readonly TextBox _plain;
    private readonly ToggleButton _eye;
    private bool _syncing, _disposed;
    public bool HasRevealed { get; private set; }

    public PasswordRevealController(PasswordBox password,TextBox plain,ToggleButton eye)
    {
        _password=password; _plain=plain; _eye=eye;
        _plain.MaxLength=_password.MaxLength;
        _plain.IsUndoEnabled=false;
        _password.PasswordChanged+=OnPasswordChanged;
        _plain.TextChanged+=OnPlainChanged;
        _eye.Checked+=OnEyeChanged; _eye.Unchecked+=OnEyeChanged;
        _password.LostKeyboardFocus+=OnFocusLeft;
        _plain.LostKeyboardFocus+=OnFocusLeft;
        _eye.LostKeyboardFocus+=OnFocusLeft;
        Hide();
    }

    private void OnEyeChanged(object sender,RoutedEventArgs e)
    {
        if(_syncing||_disposed) return;
        bool reveal=_eye.IsChecked==true&&_eye.IsEnabled&&_password.IsEnabled;
        SetRevealed(reveal);
        if(reveal) { _plain.Focus(); _plain.CaretIndex=_plain.Text.Length; }
        else _password.Focus();
    }
    private void SetRevealed(bool reveal)
    {
        _syncing=true;
        try
        {
            HasRevealed=reveal; _eye.IsChecked=reveal;
            _plain.Text=reveal?_password.Password:string.Empty;
            _plain.Visibility=reveal?Visibility.Visible:Visibility.Collapsed;
            _password.Visibility=reveal?Visibility.Collapsed:Visibility.Visible;
            _eye.ToolTip=reveal?"隐藏密码":"显示密码";
            AutomationProperties.SetName(_eye,reveal?"隐藏密码":"显示密码");
        }
        finally { _syncing=false; }
    }
    private void OnPasswordChanged(object sender,RoutedEventArgs e)
    {
        if(_syncing||_disposed||!HasRevealed) return;
        _syncing=true;
        try { _plain.Text=_password.Password; }
        finally { _syncing=false; }
    }
    private void OnPlainChanged(object sender,TextChangedEventArgs e)
    {
        if(_syncing||_disposed||!HasRevealed) return;
        _syncing=true;
        try { _password.Password=_plain.Text; }
        finally { _syncing=false; }
    }
    private void OnFocusLeft(object sender,KeyboardFocusChangedEventArgs e)
    {
        // Clicking the eye transfers focus between the three controls first.
        _password.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            if(!_disposed&&HasRevealed&&!_password.IsKeyboardFocusWithin&&!_plain.IsKeyboardFocusWithin&&!_eye.IsKeyboardFocusWithin)
                Hide();
        }));
    }
    public void Hide()
    {
        if(!_disposed) SetRevealed(false);
    }
    public void Dispose()
    {
        if(_disposed) return;
        Hide(); _disposed=true;
        _password.PasswordChanged-=OnPasswordChanged; _plain.TextChanged-=OnPlainChanged;
        _eye.Checked-=OnEyeChanged; _eye.Unchecked-=OnEyeChanged;
        _password.LostKeyboardFocus-=OnFocusLeft; _plain.LostKeyboardFocus-=OnFocusLeft;
        _eye.LostKeyboardFocus-=OnFocusLeft;
    }
}
