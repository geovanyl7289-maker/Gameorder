using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Gameorder;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SteamService _steam = new();
    private readonly SteamSessionService _sessions = new();
    private SteamSessionSnapshot _session = new(false, null, SteamSessionState.Stopped, "Steam 未启动");
    private readonly bool _allowAvatarNetwork;
    private readonly LocalStore _store;
    private readonly LocalState _local;
    private readonly ObservableCollection<SteamAccount> _accounts = [];
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _avatarRefresh;
    private DateTimeOffset? _avatarRetryAt;
    public Task AvatarLoadTask { get; private set; } = Task.CompletedTask;
    public string AvatarSummary => IsDemo ? "头像样式演示" : _accounts.Any(a=>a.AvatarLoading) ? "正在加载 Steam 头像…" : $"已加载 {_accounts.Count(a=>a.HasAvatar)} 个头像"+(_avatarRetryAt.HasValue ? " · Steam 暂时限流，稍后自动重试" : "");
    private Func<Task>? _confirmation;
    private int _view, _filter;
    private string _searchText="", _steamPath="", _noteDraft="", _settingsMessage="";
    private SteamAccount? _selected;
    private bool _isBusy, _operationVisible, _pendingConfirmation, _isAddAccountOpen;
    private string _statusTitle="", _statusMessage="", _statusKind="info", _confirmationTitle="", _confirmationMessage="";
    private string _newAccountName="", _newDisplayName="", _addFormError="";
    private string _activeFolderId="*", _folderDraftName="", _folderDialogTitle="", _folderFormError="", _renameFolderId="";
    private bool _isFolderDialogOpen, _isEditAccountOpen;
    private string _editDisplayName="", _editNote="", _editFormError="";
    private SteamAccount? _editingAccount;
    private AccountFolder? _editFolderChoice;
    private string _taskbarWidgetSettingsMessage="";
    public bool TaskbarWidgetEnabled
    {
        get=>_local.TaskbarWidgetEnabled;
        set=>SaveTaskbarPreferences(value,TaskbarWidgetDock);
    }
    public string TaskbarWidgetDock
    {
        get=>_local.TaskbarWidgetDock;
        set=>SaveTaskbarPreferences(TaskbarWidgetEnabled,LocalAccountLibrary.NormalizeTaskbarWidgetDock(value));
    }
    public bool TaskbarWidgetDockIsTray=>TaskbarWidgetDock=="tray";
    public bool TaskbarWidgetDockIsLeft=>TaskbarWidgetDock=="left";
    public string TaskbarWidgetDockLabel=>TaskbarWidgetDockIsLeft?"任务栏左侧":"托盘侧";
    public string TaskbarWidgetSettingsMessage { get=>_taskbarWidgetSettingsMessage; private set=>Set(ref _taskbarWidgetSettingsMessage,value); }
    public ObservableCollection<AccountFolder> FolderEntries { get; } = [];
    public ObservableCollection<AccountFolder> FolderChoices { get; } = [];
    public string ActiveFolderId { get=>_activeFolderId; private set { if(Set(ref _activeFolderId,value)) { Notify(nameof(FolderTitle)); RefreshFilter(); Requery(); } } }
    public string FolderTitle => ActiveFolderId=="*" ? "全部账号" : LocalAccountLibrary.FolderName(_local,ActiveFolderId);
    public bool IsFolderDialogOpen { get=>_isFolderDialogOpen; private set { if(Set(ref _isFolderDialogOpen,value)) { Notify(nameof(HasManagementDialog)); Requery(); } } }
    public bool IsEditAccountOpen { get=>_isEditAccountOpen; private set { if(Set(ref _isEditAccountOpen,value)) { Notify(nameof(HasManagementDialog)); Requery(); } } }
    public bool HasManagementDialog => IsFolderDialogOpen||IsEditAccountOpen;
    public string FolderDraftName { get=>_folderDraftName; set=>Set(ref _folderDraftName,value); }
    public string FolderDialogTitle { get=>_folderDialogTitle; private set=>Set(ref _folderDialogTitle,value); }
    public string FolderFormError { get=>_folderFormError; private set=>Set(ref _folderFormError,value); }
    public string EditDisplayName { get=>_editDisplayName; set=>Set(ref _editDisplayName,value); }
    public string EditNote { get=>_editNote; set=>Set(ref _editNote,value); }
    public AccountFolder? EditFolderChoice { get=>_editFolderChoice; set=>Set(ref _editFolderChoice,value); }
    public string EditFormError { get=>_editFormError; private set=>Set(ref _editFormError,value); }
    public bool IsDemo { get; }
    public bool IsAccountsView => _view==0;
    public bool IsHistoryView => _view==1;
    public bool IsSettingsView => _view==2;
    public bool IsAboutView => _view==3;
    public string AuthorName => AboutInfo.AuthorName;
    public string AppVersion => $"v{AboutInfo.Version}";
    public IReadOnlyList<ReleaseNote> ReleaseNotes => AboutInfo.ReleaseNotes;
    public string HeaderSubtitle => IsDemo ? "演示账号 · 所有操作仅在此窗口模拟" : "管理本机账号，选择你的下一次登录。";
    public string AccountsSummary => $"{_accounts.Count} 个账号 · {_accounts.Count(a=>a.HasCachedLogin)} 个已记住登录";
    public IReadOnlyCollection<SteamAccount> AllAccounts => _accounts;
    public bool HasActiveAccount => _accounts.Any(a=>a.IsActive);
    public string ActiveAccountName => _accounts.FirstOrDefault(a=>a.IsActive)?.DisplayName ?? "";
    public bool IsSessionOnline => _session.IsOnline;
    public bool IsSessionOffline => _session.IsOffline;
    public string SessionHint => IsDemo ? "演示连接状态；未读取真实 Steam 会话" : _session.Hint;
    public string SessionSummary => IsDemo ? "在线 · 演示" : _session.State switch
    {
        SteamSessionState.Stopped => "Steam 未启动",
        SteamSessionState.Online => "在线",
        SteamSessionState.Offline => "离线",
        SteamSessionState.AwaitingLogin => "登录中",
        _ => "连接待确认"
    };
    public string ClientStatus => SessionSummary;
    public string SteamStatus => IsDemo ? "演示环境，未连接 Steam" : IsPathValid ? "已找到 Steam 客户端" : "尚未找到 Steam，请前往设置指定路径";
    private bool IsPathValid => !string.IsNullOrWhiteSpace(SteamPath) && File.Exists(Path.Combine(SteamPath,"steam.exe"));
    public string SteamPath { get=>_steamPath; set { if (Set(ref _steamPath,value)) { Notify(nameof(SteamStatus)); Requery(); } } }
    public string SearchText { get=>_searchText; set { if (Set(ref _searchText,value)) RefreshFilter(); } }
    public bool FilterAll => _filter==0;
    public bool FilterCached => _filter==1;
    public bool FilterFavorite => _filter==2;
    public ICollectionView Accounts { get; }
    public SteamAccount? SelectedAccount { get=>_selected; set { if (Set(ref _selected,value)) { NoteDraft=value?.Note ?? ""; Notify(nameof(HasSelection)); Notify(nameof(NoSelection)); Requery(); } } }
    public bool HasSelection => SelectedAccount is not null;
    public bool NoSelection => !HasSelection;
    public bool IsEmpty => Accounts.IsEmpty;
    public string EmptyTitle => _accounts.Count==0 ? "从你的第一个账号开始" : "没有匹配的账号";
    public string EmptyDescription => _accounts.Count==0 ? "导入 Steam 本机保存的账号，或手动添加一个登录名。" : "试试其他关键词，或切换筛选条件。";
    public string NoteDraft { get=>_noteDraft; set=>Set(ref _noteDraft,value); }
    public string SettingsMessage { get=>_settingsMessage; set=>Set(ref _settingsMessage,value); }
    public ObservableCollection<ActivityEntry> Activity { get; }
    public bool ActivityEmpty => Activity.Count==0;
    public bool IsBusy { get=>_isBusy; private set { if(Set(ref _isBusy,value)) { Notify(nameof(IsNotBusy)); Requery(); } } }
    public bool IsNotBusy => !IsBusy;
    public bool OperationVisible { get=>_operationVisible; private set=>Set(ref _operationVisible,value); }
    public string StatusTitle { get=>_statusTitle; private set=>Set(ref _statusTitle,value); }
    public string StatusMessage { get=>_statusMessage; private set=>Set(ref _statusMessage,value); }
    public string StatusKind { get=>_statusKind; private set=>Set(ref _statusKind,value); }
    public bool PendingConfirmation { get=>_pendingConfirmation; private set { if(Set(ref _pendingConfirmation,value)) Requery(); } }
    public string ConfirmationTitle { get=>_confirmationTitle; private set=>Set(ref _confirmationTitle,value); }
    public string ConfirmationMessage { get=>_confirmationMessage; private set=>Set(ref _confirmationMessage,value); }
    public bool IsAddAccountOpen { get=>_isAddAccountOpen; private set { if(Set(ref _isAddAccountOpen,value)) Requery(); } }
    public string NewAccountName { get=>_newAccountName; set=>Set(ref _newAccountName,value); }
    public string NewDisplayName { get=>_newDisplayName; set=>Set(ref _newDisplayName,value); }
    public string AddFormError { get=>_addFormError; private set=>Set(ref _addFormError,value); }

    public ICommand ShowAccountsCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowAboutCommand { get; }
    public ICommand FilterAllCommand { get; }
    public ICommand FilterCachedCommand { get; }
    public ICommand FilterFavoriteCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand AddAccountCommand { get; }
    public ICommand OnlineCommand { get; }
    public ICommand OfflineCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand BrowseSteamCommand { get; }
    public ICommand DetectSteamCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand DismissStatusCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand SaveNewAccountCommand { get; }
    public ICommand CloseAddAccountCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand RenameFolderCommand { get; }
    public ICommand DeleteFolderCommand { get; }
    public ICommand SaveFolderCommand { get; }
    public ICommand CloseFolderCommand { get; }
    public ICommand EditAccountCommand { get; }
    public ICommand SaveEditAccountCommand { get; }
    public ICommand CloseEditAccountCommand { get; }
    public ICommand TaskbarDockTrayCommand { get; }
    public ICommand TaskbarDockLeftCommand { get; }

    public MainViewModel(bool demo, bool allowAvatarNetwork = true)
    {
        _allowAvatarNetwork=allowAvatarNetwork;
        IsDemo=demo; _store=new(demo); _local=_store.Load();
        Accounts=CollectionViewSource.GetDefaultView(_accounts);
        Accounts.Filter=o=>o is SteamAccount a && (ActiveFolderId=="*"||a.FolderId==ActiveFolderId) && (_filter!=1 || a.HasCachedLogin) && (_filter!=2 || a.IsFavorite) && (string.IsNullOrWhiteSpace(SearchText) || $"{a.DisplayName} {a.AccountName} {a.SteamId} {a.Note}".Contains(SearchText.Trim(),StringComparison.OrdinalIgnoreCase));
        Activity=new(_local.Activity.OrderByDescending(a=>a.Time));
        ShowAccountsCommand=new RelayCommand(()=>SetView(0)); ShowHistoryCommand=new RelayCommand(()=>SetView(1)); ShowSettingsCommand=new RelayCommand(()=>SetView(2)); ShowAboutCommand=new RelayCommand(()=>SetView(3));
        FilterAllCommand=new RelayCommand(()=>SetFilter(0)); FilterCachedCommand=new RelayCommand(()=>SetFilter(1)); FilterFavoriteCommand=new RelayCommand(()=>SetFilter(2));
        RefreshCommand=new RelayCommand(()=>LoadAccounts(true),()=>CanManage);
        AddAccountCommand=new RelayCommand(()=> { NewAccountName=""; NewDisplayName=""; AddFormError=""; IsAddAccountOpen=true; },()=>CanManage);
        OnlineCommand=new AsyncCommand(()=>RequestSwitch(false),()=>CanSwitch(false)); OfflineCommand=new AsyncCommand(()=>RequestSwitch(true),()=>CanSwitch(true));
        ToggleFavoriteCommand=new RelayCommand(ToggleFavorite,()=>HasSelection&&CanManage);
        RemoveAccountCommand=new RelayCommand(RequestRemove,()=>HasSelection&&CanManage);
        SaveNoteCommand=new RelayCommand(SaveNote,()=>HasSelection&&CanManage);
        BrowseSteamCommand=new RelayCommand(BrowseSteam,()=>!IsBusy&&!IsDemo);
        DetectSteamCommand=new RelayCommand(DetectSteam,()=>!IsBusy&&!IsDemo);
        SaveSettingsCommand=new RelayCommand(SaveSettings,()=>!IsBusy);
        RestoreBackupCommand=new AsyncCommand(RequestRestore,()=>!IsBusy&&(IsDemo||(_steam.CanRestoreBackup(SteamPath)&&!_steam.IsSteamRunning)));
        DismissStatusCommand=new RelayCommand(()=>OperationVisible=false,()=>!IsBusy);
        ConfirmCommand=new AsyncCommand(async()=> { var action=_confirmation; _confirmation=null; PendingConfirmation=false; if(action is not null) await action(); });
        CancelConfirmCommand=new RelayCommand(()=> { PendingConfirmation=false; _confirmation=null; });
        SaveNewAccountCommand=new RelayCommand(SaveNewAccount,()=>!IsBusy);
        CloseAddAccountCommand=new RelayCommand(()=>IsAddAccountOpen=false);
        SelectFolderCommand=new ParameterCommand(p=>SelectFolder(p?.ToString() ?? "*"),_=>CanManage);
        CreateFolderCommand=new RelayCommand(()=>OpenFolderDialog(false),()=>CanManage);
        RenameFolderCommand=new RelayCommand(()=>OpenFolderDialog(true),()=>CanManage&&LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId));
        DeleteFolderCommand=new RelayCommand(RequestDeleteFolder,()=>CanManage&&LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId));
        SaveFolderCommand=new RelayCommand(SaveFolder,()=>IsFolderDialogOpen&&!IsBusy);
        CloseFolderCommand=new RelayCommand(()=>IsFolderDialogOpen=false);
        EditAccountCommand=new RelayCommand(OpenEditAccount,()=>HasSelection&&CanManage);
        SaveEditAccountCommand=new RelayCommand(SaveEditAccount,()=>IsEditAccountOpen&&!IsBusy);
        CloseEditAccountCommand=new RelayCommand(()=> { IsEditAccountOpen=false; _editingAccount=null; });
        TaskbarDockTrayCommand=new RelayCommand(()=>SetTaskbarWidgetDockPosition("tray"),()=>!IsBusy);
        TaskbarDockLeftCommand=new RelayCommand(()=>SetTaskbarWidgetDockPosition("left"),()=>!IsBusy);
        SteamPath=demo ? "演示环境" : !string.IsNullOrEmpty(_local.SteamPath) ? _local.SteamPath : _steam.DetectSteamPath();
        if(demo) LoadDemo(); else LoadAccounts(false);
        if(_store.LoadError is not null) ShowStatus("本地数据读取异常",_store.LoadError,"error");
        _timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
        _timer.Tick+=(_,_)=>
        {
            RefreshSession(); Requery();
            if(!IsDemo&&_allowAvatarNetwork&&_avatarRetryAt.HasValue&&DateTimeOffset.UtcNow>=_avatarRetryAt.Value&&AvatarLoadTask.IsCompleted)
            { _avatarRetryAt=null; StartAvatarLoading(); }
        }; _timer.Start();
    }
    private static void Requery()=>CommandManager.InvalidateRequerySuggested();
    private void SetView(int view) { _view=view; Notify(nameof(IsAccountsView)); Notify(nameof(IsHistoryView)); Notify(nameof(IsSettingsView)); Notify(nameof(IsAboutView)); }
    private void SetFilter(int filter) { _filter=filter; Notify(nameof(FilterAll)); Notify(nameof(FilterCached)); Notify(nameof(FilterFavorite)); RefreshFilter(); }
    private void RefreshFilter() { Accounts.Refresh(); Notify(nameof(IsEmpty)); Notify(nameof(EmptyTitle)); Notify(nameof(EmptyDescription)); }
    private void UpdateCounts() { Notify(nameof(AccountsSummary)); Notify(nameof(AvatarSummary)); Notify(nameof(AllAccounts)); RebuildFolders(); RefreshFilter(); RefreshSession(); Requery(); }
    private void RefreshSession()
    {
        _session=IsDemo ? new(true,_accounts.FirstOrDefault()?.SteamId,SteamSessionState.Online,"演示会话") : _sessions.Read(SteamPath);
        foreach(var account in _accounts)
        {
            bool active=!string.IsNullOrEmpty(account.SteamId)&&account.SteamId==_session.ActiveSteamId;
            account.ApplySession(active,_session.State,active ? SessionHint : "此账号当前未登录本机 Steam");
        }
        Notify(nameof(HasActiveAccount)); Notify(nameof(ActiveAccountName)); Notify(nameof(IsSessionOnline));
        Notify(nameof(IsSessionOffline)); Notify(nameof(SessionSummary)); Notify(nameof(SessionHint)); Notify(nameof(ClientStatus));
    }
    private void LoadDemo()
    {
        _accounts.Add(new() { AccountName="northwind", DisplayName="Northwind", SteamId="76561198000000001", HasCachedLogin=true, IsCurrentSelection=true, LastLoginAt=DateTimeOffset.Now.AddHours(-2), IsFavorite=true, Note="主账号 · 和朋友一起玩的游戏", Accent="#E9EFFB" });
        _accounts.Add(new() { AccountName="weekend_player", DisplayName="周末玩家", SteamId="76561198000000002", HasCachedLogin=true, LastLoginAt=DateTimeOffset.Now.AddDays(-1), Note="单机游戏 / 离线使用", Accent="#EEF2E7" });
        _accounts.Add(new() { AccountName="little_orbit", DisplayName="Little Orbit", SteamId="76561198000000003", HasCachedLogin=false, LastLoginAt=DateTimeOffset.Now.AddDays(-8), Accent="#F4EBE4" });
        SelectedAccount=_accounts[0]; UpdateCounts();
    }
    private void LoadAccounts(bool explicitImport)
    {
        if(IsDemo) { ShowStatus("演示环境","当前显示演示账号。正式版会从本机 Steam 导入。","info"); return; }
        try
        {
            IReadOnlyList<SteamAccount> fresh=[];
            bool imported=false;
            if(IsPathValid)
            {
                try { fresh=_steam.ReadAccounts(SteamPath); imported=true; }
                catch(Exception ex) { ShowStatus("Steam 账号读取未完成",ex.Message,"error"); }
            }
            else ShowStatus("设置 Steam 路径","请在设置中选择 steam.exe 所在目录，再导入本机账号。本地添加的账号仍可管理。","info");
            var keep=SelectedAccount?.AccountName;
            if(explicitImport&&imported)
            {
                var candidate=LocalAccountLibrary.Copy(_local); candidate.HiddenAccounts.Clear();
                candidate.Activity=Activity.Take(100).ToList(); _store.Save(candidate); _local.HiddenAccounts=candidate.HiddenAccounts;
            }
            _accounts.Clear();
            foreach(var account in fresh)
            {
                if(_local.HiddenAccounts.Contains(account.AccountName,StringComparer.OrdinalIgnoreCase)) continue;
                ApplyMetadata(account); _accounts.Add(account);
            }
            foreach(var metadata in _local.Accounts.Where(m=>m.IsManual))
            {
                if(_accounts.Any(a=>a.AccountName.Equals(metadata.AccountName,StringComparison.OrdinalIgnoreCase)) || _local.HiddenAccounts.Contains(metadata.AccountName,StringComparer.OrdinalIgnoreCase)) continue;
                var account=new SteamAccount { AccountName=metadata.AccountName, DisplayName=metadata.DisplayName }; ApplyMetadata(account); _accounts.Add(account);
            }
            SelectedAccount=_accounts.FirstOrDefault(a=>a.AccountName==keep) ?? _accounts.FirstOrDefault(a=>a.IsCurrentSelection) ?? _accounts.FirstOrDefault();
            UpdateCounts();
            StartAvatarLoading();
            if(explicitImport&&imported&&SaveLocal()) ShowStatus("导入完成",$"已读取 {_accounts.Count} 个本机账号，分组与备注已保留。","success");
        }
        catch(Exception ex) { ShowStatus("导入未完成",ex.Message,"error"); }
    }
    private void ApplyMetadata(SteamAccount account)
    {
        LocalAccountLibrary.ApplyMetadata(_local,account);
        string[] colors=["#E9EFFB","#EEF2E7","#F4EBE4","#ECEAF5","#E7F1F2"];
        int hash=account.AccountName.Aggregate(0,(h,c)=>unchecked(h*31+c)); account.Accent=colors[(int)((uint)hash%(uint)colors.Length)];
    }
    private void StartAvatarLoading()
    {
        if(IsDemo) return;
        _avatarRefresh?.Cancel();
        var refresh=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _avatarRefresh=refresh;
        AvatarLoadTask=LoadAvatarsAsync(_accounts.ToArray(),SteamPath,refresh);
    }
    private async Task LoadAvatarsAsync(SteamAccount[] accounts,string steamPath,CancellationTokenSource refresh)
    {
        try
        {
            using var service=new AvatarService(steamPath,IsDemo,allowNetwork:_allowAvatarNetwork,imageValidator:AvatarImageConverter.CanDecode);
            await Task.WhenAll(accounts.Select(async account=>
            {
                if(string.IsNullOrEmpty(account.SteamId)) return;
                account.AvatarLoading=true;
                Notify(nameof(AvatarSummary));
                try
                {
                    var image=await service.LoadAvatarAsync(account.SteamId,refresh.Token);
                    if(!refresh.IsCancellationRequested&&_accounts.Contains(account))
                        account.AvatarPath=AvatarImageConverter.Load(image) is not null ? image : null;
                }
                catch(OperationCanceledException) { }
                catch(Exception) { /* A missing avatar must not interrupt account switching. */ }
                finally { account.AvatarLoading=false; Notify(nameof(AvatarSummary)); }
            }));
        }
        catch(OperationCanceledException) { }
        finally
        {
            if(ReferenceEquals(_avatarRefresh,refresh)) { _avatarRefresh=null; _avatarRetryAt=AvatarService.NextNetworkAttemptUtc; Notify(nameof(AvatarSummary)); }
            refresh.Dispose();
        }
    }
    private AccountMetadata Metadata(SteamAccount a)
    {
        return LocalAccountLibrary.UpsertMetadata(_local,a);
    }
    private bool CanManage => !IsBusy&&!PendingConfirmation&&!IsAddAccountOpen&&!HasManagementDialog;
    private void RebuildFolders()
    {
        if(ActiveFolderId!="*"&&ActiveFolderId!=""&&!LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId)) ActiveFolderId="";
        var choiceId=EditFolderChoice?.Id;
        FolderEntries.Clear(); FolderChoices.Clear();
        FolderEntries.Add(new() { Id="*",Name="全部账号",AccountCount=_accounts.Count,IsSelected=ActiveFolderId=="*" });
        var ungrouped=new AccountFolder { Id="",Name="未分组",AccountCount=_accounts.Count(a=>a.FolderId==""),IsSelected=ActiveFolderId=="" };
        FolderEntries.Add(ungrouped); FolderChoices.Add(ungrouped);
        foreach(var folder in _local.Folders)
        {
            var entry=new AccountFolder { Id=folder.Id,Name=folder.Name,AccountCount=_accounts.Count(a=>a.FolderId==folder.Id),IsSelected=ActiveFolderId==folder.Id };
            FolderEntries.Add(entry); FolderChoices.Add(entry);
        }
        if(IsEditAccountOpen) EditFolderChoice=FolderChoices.FirstOrDefault(f=>f.Id==choiceId) ?? FolderChoices[0];
        Notify(nameof(FolderTitle));
    }
    private void SelectFolder(string id)
    {
        if(id!="*"&&id!=""&&!LocalAccountLibrary.IsCustomFolder(_local,id)) return;
        ActiveFolderId=id; SetView(0);
        foreach(var folder in FolderEntries) folder.IsSelected=folder.Id==id;
        if(SelectedAccount is null||!Accounts.Contains(SelectedAccount)) SelectedAccount=Accounts.Cast<SteamAccount>().FirstOrDefault();
    }
    private void CommitCandidate(LocalState candidate)
    {
        candidate.Activity=Activity.Take(100).ToList();
        _store.Save(candidate);
        _local.SteamPath=candidate.SteamPath; _local.Accounts=candidate.Accounts; _local.Folders=candidate.Folders;
        _local.TaskbarWidgetEnabled=candidate.TaskbarWidgetEnabled; _local.TaskbarWidgetDock=candidate.TaskbarWidgetDock;
        _local.HiddenAccounts=candidate.HiddenAccounts; _local.Activity=candidate.Activity;
        foreach(var account in _accounts) ApplyMetadata(account);
        NoteDraft=SelectedAccount?.Note ?? "";
        UpdateCounts();
    }
    private void OpenFolderDialog(bool rename)
    {
        _renameFolderId=rename ? ActiveFolderId : "";
        FolderDialogTitle=rename ? "重命名分组" : "新建分组";
        FolderDraftName=rename ? FolderTitle : ""; FolderFormError=""; IsFolderDialogOpen=true;
    }
    private void SaveFolder()
    {
        try
        {
            string id=_renameFolderId;
            var candidate=id.Length==0 ? LocalAccountLibrary.CreateFolder(_local,FolderDraftName,out id) : LocalAccountLibrary.RenameFolder(_local,id,FolderDraftName);
            CommitCandidate(candidate); IsFolderDialogOpen=false; SelectFolder(id);
        }
        catch(Exception ex) { FolderFormError=ex.Message; }
    }
    private void RequestDeleteFolder()
    {
        var id=ActiveFolderId; var name=FolderTitle;
        Confirm("删除分组？",$"删除「{name}」，其中账号会移到未分组。账号和备注会保留。",()=>
        {
            try { CommitCandidate(LocalAccountLibrary.DeleteFolder(_local,id)); SelectFolder(""); }
            catch(Exception ex) { ShowStatus("分组未删除",ex.Message,"error"); }
            return Task.CompletedTask;
        });
    }
    private void OpenEditAccount()
    {
        if(SelectedAccount is not { } account) return;
        _editingAccount=account; EditDisplayName=account.DisplayName; EditNote=account.Note; EditFormError="";
        EditFolderChoice=FolderChoices.FirstOrDefault(f=>f.Id==account.FolderId) ?? FolderChoices.FirstOrDefault();
        IsEditAccountOpen=true;
    }
    private void SaveEditAccount()
    {
        if(_editingAccount is not { } account||!_accounts.Contains(account)) { EditFormError="账号已不在账号库中。"; return; }
        try
        {
            var candidate=LocalAccountLibrary.EditAccount(_local,account,EditDisplayName,EditNote,EditFolderChoice?.Id);
            CommitCandidate(candidate); IsEditAccountOpen=false; _editingAccount=null;
            if(ActiveFolderId!="*"&&account.FolderId!=ActiveFolderId) SelectFolder(account.FolderId);
            SelectedAccount=account;
        }
        catch(Exception ex) { EditFormError=ex.Message; }
    }
    private bool SaveLocal()
    {
        try { _local.Activity=Activity.Take(100).ToList(); _store.Save(_local); return true; }
        catch(Exception ex) { ShowStatus("无法保存本地资料",ex.Message,"error"); return false; }
    }
    private void AddActivity(string title,string description,string kind)
    { Activity.Insert(0,new() { Title=title, Description=description,Kind=kind }); while(Activity.Count>100) Activity.RemoveAt(Activity.Count-1); Notify(nameof(ActivityEmpty)); SaveLocal(); }
    public void ShowStatus(string title,string message,string kind="info") { StatusTitle=title; StatusMessage=message; StatusKind=kind; OperationVisible=true; }
    private void ToggleFavorite()
    {
        if(SelectedAccount is not { } a) return;
        try { var candidate=LocalAccountLibrary.Copy(_local); LocalAccountLibrary.UpsertMetadata(candidate,a).IsFavorite=!a.IsFavorite; CommitCandidate(candidate); }
        catch(Exception ex) { ShowStatus("收藏未保存",ex.Message,"error"); }
    }
    private void SaveNote()
    {
        if(SelectedAccount is not { } a) return;
        try { CommitCandidate(LocalAccountLibrary.EditAccount(_local,a,a.DisplayName,NoteDraft,a.FolderId)); ShowStatus("备注已保存",IsDemo?"演示备注仅保留在本次窗口。":"备注只保存在本机。","success"); }
        catch(Exception ex) { ShowStatus("备注未保存",ex.Message,"error"); }
    }
    private void Confirm(string title,string message,Func<Task> action) { ConfirmationTitle=title; ConfirmationMessage=message; _confirmation=action; PendingConfirmation=true; }
    private void RequestRemove()
    {
        if(SelectedAccount is not { } account) return;
        Confirm("移出账号库？",$"将「{account.DisplayName}」从 Gameorder 隐藏。备注、分组和 Steam 登录数据会保留，重新导入可以找回。",()=>
        {
            try
            {
                var candidate=LocalAccountLibrary.Copy(_local); LocalAccountLibrary.UpsertMetadata(candidate,account);
                if(!candidate.HiddenAccounts.Contains(account.AccountName,StringComparer.OrdinalIgnoreCase)) candidate.HiddenAccounts.Add(account.AccountName);
                CommitCandidate(candidate); _accounts.Remove(account); SelectedAccount=Accounts.Cast<SteamAccount>().FirstOrDefault(); UpdateCounts();
            }
            catch(Exception ex) { ShowStatus("账号未移出",ex.Message,"error"); }
            return Task.CompletedTask;
        });
    }
    private bool CanSwitch(bool offline)=>CanManage&&SelectedAccount is not null&&(IsDemo||IsPathValid)&&(!offline||SelectedAccount.HasCachedLogin);
    public Task SelectAndSwitchAsync(string steamId,SwitchMode mode)
    {
        if(!CanManage) return Task.CompletedTask;
        var matches=_accounts.Where(a=>!string.IsNullOrWhiteSpace(steamId)&&a.SteamId==steamId).Take(2).ToArray();
        var account=string.IsNullOrWhiteSpace(steamId) ? SelectedAccount is { } selected&&string.IsNullOrWhiteSpace(selected.SteamId)&&_accounts.Contains(selected) ? selected : null
            : matches.Length==1 ? matches[0] : null;
        if(account is null) { ShowStatus("账号不可用","请刷新账号库后重试。","info"); return Task.CompletedTask; }
        if(ActiveFolderId!="*"&&ActiveFolderId!=account.FolderId) SelectFolder(account.FolderId);
        SetFilter(0); SearchText=""; SelectedAccount=account;
        SetView(0);
        if(!CanSwitch(mode==SwitchMode.Offline))
        { ShowStatus("暂时无法切换",mode==SwitchMode.Offline&&!account.HasCachedLogin ? "此账号需要先在 Steam 在线登录并记住本机登录。" : "请检查 Steam 路径或等待当前操作完成。","info"); return Task.CompletedTask; }
        return RequestSwitch(mode==SwitchMode.Offline);
    }
    private Task RequestSwitch(bool offline)
    {
        if(!CanSwitch(offline)||SelectedAccount is not { } account) return Task.CompletedTask;
        string mode=offline?"离线":"在线";
        string message=$"将为「{account.DisplayName}」发起{mode}启动。"+(!IsDemo&&_steam.IsSteamRunning?"\nSteam 将正常退出并重新启动。请先保存游戏、完成下载中的重要任务。":"")+(offline?"\n离线需要本机有效的已记住登录；部分游戏仍需联网。":"\n如需密码或 Steam Guard 验证，请在 Steam 官方窗口完成。");
        Confirm($"使用{mode}模式切换",message,()=>ExecuteSwitch(account,offline));
        return Task.CompletedTask;
    }
    private async Task ExecuteSwitch(SteamAccount account,bool offline)
    {
        IsBusy=true;
        string mode=offline?"离线":"在线";
        ShowStatus("正在准备 Steam",IsDemo?"模拟账号切换中…":"等待 Steam 正常退出，然后选择账号并重新启动…");
        try
        {
            if(IsDemo)
            {
                await Task.Delay(650,_lifetime.Token);
                ShowStatus("演示操作完成",$"已模拟「{account.DisplayName}」的{mode}启动。Steam 和本机登录配置未改动。","success");
                AddActivity($"{mode}启动 · 演示",account.DisplayName+" · 未启动真实 Steam","info");
            }
            else
            {
                var result=await _steam.SwitchAsync(SteamPath,account,offline,_lifetime.Token);
                LoadAccounts(false);
                ShowStatus($"已请求{mode}启动",result.Message,"success");
                AddActivity($"已请求{mode}启动",account.DisplayName+" · 实际登录结果请查看 Steam 客户端","success");
            }
        }
        catch(OperationCanceledException) { ShowStatus("操作已取消","Steam 账号切换未完成。","info"); }
        catch(Exception ex) { ShowStatus("切换未完成",ex.Message,"error"); AddActivity("切换未完成",ex.Message,"error"); }
        finally { IsBusy=false; RefreshSession(); }
    }
    private void SaveNewAccount()
    {
        var name=NewAccountName.Trim(); var display=NewDisplayName.Trim();
        if(!Regex.IsMatch(name,@"^[A-Za-z0-9_]{2,64}$")) { AddFormError="请输入 2–64 位 Steam 登录名（字母、数字或下划线）。"; return; }
        if(display.Length>80) { AddFormError="显示名称不能超过 80 个字符。"; return; }
        if(_accounts.Any(a=>a.AccountName.Equals(name,StringComparison.OrdinalIgnoreCase))) { AddFormError="这个登录名已在账号库中。"; return; }
        var a=new SteamAccount { AccountName=name,DisplayName=string.IsNullOrWhiteSpace(display)?name:display,FolderId=LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId) ? ActiveFolderId : "" };
        try
        {
            var candidate=LocalAccountLibrary.Copy(_local); candidate.HiddenAccounts.RemoveAll(x=>x.Equals(name,StringComparison.OrdinalIgnoreCase)); LocalAccountLibrary.UpsertMetadata(candidate,a);
            CommitCandidate(candidate); ApplyMetadata(a); _accounts.Add(a); SetFilter(0); SearchText=""; SelectedAccount=a; IsAddAccountOpen=false;
            UpdateCounts(); ShowStatus("账号已添加","点击在线登录，在 Steam 窗口完成首次验证；登录后可重新导入缓存状态。","success");
        }
        catch(Exception ex) { AddFormError=ex.Message; }
    }
    private void BrowseSteam()
    {
        var dialog=new OpenFileDialog { Title="选择 Steam 客户端",Filter="Steam 客户端 (steam.exe)|steam.exe",CheckFileExists=true };
        if(dialog.ShowDialog()==true) SteamPath=Path.GetDirectoryName(dialog.FileName)!;
    }
    private void DetectSteam() { SteamPath=_steam.DetectSteamPath(); SettingsMessage=IsPathValid?"已定位 Steam，保存后重新读取账号。":"未找到安装路径，请手动选择 steam.exe。"; }
    public void SetTaskbarWidgetDockPosition(string dock)=>TaskbarWidgetDock=dock;
    private void SaveTaskbarPreferences(bool enabled,string dock)
    {
        dock=LocalAccountLibrary.NormalizeTaskbarWidgetDock(dock);
        if(enabled==TaskbarWidgetEnabled&&dock==TaskbarWidgetDock) return;
        var candidate=LocalAccountLibrary.SetTaskbarPreferences(_local,enabled,dock);
        candidate.Activity=Activity.Take(100).ToList();
        try
        {
            _store.Save(candidate);
            // Publish only after the atomic file replacement succeeds. Host subscribers see committed values.
            _local.TaskbarWidgetEnabled=candidate.TaskbarWidgetEnabled;
            _local.TaskbarWidgetDock=candidate.TaskbarWidgetDock;
            TaskbarWidgetSettingsMessage=IsDemo?"演示设置，未写入文件":"";
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            TaskbarWidgetSettingsMessage="未保存，请稍后重试";
            ShowStatus("任务栏设置未保存",ex.Message,"error");
        }
        Notify(nameof(TaskbarWidgetEnabled)); Notify(nameof(TaskbarWidgetDock));
        Notify(nameof(TaskbarWidgetDockIsTray)); Notify(nameof(TaskbarWidgetDockIsLeft)); Notify(nameof(TaskbarWidgetDockLabel));
        Requery();
    }
    private void SaveSettings()
    {
        if(IsDemo) { SettingsMessage="演示模式下不会保存 Steam 路径。"; return; }
        var path=SteamPath.Trim().Trim('"'); if(File.Exists(path)&&Path.GetFileName(path).Equals("steam.exe",StringComparison.OrdinalIgnoreCase)) path=Path.GetDirectoryName(path)!;
        if(!Directory.Exists(path)||!File.Exists(Path.Combine(path,"steam.exe"))) { SettingsMessage="目录中未找到 steam.exe，请重新选择。"; return; }
        SteamPath=Path.GetFullPath(path); _local.SteamPath=SteamPath;
        if(SaveLocal()) { SettingsMessage="路径已保存，账号库已更新。"; LoadAccounts(false); }
    }
    private Task RequestRestore()
    {
        Confirm("恢复最近一次切换配置？","Steam 必须完全退出。将恢复最近一次切换前的账号选择和登录模式；Gameorder 备注不受影响。",async()=>
        {
            IsBusy=true;
            try
            {
                if(IsDemo) { ShowStatus("演示恢复完成","未修改真实配置。","success"); return; }
                await Task.Run(()=>_steam.RestoreLatestBackup(SteamPath)); LoadAccounts(false);
                ShowStatus("配置已恢复","你可以重新启动 Steam。","success"); AddActivity("恢复切换配置","已恢复本机最近一次备份","success");
            }
            catch(Exception ex) { ShowStatus("恢复未完成",ex.Message,"error"); }
            finally { IsBusy=false; }
        }); return Task.CompletedTask;
    }
    public void CloseTransient() { if(IsBusy) return; IsAddAccountOpen=false; IsFolderDialogOpen=false; IsEditAccountOpen=false; _editingAccount=null; PendingConfirmation=false; _confirmation=null; }
    public void Dispose() { _timer.Stop(); _lifetime.Cancel(); _avatarRefresh?.Cancel(); _lifetime.Dispose(); }
}
