using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

namespace Gameorder;

// A preview selection for the taskbar. Browsing accounts never changes Steam or the main selection.
public sealed class TaskbarWidgetViewModel : ObservableObject,IDisposable
{
    private readonly MainViewModel _host;
    private SteamAccount[] _accounts=[];
    private SteamAccount? _selected;
    private bool _previewing,_disposed;
    public IReadOnlyList<SteamAccount> Accounts => _accounts;
    public SteamAccount? SelectedAccount
    {
        get=>_selected;
        set { if(value is null||_accounts.Any(a=>ReferenceEquals(a,value))) SetSelection(value,true); }
    }
    public bool HasAccount => _selected is not null;
    public string DisplayName => _selected?.DisplayName ?? "Gameorder";
    public string Note => _selected?.NotePreview ?? "";
    public bool HasNote => _selected?.HasNote==true;
    public string? AvatarPath => _selected?.AvatarPath;
    public bool HasAvatar => _selected?.HasAvatar==true;
    public string AvatarHint => _selected?.AvatarHint ?? "打开账号管理";
    public string PresenceLabel => _selected?.PresenceLabel ?? "";
    public string PresenceHint => _selected?.PresenceHint ?? "导入 Steam 账号后即可快捷切换";
    public bool IsOnline => _selected?.IsOnline==true;
    public bool IsOffline => _selected?.IsOffline==true;
    public bool IsBusy => _host.IsBusy;
    public bool CanBrowse => _accounts.Length>1&&!_host.IsBusy&&!_host.PendingConfirmation;
    public bool CanOnline => HasAccount&&CanAct&&(_host.IsDemo||System.IO.File.Exists(System.IO.Path.Combine(_host.SteamPath,"steam.exe")));
    public bool CanOffline => CanOnline&&_selected?.HasCachedLogin==true;
    public string CounterLabel => _selected is null ? "0 / 0" : $"{Array.IndexOf(_accounts,_selected)+1} / {_accounts.Length}";
    public string AccountTooltip => _selected is null ? "Gameorder · 打开账号管理" :
        string.Join("\n",new[] { _selected.DisplayName,_selected.AccountName,_selected.Note,_selected.PresenceHint,CounterLabel }.Where(x=>!string.IsNullOrWhiteSpace(x)));
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand FollowCurrentAccountCommand { get; }
    private bool CanAct => !_host.IsBusy&&!_host.PendingConfirmation&&!_host.IsAddAccountOpen&&!_host.HasManagementDialog;

    public TaskbarWidgetViewModel(MainViewModel host)
    {
        _host=host;
        PreviousCommand=new RelayCommand(()=>Move(-1),()=>CanBrowse);
        NextCommand=new RelayCommand(()=>Move(1),()=>CanBrowse);
        FollowCurrentAccountCommand=new RelayCommand(FollowCurrentAccount,()=>_accounts.Length>0);
        _host.PropertyChanged+=HostChanged;
        if(_host.AllAccounts is INotifyCollectionChanged collection) collection.CollectionChanged+=AccountsChanged;
        Refresh();
    }

    public bool SelectAccount(SteamAccount account)
    {
        if(!_accounts.Any(a=>ReferenceEquals(a,account))) return false;
        SetSelection(account,true); return true;
    }

    public void FollowCurrentAccount()
    {
        _previewing=false;
        SetSelection(CurrentAccount(),false);
    }

    public void Refresh()
    {
        if(_disposed) return;
        _accounts=_host.AllAccounts.ToArray();
        if(!_previewing||_selected is null||!_accounts.Any(a=>ReferenceEquals(a,_selected)))
            SetSelection(CurrentAccount(),false);
        NotifyAll();
    }

    private SteamAccount? CurrentAccount()=>_accounts.FirstOrDefault(a=>a.IsOnline)??_accounts.FirstOrDefault(a=>a.IsActive)??
        _accounts.FirstOrDefault(a=>ReferenceEquals(a,_host.SelectedAccount))??_accounts.FirstOrDefault();
    private void Move(int offset)
    {
        if(!CanBrowse) return;
        int index=Array.IndexOf(_accounts,_selected);
        SetSelection(_accounts[((index<0?0:index)+offset+_accounts.Length)%_accounts.Length],true);
    }
    private void SetSelection(SteamAccount? account,bool preview)
    {
        _previewing=preview;
        if(!ReferenceEquals(_selected,account))
        {
            if(_selected is not null) _selected.PropertyChanged-=AccountChanged;
            _selected=account;
            if(_selected is not null) _selected.PropertyChanged+=AccountChanged;
        }
        NotifyAll();
    }
    private void NotifyAll()
    {
        foreach(string name in new[] { nameof(Accounts),nameof(SelectedAccount),nameof(HasAccount),nameof(DisplayName),nameof(Note),nameof(HasNote),nameof(AvatarPath),nameof(HasAvatar),nameof(AvatarHint),nameof(PresenceLabel),nameof(PresenceHint),nameof(IsOnline),nameof(IsOffline),nameof(IsBusy),nameof(CanBrowse),nameof(CanOnline),nameof(CanOffline),nameof(CounterLabel),nameof(AccountTooltip) }) Notify(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void HostChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainViewModel.AllAccounts)) Refresh();
        else if(e.PropertyName is nameof(MainViewModel.ActiveAccountName) or nameof(MainViewModel.HasActiveAccount))
        { if(!_previewing) SetSelection(CurrentAccount(),false); else NotifyAll(); }
        else if(e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.PendingConfirmation) or nameof(MainViewModel.IsAddAccountOpen) or nameof(MainViewModel.HasManagementDialog) or nameof(MainViewModel.SteamPath)) NotifyAll();
    }
    private void AccountChanged(object? sender,PropertyChangedEventArgs e)=>NotifyAll();
    private void AccountsChanged(object? sender,NotifyCollectionChangedEventArgs e)=>Refresh();
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;
        _host.PropertyChanged-=HostChanged;
        if(_host.AllAccounts is INotifyCollectionChanged collection) collection.CollectionChanged-=AccountsChanged;
        if(_selected is not null) _selected.PropertyChanged-=AccountChanged;
    }
}
