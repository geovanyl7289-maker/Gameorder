using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Gameorder;

public partial class QuickAccountWindow : Window
{
    private readonly MainViewModel _mainModel;
    private readonly MainWindow _host;
    private bool _closing;
    private bool _opening;
    public QuickAccountViewModel ViewModel { get; }
    public Func<SteamAccount,SwitchMode,Task>? SwitchRequestHandler { get; set; }
    public bool ConfirmationVisible => _mainModel.PendingConfirmation;
    public bool DismissOnDeactivate { get; set; } = true;
    public double ScrollOffsetForVerification => AccountScroller.VerticalOffset;
    public double ScrollableHeightForVerification => AccountScroller.ScrollableHeight;
    public int VisibleAccountCount => ViewModel.VisibleAccountCount;

    public QuickAccountWindow(MainViewModel viewModel, MainWindow host)
    {
        InitializeComponent();
        _mainModel=viewModel;
        _host=host;
        ViewModel=new(viewModel);
        DataContext=ViewModel;
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key!=Key.Escape) return;
            if(_mainModel.PendingConfirmation) _mainModel.CancelConfirmCommand.Execute(null); else Close();
            e.Handled=true;
        };
        Closing+=(_,_)=> { if(_mainModel.PendingConfirmation&&!_mainModel.IsBusy) _mainModel.CancelConfirmCommand.Execute(null); };
        Deactivated+=(_,_)=>
        {
            if(!DismissOnDeactivate||_closing||_opening||!IsLoaded||!IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if(DismissOnDeactivate&&!_closing&&!_opening&&IsVisible&&!IsActive) Close();
            },DispatcherPriority.ContextIdle);
        };
        Closed+=(_,_)=> { _closing=true; ViewModel.Dispose(); };
    }

    // The host computes and clamps this rectangle for the taskbar's monitor.
    public void ShowAtTaskbar(Rect bounds)
    {
        if(bounds.IsEmpty||!double.IsFinite(bounds.Left)||!double.IsFinite(bounds.Top)||!double.IsFinite(bounds.Width)||!double.IsFinite(bounds.Height)||bounds.Width<=0||bounds.Height<=0)
            throw new ArgumentException("快捷栏位置无效。",nameof(bounds));
        Left=bounds.Left; Top=bounds.Top;
        Width=Math.Max(MinWidth,bounds.Width); Height=Math.Max(MinHeight,bounds.Height);
        ViewModel.Refresh();
        _opening=true;
        try { Show(); Activate(); }
        finally { Dispatcher.BeginInvoke(() =>_opening=false,DispatcherPriority.ContextIdle); }
    }

    public void OpenFullWindow()
    {
        _closing=true;
        Close();
        _host.ShowFromTray();
        _mainModel.ShowAccountsCommand.Execute(null);
    }

    public async Task RequestSwitchAsync(SteamAccount account,SwitchMode mode)
    {
        // Keep the clicked object, independent of selection, filtering, or later collection changes.
        if(_closing||mode is not (SwitchMode.Online or SwitchMode.Offline)||!ViewModel.CanSwitch||!_mainModel.AllAccounts.Any(a=>ReferenceEquals(a,account))) return;
        if(mode==SwitchMode.Offline&&!account.HasCachedLogin) return;
        _mainModel.SelectedAccount=account;
        if(SwitchRequestHandler is { } handler) await handler(account,mode);
        else await _mainModel.SelectAndSwitchAsync(account.SteamId,mode);
    }

    public Task RequestAccountSwitchAsync(SteamAccount account,SwitchMode mode)=>RequestSwitchAsync(account,mode);

    public void ScrollToEndForVerification() { UpdateLayout(); AccountScroller.ScrollToEnd(); UpdateLayout(); }
    public void ScrollToStartForVerification() { UpdateLayout(); AccountScroller.ScrollToTop(); UpdateLayout(); }
    private void ClosePopupClick(object sender,RoutedEventArgs e)=>Close();
    private void OpenFullWindowClick(object sender,RoutedEventArgs e)=>OpenFullWindow();
    private async void OnlineAccountClick(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext:SteamAccount account }) await RequestSwitchAsync(account,SwitchMode.Online);
    }
    private async void OfflineAccountClick(object sender,RoutedEventArgs e)
    {
        if(sender is FrameworkElement { DataContext:SteamAccount account }) await RequestSwitchAsync(account,SwitchMode.Offline);
    }
}

// This view owns its filters so a quick search never changes the management window.
public sealed class QuickAccountViewModel : ObservableObject,IDisposable
{
    private readonly MainViewModel _host;
    private readonly ObservableCollection<SteamAccount> _source=[];
    private readonly HashSet<SteamAccount> _observed=[];
    private string _searchText="",_folderId="*";
    private bool _disposed;
    public ObservableCollection<AccountFolder> Folders { get; }=[];
    public MainViewModel MainModel => _host;
    public ICollectionView Accounts { get; }
    public string SearchText { get=>_searchText; set { if(Set(ref _searchText,value??"")) RefreshFilter(); } }
    public string ActiveFolderId => _folderId;
    public int VisibleAccountCount => Accounts.Cast<SteamAccount>().Count();
    public bool IsEmpty => Accounts.IsEmpty;
    public string AccountsSummary => $"{VisibleAccountCount} / {_source.Count} 个账号";
    public bool CanSwitch => !_host.IsBusy&&!_host.PendingConfirmation&&!_host.IsAddAccountOpen&&!_host.HasManagementDialog&&
        (_host.IsDemo||System.IO.File.Exists(System.IO.Path.Combine(_host.SteamPath,"steam.exe")));
    public string SessionSummary => _host.SessionSummary;
    public string SessionHint => _host.SessionHint;
    public bool IsSessionOnline => _host.IsSessionOnline;
    public bool IsSessionOffline => _host.IsSessionOffline;
    public bool IsDemo => _host.IsDemo;
    public ICommand SelectFolderCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public IReadOnlyList<SteamAccount> VisibleAccounts => Accounts.Cast<SteamAccount>().ToArray();

    public QuickAccountViewModel(MainViewModel host)
    {
        _host=host;
        Accounts=new ListCollectionView(_source);
        Accounts.Filter=item=>item is SteamAccount account&&(_folderId=="*"||account.FolderId==_folderId)&&
            (string.IsNullOrWhiteSpace(SearchText)||$"{account.DisplayName} {account.AccountName} {account.SteamId} {account.Note}".Contains(SearchText.Trim(),StringComparison.OrdinalIgnoreCase));
        SelectFolderCommand=new ParameterCommand(p=>SelectFolder(p?.ToString()??"*"));
        ClearSearchCommand=new RelayCommand(()=>SearchText="",()=>SearchText.Length>0);
        _host.PropertyChanged+=HostChanged;
        _host.FolderEntries.CollectionChanged+=FoldersChanged;
        if(_host.AllAccounts is INotifyCollectionChanged collection) collection.CollectionChanged+=AccountsChanged;
        Refresh();
    }

    public void SelectFolder(string id)
    {
        if(!Folders.Any(f=>f.Id==id)) id="*";
        _folderId=id;
        foreach(var folder in Folders) folder.IsSelected=folder.Id==id;
        Notify(nameof(ActiveFolderId)); RefreshFilter();
    }

    public void Refresh()
    {
        if(_disposed) return;
        var source=_host.AllAccounts.ToArray();
        foreach(var account in _observed.Where(a=>!source.Contains(a)).ToArray()) { account.PropertyChanged-=AccountChanged; _observed.Remove(account); }
        foreach(var account in source) if(_observed.Add(account)) account.PropertyChanged+=AccountChanged;
        if(!_source.SequenceEqual(source))
        {
            // ListCollectionView processes observable changes immediately; changing its source
            // inside DeferRefresh can throw when it adjusts the current item after an Add.
            _source.Clear();
            foreach(var account in source) _source.Add(account);
        }
        RebuildFolders(); RefreshFilter();
    }

    private void RebuildFolders()
    {
        Folders.Clear();
        Folders.Add(new() { Id="*",Name="全部",AccountCount=_source.Count });
        Folders.Add(new() { Id="",Name="未分组",AccountCount=_source.Count(a=>a.FolderId=="") });
        foreach(var folder in _host.FolderEntries.Where(f=>f.Id!="*"&&f.Id!=""))
            Folders.Add(new() { Id=folder.Id,Name=folder.Name,AccountCount=_source.Count(a=>a.FolderId==folder.Id) });
        if(!Folders.Any(f=>f.Id==_folderId)) _folderId="*";
        foreach(var folder in Folders) folder.IsSelected=folder.Id==_folderId;
        Notify(nameof(ActiveFolderId));
    }

    private void RefreshFilter()
    {
        Accounts.Refresh(); Notify(nameof(IsEmpty)); Notify(nameof(VisibleAccountCount)); Notify(nameof(AccountsSummary));
        CommandManager.InvalidateRequerySuggested();
    }
    private void HostChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(MainViewModel.AllAccounts)) Refresh();
        if(e.PropertyName is nameof(MainViewModel.SessionSummary) or nameof(MainViewModel.SessionHint) or nameof(MainViewModel.IsSessionOnline) or nameof(MainViewModel.IsSessionOffline))
        { Notify(nameof(SessionSummary)); Notify(nameof(SessionHint)); Notify(nameof(IsSessionOnline)); Notify(nameof(IsSessionOffline)); }
        if(e.PropertyName is nameof(MainViewModel.IsBusy) or nameof(MainViewModel.PendingConfirmation) or nameof(MainViewModel.IsAddAccountOpen) or nameof(MainViewModel.HasManagementDialog) or nameof(MainViewModel.SteamPath)) Notify(nameof(CanSwitch));
    }
    private void AccountChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(SteamAccount.FolderId)) { RebuildFolders(); RefreshFilter(); }
        else if(e.PropertyName is nameof(SteamAccount.DisplayName) or nameof(SteamAccount.Note)) RefreshFilter();
    }
    private void FoldersChanged(object? sender,NotifyCollectionChangedEventArgs e)=>Refresh();
    private void AccountsChanged(object? sender,NotifyCollectionChangedEventArgs e)=>Refresh();
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;
        _host.PropertyChanged-=HostChanged;
        _host.FolderEntries.CollectionChanged-=FoldersChanged;
        if(_host.AllAccounts is INotifyCollectionChanged collection) collection.CollectionChanged-=AccountsChanged;
        foreach(var account in _observed) account.PropertyChanged-=AccountChanged;
        _observed.Clear();
    }
}
