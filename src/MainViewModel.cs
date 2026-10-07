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
    private readonly AppLockService _appLock;
    private readonly TimeProvider _appLockTime;
    private readonly DispatcherTimer _appLockTimer=new(DispatcherPriority.Background) {Interval=TimeSpan.FromSeconds(1)};
    private bool _isLocked,_appLockChecking,_lockPending,_initializing=true;
    private bool _backgroundLockCountdownActive,_isResetting;
    private string _lockPendingReason="";
    private readonly GameorderDataContext? _dataContext;
    private readonly bool _usesPersistentLocalStore;
    private IReadOnlyList<SteamAccount> _publicAccountPreview=[];
    private bool _publicPreviewOnly;
    private long _lastActivityTimestamp,_lockEpoch;
    private int _failedUnlockAttempts;
    private DateTimeOffset _unlockRetryAt;
    private string _appLockMessage="";
    private bool _showPasswordHint;
    public event EventHandler? UnlockRequested;
    public event EventHandler<AppLockSettingsRequestedEventArgs>? AppLockSettingsRequested;
    public event EventHandler? ForgotPasswordRequested;
    public event EventHandler? DataResetCompleted;
    public bool IsLocked {get=>_isLocked; private set { if(Set(ref _isLocked,value)) { Notify(nameof(IsUnlocked)); Notify(nameof(AppLockControlEnabled)); UpdateAppLockTimer(); Requery(); } } }
    public bool IsUnlocked=>!IsLocked;
    public bool AppLockEnabled=>_appLock.Enabled;
    public bool AppLockConfigurationError=>_appLock.ConfigurationError;
    public bool AppLockChecking {get=>_appLockChecking; private set { if(Set(ref _appLockChecking,value)) { Notify(nameof(AppLockControlEnabled)); Requery(); } } }
    public bool AppLockControlEnabled=>!IsLocked&&!IsBusy&&!AppLockChecking&&!IsResetting;
    public string AppLockMessage {get=>_appLockMessage; private set {if(Set(ref _appLockMessage,value))Notify(nameof(HasAppLockMessage));}}
    public bool HasAppLockMessage=>!string.IsNullOrWhiteSpace(AppLockMessage);
    public string CurrentPasswordHint=>_appLock.PasswordHint;
    public bool HasPasswordHint=>!string.IsNullOrWhiteSpace(CurrentPasswordHint);
    public bool ShowPasswordHint {get=>_showPasswordHint&&HasPasswordHint;private set=>Set(ref _showPasswordHint,value);}
    public string AppLockStatus=>AppLockConfigurationError?_appLock.Status:AppLockEnabled?$"已启用 · 后台{(AppLockDelayMinutes==0?"立即锁定":$" {AppLockDelayMinutes} 分钟后锁定")}":"未启用";
    public string AppLockActionLabel=>AppLockEnabled?"更改密码":"启用";
    public bool IsEncrypted=>_store.IsEncrypted;
    public string DataProtectionStatus=>_store.DataProtectionStatus;
    public ICommand LockNowCommand {get;}
    public ICommand AppLockSettingsCommand {get;}
    public ICommand ChangePasswordCommand {get;}
    public ICommand ChangePasswordHintCommand {get;}
    public ICommand DisableLockCommand {get;}
    public ICommand ForgotPasswordCommand {get;}
    public ICommand RestoreResetTestDataCommand {get;}
    public bool IsResetTest=>_dataContext?.IsResetTest==true;
    public bool IsResetting {get=>_isResetting;private set {if(Set(ref _isResetting,value)){Notify(nameof(AppLockControlEnabled));Requery();}}}
    public bool IsBackgroundLockCountdownActive=>_backgroundLockCountdownActive;
    public string ResetScopeLabel=>IsResetTest?"隔离的重置测试资料":"Gameorder 本机账号资料";
    public IReadOnlyList<AppLockDelayOption> AppLockDelayOptions {get;}=[new(0,"立即"),new(1,"1 分钟"),new(3,"3 分钟"),new(5,"5 分钟"),new(10,"10 分钟")];
    public int AppLockDelayMinutes
    {
        get=>NormalizeAppLockDelay(_local.AppLockDelayMinutes);
        set
        {
            if(value is not (0 or 1 or 3 or 5 or 10)||!EnsureUnlocked()||IsBusy||AppLockChecking||IsResetting||value==AppLockDelayMinutes) return;
            var candidate=LocalAccountLibrary.Copy(_local); candidate.AppLockDelayMinutes=value;
            try { SaveState(candidate); _local.AppLockDelayMinutes=value; Notify(nameof(AppLockDelayMinutes));Notify(nameof(AppLockStatus));RecordAppActivity();CheckAppLockIdle(); }
            catch { AppLockMessage="锁定延迟未保存，请稍后重试。"; }
        }
    }
    private static int NormalizeAppLockDelay(int minutes)=>minutes is 0 or 1 or 3 or 5 or 10?minutes:5;
    private readonly SteamService _steam = new();
    private readonly SteamSessionService _sessions = new();
    private readonly SteamGameService _games = new();
    private SteamGameSnapshot _game = SteamGameSnapshot.None();
    private SteamGameSnapshot _demoGame = SteamGameSnapshot.None(isKnown:true);
    private bool _avatarsDeferred;
    private readonly ProcessEfficiencyService _efficiency = new();
    private readonly object _monitorGate = new();
    private bool _foregroundInteractive = true, _monitoring, _monitorRefreshQueued, _disposed;
    private long _monitorGeneration;
    private string _publishedActiveName="";
    public bool IsBackgroundEfficiencyRequested => !_foregroundInteractive && !IsBusy;
    public int EffectiveMonitoringIntervalSeconds => (int)GameBackgroundPolicy.For(IsGameRunning,_foregroundInteractive).PollInterval.TotalSeconds;
    private SteamSessionSnapshot _session = new(false, null, SteamSessionState.Stopped, "Steam 未启动");
    private readonly bool _allowAvatarNetwork;
    private readonly LocalStore _store;
    private LocalState _local;
    private readonly ObservableCollection<SteamAccount> _accounts = [];
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _statusTimer=new(DispatcherPriority.Background);
    private SteamSessionSnapshot? _demoSession;
    private string _activityCategory="all", _activitySearchText="";
    public int StatusDismissSeconds => StatusKind=="error" ? 12 : 6;
    public bool StatusDismissScheduled => _statusTimer.IsEnabled;
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
    public string TaskbarWidgetSettingsMessage { get=>_taskbarWidgetSettingsMessage; private set { if(Set(ref _taskbarWidgetSettingsMessage,value)) ScheduleStatusDismiss(); } }
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
    public IReadOnlyList<ReleaseNote> DevelopmentStages => AboutInfo.DevelopmentStages;
    public string DevelopmentHistoryLabel => AboutInfo.DevelopmentHistoryLabel;
    public string DevelopmentHistoryNotice => AboutInfo.DevelopmentHistoryNotice;
    public bool IsGameRunning => _game.IsRunning && !string.IsNullOrEmpty(_game.SteamId) && _game.SteamId==_session.ActiveSteamId;
    public string RunningGameName => IsGameRunning ? _game.GameName : "";
    public string GameModeHint => IsGameRunning ? $"{RunningGameName} · {ActiveAccountName}\n游戏期间每 20 秒读取状态，暂停头像刷新与任务栏组件；结束后恢复。" : _game.Hint;
    public bool TaskbarWidgetTemporarilyHidden => IsGameRunning;
    public int GameMonitoringIntervalSeconds => (int)GameBackgroundPolicy.For(IsGameRunning).PollInterval.TotalSeconds;
    public string HeaderSubtitle => IsDemo ? "演示账号 · 所有操作仅在此窗口模拟" : "管理本机账号，选择你的下一次登录。";
    public string AccountsSummary => $"{_accounts.Count} 个账号 · {_accounts.Count(a=>a.HasCachedLogin)} 个已记住登录";
    public IReadOnlyCollection<SteamAccount> AllAccounts => _accounts;
    public bool HasActiveAccount => _accounts.Any(a=>a.IsActive);
    public string ActiveAccountName => _accounts.FirstOrDefault(a=>a.IsActive)?.DisplayName ?? "";
    public bool IsSessionOnline => _session.IsOnline;
    public bool IsSessionOffline => !IsSessionOnline;
    public string SessionHint => IsDemo ? "演示连接状态；未读取真实 Steam 会话" : _session.Hint;
    public string SessionSummary => (IsSessionOnline ? "在线" : _session.IsOffline ? "离线登录" : "离线")+(IsDemo ? " · 演示" : "");
    public string CurrentSessionDisplay => HasActiveAccount ? $"{ActiveAccountName} · {SessionSummary}" : SessionSummary;
    public string ClientStatus => SessionSummary;
    public string SteamStatus => IsDemo ? "演示环境，未连接 Steam" : IsPathValid ? "已找到 Steam 客户端" : "尚未找到 Steam，请前往设置指定路径";
    private bool IsPathValid => !string.IsNullOrWhiteSpace(SteamPath) && File.Exists(Path.Combine(SteamPath,"steam.exe"));
    public string SteamPath { get=>_steamPath; set { if ((!IsLocked||_initializing)&&Set(ref _steamPath,value)) { Notify(nameof(SteamStatus)); Requery(); } } }
    public string SearchText { get=>_searchText; set { if ((!IsLocked||_initializing)&&Set(ref _searchText,value)) RefreshFilter(); } }
    public bool FilterAll => _filter==0;
    public bool FilterCached => _filter==1;
    public bool FilterFavorite => _filter==2;
    public ICollectionView Accounts { get; }
    public SteamAccount? SelectedAccount { get=>_selected; set { if ((!IsLocked||_initializing)&&Set(ref _selected,value)) { NoteDraft=value?.Note ?? ""; Notify(nameof(HasSelection)); Notify(nameof(NoSelection)); Requery(); } } }
    public bool HasSelection => SelectedAccount is not null;
    public bool NoSelection => !HasSelection;
    public bool IsEmpty => Accounts.IsEmpty;
    public string EmptyTitle => _accounts.Count==0 ? "从你的第一个账号开始" : "没有匹配的账号";
    public string EmptyDescription => _accounts.Count==0 ? "导入 Steam 本机保存的账号，或手动添加一个登录名。" : "试试其他关键词，或切换筛选条件。";
    public string NoteDraft { get=>_noteDraft; set { if(!IsLocked||_initializing) Set(ref _noteDraft,value); } }
    public string SettingsMessage { get=>_settingsMessage; set { if(Set(ref _settingsMessage,value)) ScheduleStatusDismiss(); } }
    public ObservableCollection<ActivityEntry> Activity { get; }
    public ICollectionView ActivityView { get; }
    public bool ActivityEmpty => ActivityView.IsEmpty;
    public string ActivityCategory
    {
        get=>_activityCategory;
        set { if(value is not ("all" or "login" or "account" or "group" or "settings" or "other")) return; if(Set(ref _activityCategory,value)) RefreshActivityView(); }
    }
    public string ActivitySearchText { get=>_activitySearchText; set { if(Set(ref _activitySearchText,value)) RefreshActivityView(); } }
    public string ActivityCountSummary => $"{ActivityView.Cast<ActivityEntry>().Count()} / {Activity.Count}";
    public string ActivityEmptyTitle => Activity.Count==0 ? "暂无操作记录" : "没有匹配记录";
    public ICommand FilterActivityCommand { get; }
    public ICommand ClearActivityCommand { get; }
    public bool IsBusy { get=>_isBusy; private set { if(Set(ref _isBusy,value)) { Notify(nameof(IsNotBusy)); Notify(nameof(AppLockControlEnabled)); ScheduleStatusDismiss(); ApplyEfficiencyPolicy(); Requery(); if(!value&&_lockPending) ApplyPendingAppLock(); } } }
    public bool IsNotBusy => !IsBusy;
    public bool OperationVisible { get=>_operationVisible; private set { if(Set(ref _operationVisible,value)) ScheduleStatusDismiss(); } }
    public string StatusTitle { get=>_statusTitle; private set=>Set(ref _statusTitle,value); }
    public string StatusMessage { get=>_statusMessage; private set=>Set(ref _statusMessage,value); }
    public string StatusKind { get=>_statusKind; private set=>Set(ref _statusKind,value); }
    public bool PendingConfirmation { get=>_pendingConfirmation; private set { if(Set(ref _pendingConfirmation,value)) { ScheduleStatusDismiss(); Requery(); } } }
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

    public MainViewModel(bool demo, bool allowAvatarNetwork = true,AppLockService? appLockService=null,TimeProvider? appLockTimeProvider=null,
        LocalStore? localStore=null,GameorderDataContext? dataContext=null)
    {
        _statusTimer.Tick+=(_,_)=>
        {
            _statusTimer.Stop();
            if(IsBusy||PendingConfirmation) { Notify(nameof(StatusDismissScheduled)); return; }
            OperationVisible=false; SettingsMessage=""; TaskbarWidgetSettingsMessage="";
            _statusTimer.Stop(); Notify(nameof(StatusDismissScheduled));
        };
        _dataContext=dataContext??(!demo&&appLockService is null&&localStore is null?GameorderDataContext.CreateProduction():null);
        _appLock=_dataContext?.LockService??appLockService??(demo?AppLockService.CreateVolatile():new AppLockService());
        _appLockTime=appLockTimeProvider??TimeProvider.System;
        _lastActivityTimestamp=_appLockTime.GetTimestamp();
        IsDemo=demo||IsResetTest;
        _usesPersistentLocalStore=_dataContext is not null||localStore is not null||!IsDemo;
        _allowAvatarNetwork=allowAvatarNetwork&&!IsResetTest;
        _store=_dataContext?.Store??localStore??new LocalStore(IsDemo,null,_appLock); _local=_store.Load();
        if(_dataContext?.SuppressAutomaticImport==true) _local.AutoImportSteamAccounts=false;
        Accounts=CollectionViewSource.GetDefaultView(_accounts);
        Accounts.Filter=o=>o is SteamAccount a && (ActiveFolderId=="*"||a.FolderId==ActiveFolderId) && (_filter!=1 || a.HasCachedLogin) && (_filter!=2 || a.IsFavorite) && (string.IsNullOrWhiteSpace(SearchText) || $"{a.DisplayName} {a.AccountName} {a.SteamId} {a.Note}".Contains(SearchText.Trim(),StringComparison.OrdinalIgnoreCase));
        Activity=new(_local.Activity.OrderByDescending(a=>a.Time));
        ActivityView=CollectionViewSource.GetDefaultView(Activity);
        ActivityView.Filter=o=>o is ActivityEntry entry&&OperationLogService.Matches(entry,ActivityCategory,ActivitySearchText);
        FilterActivityCommand=ProtectedParameterCommand(p=>ActivityCategory=p?.ToString() ?? "all",_=>!IsBusy);
        ClearActivityCommand=ProtectedCommand(RequestClearActivity,()=>CanManage&&!ActivityView.IsEmpty);
        Activity.CollectionChanged+=(_,_)=>RefreshActivityView();
        ShowAccountsCommand=ProtectedCommand(()=>SetView(0)); ShowHistoryCommand=ProtectedCommand(()=>SetView(1)); ShowSettingsCommand=ProtectedCommand(()=>SetView(2)); ShowAboutCommand=ProtectedCommand(()=>SetView(3));
        FilterAllCommand=ProtectedCommand(()=>SetFilter(0)); FilterCachedCommand=ProtectedCommand(()=>SetFilter(1)); FilterFavoriteCommand=ProtectedCommand(()=>SetFilter(2));
        RefreshCommand=ProtectedCommand(()=>LoadAccounts(true),()=>CanManage);
        AddAccountCommand=ProtectedCommand(()=> { NewAccountName=""; NewDisplayName=""; AddFormError=""; IsAddAccountOpen=true; },()=>CanManage);
        OnlineCommand=ProtectedAsyncCommand(()=>RequestSwitch(false),()=>CanSwitch(false)); OfflineCommand=ProtectedAsyncCommand(()=>RequestSwitch(true),()=>CanSwitch(true));
        ToggleFavoriteCommand=ProtectedCommand(ToggleFavorite,()=>HasSelection&&CanManage);
        RemoveAccountCommand=ProtectedCommand(RequestRemove,()=>HasSelection&&CanManage);
        SaveNoteCommand=ProtectedCommand(SaveNote,()=>HasSelection&&CanManage);
        BrowseSteamCommand=ProtectedCommand(BrowseSteam,()=>!IsBusy&&!IsDemo);
        DetectSteamCommand=ProtectedCommand(DetectSteam,()=>!IsBusy&&!IsDemo);
        SaveSettingsCommand=ProtectedCommand(SaveSettings,()=>!IsBusy);
        RestoreBackupCommand=ProtectedAsyncCommand(RequestRestore,()=>!IsBusy&&(IsDemo||(_steam.CanRestoreBackup(SteamPath)&&!_steam.IsSteamRunning)));
        DismissStatusCommand=ProtectedCommand(()=>OperationVisible=false,()=>!IsBusy);
        ConfirmCommand=ProtectedAsyncCommand(async()=> { if(!EnsureUnlocked()) return; var action=_confirmation; _confirmation=null; PendingConfirmation=false; if(action is not null&&EnsureUnlocked()) await action(); });
        CancelConfirmCommand=ProtectedCommand(()=> { PendingConfirmation=false; _confirmation=null; });
        SaveNewAccountCommand=ProtectedCommand(SaveNewAccount,()=>!IsBusy);
        CloseAddAccountCommand=ProtectedCommand(()=>IsAddAccountOpen=false);
        SelectFolderCommand=ProtectedParameterCommand(p=>SelectFolder(p?.ToString() ?? "*"),_=>CanManage);
        CreateFolderCommand=ProtectedCommand(()=>OpenFolderDialog(false),()=>CanManage);
        RenameFolderCommand=ProtectedCommand(()=>OpenFolderDialog(true),()=>CanManage&&LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId));
        DeleteFolderCommand=ProtectedCommand(RequestDeleteFolder,()=>CanManage&&LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId));
        SaveFolderCommand=ProtectedCommand(SaveFolder,()=>IsFolderDialogOpen&&!IsBusy);
        CloseFolderCommand=ProtectedCommand(()=>IsFolderDialogOpen=false);
        EditAccountCommand=ProtectedCommand(OpenEditAccount,()=>HasSelection&&CanManage);
        SaveEditAccountCommand=ProtectedCommand(SaveEditAccount,()=>IsEditAccountOpen&&!IsBusy);
        CloseEditAccountCommand=ProtectedCommand(()=> { IsEditAccountOpen=false; _editingAccount=null; });
        TaskbarDockTrayCommand=ProtectedCommand(()=>SetTaskbarWidgetDockPosition("tray"),()=>!IsBusy);
        TaskbarDockLeftCommand=ProtectedCommand(()=>SetTaskbarWidgetDockPosition("left"),()=>!IsBusy);
        SteamPath=IsDemo ? "演示环境" : !string.IsNullOrEmpty(_local.SteamPath) ? _local.SteamPath : _steam.DetectSteamPath();
        if(_store.RequiresUnlock) LoadPublicAccountPreview();
        else if(IsDemo&&!_usesPersistentLocalStore) LoadDemo();else LoadAccounts(false);
        if(_store.LoadError is not null) ShowStatus("本地数据读取异常",_store.LoadError,"error");
        LockNowCommand=new RelayCommand(()=>RequestAppLock(),()=>AppLockEnabled&&!IsLocked);
        AppLockSettingsCommand=new RelayCommand(()=>RequestAppLockSettings(AppLockEnabled?AppLockSettingsAction.ChangePassword:AppLockSettingsAction.Enable),()=>AppLockControlEnabled);
        ChangePasswordCommand=new RelayCommand(()=>RequestAppLockSettings(AppLockSettingsAction.ChangePassword),()=>AppLockControlEnabled&&AppLockEnabled);
        ChangePasswordHintCommand=new RelayCommand(()=>RequestAppLockSettings(AppLockSettingsAction.ChangeHint),()=>AppLockControlEnabled&&AppLockEnabled);
        DisableLockCommand=new RelayCommand(()=>RequestAppLockSettings(AppLockSettingsAction.Disable),()=>AppLockControlEnabled&&AppLockEnabled);
        ForgotPasswordCommand=new RelayCommand(RequestForgotPassword,()=>_dataContext is not null&&!IsBusy&&!AppLockChecking&&!IsResetting);
        RestoreResetTestDataCommand=new AsyncCommand(RestoreResetTestDataAsync,()=>IsResetTest&&!IsBusy&&!AppLockChecking&&!IsResetting);
        _timer=new DispatcherTimer(DispatcherPriority.Background) { Interval=GameBackgroundPolicy.For(IsGameRunning,_foregroundInteractive).PollInterval };
        _timer.Tick+=async (_,_)=>
        {
            await RefreshSessionAsync();
            if(!_disposed&&!IsResetting&&!IsDemo&&GameBackgroundPolicy.For(IsGameRunning,_foregroundInteractive).AllowAvatarWork&&AvatarLoadTask.IsCompleted&&
                (_avatarsDeferred||(_allowAvatarNetwork&&_avatarRetryAt.HasValue&&DateTimeOffset.UtcNow>=_avatarRetryAt.Value)))
            { _avatarRetryAt=null; StartAvatarLoading(); }
        }; _timer.Start();
        _initializing=false;
        _isLocked=_appLock.Enabled;
        AppLockMessage=_appLock.ConfigurationError?AppLockService.RecoveryInstructions:"";
        _appLockTimer.Tick+=(_,_)=>CheckAppLockIdle();
        UpdateAppLockTimer();
    }
    private RelayCommand ProtectedCommand(Action action,Func<bool>? canExecute=null)=>new(()=> { if(EnsureUnlocked()) action(); },()=>!IsLocked&&(canExecute?.Invoke()??true));
    private ParameterCommand ProtectedParameterCommand(Action<object?> action,Func<object?,bool>? canExecute=null)=>new(p=> { if(EnsureUnlocked()) action(p); },p=>!IsLocked&&(canExecute?.Invoke(p)??true));
    private AsyncCommand ProtectedAsyncCommand(Func<Task> action,Func<bool>? canExecute=null)=>new(async()=> { if(EnsureUnlocked()) await action(); },()=>!IsLocked&&(canExecute?.Invoke()??true));
    public bool EnsureUnlocked()
    {
        if(!IsLocked||_initializing) return true;
        UnlockRequested?.Invoke(this,EventArgs.Empty); return false;
    }
    public void RecordAppActivity()
    {
        if(IsLocked||IsResetting) return;
        _lastActivityTimestamp=_appLockTime.GetTimestamp();
        if(_backgroundLockCountdownActive) CheckAppLockIdle();
    }
    public void BeginBackgroundLockCountdown()
    {
        if(_disposed||IsResetting) return;
        _backgroundLockCountdownActive=true; _lastActivityTimestamp=_appLockTime.GetTimestamp();
        Notify(nameof(IsBackgroundLockCountdownActive)); UpdateAppLockTimer(); CheckAppLockIdle();
    }
    public void CancelBackgroundLockCountdown()
    {
        _backgroundLockCountdownActive=false;
        if(_lockPendingReason is "background" or "idle") { _lockPending=false; _lockPendingReason=""; }
        Notify(nameof(IsBackgroundLockCountdownActive)); UpdateAppLockTimer();
    }
    public void RequestAppLock(string reason="manual")
    {
        if(_disposed||IsResetting) return;
        ShowPasswordHint=false;
        // An enable save may still be in flight. Preserve session/background lock requests
        // before Enabled becomes true, then apply them only if the atomic save succeeds.
        if(AppLockChecking) { ++_lockEpoch; QueueAppLock(reason); return; }
        if(!AppLockEnabled) { _lockPending=false; _lockPendingReason=""; return; }
        ++_lockEpoch;
        if(IsBusy&&!AppLockConfigurationError) { QueueAppLock(reason); return; }
        _lockPending=false; _lockPendingReason="";
        CloseTransient();
        _appLock.Lock();
        IsLocked=true;
        AppLockMessage=_appLock.ConfigurationError?AppLockService.RecoveryInstructions:"";
    }
    private void QueueAppLock(string reason)
    {
        _lockPending=true;
        if(_lockPendingReason is "windows-session" or "manual") return;
        _lockPendingReason=reason;
    }
    private void ApplyPendingAppLock()
    {
        if(!_lockPending||IsBusy||AppLockChecking) return;
        string reason=_lockPendingReason; RequestAppLock(string.IsNullOrEmpty(reason)?"manual":reason);
    }
    public void CheckAppLockIdle()
    {
        if(_backgroundLockCountdownActive&&!IsLocked&&!IsResetting&&(AppLockEnabled||AppLockChecking)&&
            _appLockTime.GetElapsedTime(_lastActivityTimestamp)>=TimeSpan.FromMinutes(AppLockDelayMinutes))
            RequestAppLock("background");
    }
    private void UpdateAppLockTimer()
    {
        if(_backgroundLockCountdownActive&&(AppLockEnabled||AppLockChecking)&&!IsLocked&&!_disposed&&!IsResetting) _appLockTimer.Start();
        else _appLockTimer.Stop();
    }
    public async Task<bool> TryUnlockAsync(string password)
    {
        if(!IsLocked) return true;
        if(AppLockChecking) return false;
        if(string.IsNullOrEmpty(password)) {ShowPasswordHint=false;AppLockMessage="请输入应用密码。";return false;}
        if(_appLockTime.GetUtcNow()<_unlockRetryAt)
        { AppLockMessage="尝试过多，请稍后再试。"; return false; }
        long epoch=_lockEpoch;
        AppLockChecking=true;
        try
        {
            var result=await Task.Run(()=> { bool valid=_appLock.Verify(password,out string error); return (valid,error); });
            if(_disposed) { if(result.valid) _appLock.Lock();return false; }
            if(result.valid&&epoch==_lockEpoch&&!_lockPending)
            {
                ReloadAuthenticatedLibrary();
                CancelBackgroundLockCountdown();
                IsLocked=false; _failedUnlockAttempts=0; _unlockRetryAt=default; ShowPasswordHint=false; AppLockMessage="";
                PopulateAuthenticatedAccounts();
                RecordAppActivity(); return true;
            }
            if(result.valid) _appLock.Lock();
            if(!result.valid&&++_failedUnlockAttempts>=5)
            { _unlockRetryAt=_appLockTime.GetUtcNow()+TimeSpan.FromSeconds(30); _failedUnlockAttempts=0; }
            ShowPasswordHint=!result.valid&&result.error=="密码不正确。";
            AppLockMessage=result.valid?"会话已重新锁定，请重试。":NormalizeAppLockError(result.error); return false;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { ShowPasswordHint=false;AppLockMessage="无法验证应用锁，请稍后重试。"; return false; }
        finally
        {
            AppLockChecking=false;
            ApplyPendingAppLock();UpdateAppLockTimer();
        }
    }
    private void RequestAppLockSettings(AppLockSettingsAction action)
    {
        if(!EnsureUnlocked()||IsBusy||AppLockChecking) return;
        if(action==AppLockSettingsAction.ChangeHint&&!AppLockEnabled) return;
        ShowPasswordHint=false;AppLockMessage="";
        RecordAppActivity(); AppLockSettingsRequested?.Invoke(this,new(action));
    }
    public async Task<bool> ConfigureAppLockAsync(AppLockSettingsAction action,string currentPassword,string newPassword,string confirmation,string? passwordHint=null)
    {
        if(!EnsureUnlocked()||IsBusy||AppLockChecking) return false;
        if(action==AppLockSettingsAction.ChangeHint&&!AppLockEnabled) return false;
        if((action is AppLockSettingsAction.ChangePassword or AppLockSettingsAction.ChangeHint or AppLockSettingsAction.Disable)&&string.IsNullOrEmpty(currentPassword))
        {ShowPasswordHint=false;AppLockMessage="请输入当前密码。";return false;}
        if((action is AppLockSettingsAction.Enable or AppLockSettingsAction.ChangePassword)&&newPassword!=confirmation)
        { ShowPasswordHint=false;AppLockMessage="两次密码不一致。"; return false; }
        AppLockChecking=true;
        try
        {
            var result=await Task.Run(()=> {
                string error;
                bool success=action switch {
                    AppLockSettingsAction.Enable=>_appLock.Enable(newPassword,out error,passwordHint),
                    AppLockSettingsAction.ChangePassword=>_appLock.ChangePassword(currentPassword,newPassword,out error,passwordHint),
                    AppLockSettingsAction.ChangeHint=>_appLock.ChangePasswordHint(currentPassword,passwordHint,out error),
                    AppLockSettingsAction.Disable=>_appLock.Disable(currentPassword,out error),
                    _=>ThrowUnsupported(out error)};
                return (success,error);
            });
            if(_disposed) return false;
            ShowPasswordHint=!result.success&&result.error=="密码不正确。";
            AppLockMessage=result.success?"":NormalizeAppLockError(result.error);
            Notify(nameof(AppLockEnabled)); Notify(nameof(AppLockConfigurationError)); Notify(nameof(AppLockStatus)); Notify(nameof(AppLockActionLabel));
            Notify(nameof(IsEncrypted));Notify(nameof(DataProtectionStatus));
            NotifyPasswordHint();
            UpdateAppLockTimer();
            RecordAppActivity(); return result.success;
        }
        finally
        {
            AppLockChecking=false;
            // A failed atomic security save may have invalidated the backend session.
            // Cover the interface immediately rather than waiting for background locking.
            if(AppLockConfigurationError) RequestAppLock("security-configuration");
            ApplyPendingAppLock(); UpdateAppLockTimer();CheckAppLockIdle();
        }
    }
    private static bool ThrowUnsupported(out string error) { error="无效的安全设置操作。"; return false; }
    private static string NormalizeAppLockError(string message)=>message=="密码不正确。"?"密码错误，请重新输入。":message;
    private void NotifyPasswordHint() {Notify(nameof(CurrentPasswordHint));Notify(nameof(HasPasswordHint));Notify(nameof(ShowPasswordHint));}
    private void ReloadAuthenticatedLibrary()
    {
        if(!_usesPersistentLocalStore) return;
        _local=_store.Load();
        if(_dataContext?.SuppressAutomaticImport==true) _local.AutoImportSteamAccounts=false;
        if(_store.RequiresUnlock) throw new System.Security.Cryptography.CryptographicException("应用资料仍处于锁定状态。");
        PublishActivity(_local.Activity);
        Notify(nameof(AppLockDelayMinutes)); Notify(nameof(AppLockStatus));
        Notify(nameof(IsEncrypted)); Notify(nameof(DataProtectionStatus));
    }
    private void PopulateAuthenticatedAccounts()
    {
        bool wasPublicPreview=_publicPreviewOnly;
        _publicPreviewOnly=false;
        if(IsDemo&&!_usesPersistentLocalStore) { if(wasPublicPreview){_accounts.Clear();LoadDemo();}else UpdateCounts();return; }
        LoadAccounts(false);
        if(_store.LoadError is not null) ShowStatus("本地资料读取未完成",_store.LoadError,"error");
    }
    private void RequestForgotPassword()
    {
        if(_dataContext is null||IsBusy||AppLockChecking||IsResetting||_disposed) return;
        ForgotPasswordRequested?.Invoke(this,EventArgs.Empty);
    }
    public ForgotPasswordFlow CreateForgotPasswordFlow()=>new(_appLockTime);
    public async Task<bool> ResetLocalDataAsync(ResetConsent consent)
    {
        if(_dataContext is null||IsBusy||AppLockChecking||IsResetting||_disposed||consent is null||!consent.TryConsume()) return false;
        ShowPasswordHint=false;
        CloseTransient(); IsResetting=true; IsBusy=true; ++_lockEpoch;
        StopDataWorkForReset();
        bool succeeded=false;
        try
        {
            try { await AvatarLoadTask; } catch(OperationCanceledException) { }
            var state=await _dataContext.ResetAsync();
            if(_disposed) return false;
            PublishResetState(state,restored:false);
            AppLockMessage=""; succeeded=true; DataResetCompleted?.Invoke(this,EventArgs.Empty); return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            _appLock.Lock(); IsLocked=AppLockEnabled;
            AppLockMessage="清空未完成，请关闭后重新打开并检查本地资料。"; return false;
        }
        finally
        {
            consent.CompleteReset(succeeded); IsResetting=false; IsBusy=false;
            _avatarsDeferred=false; _avatarRetryAt=null;
            if(!_disposed) _timer.Start();UpdateAppLockTimer();
        }
    }
    private async Task RestoreResetTestDataAsync()
    {
        if(!IsResetTest||_dataContext is null||IsBusy||AppLockChecking||IsResetting||_disposed) return;
        ShowPasswordHint=false;
        CloseTransient();IsResetting=true;IsBusy=true;++_lockEpoch;StopDataWorkForReset();
        try
        {
            try {await AvatarLoadTask;}catch(OperationCanceledException){}
            var state=await _dataContext.RestoreTestDataAsync();
            if(_disposed)return;
            PublishResetState(state,restored:true);AppLockMessage="";DataResetCompleted?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        { _appLock.Lock();IsLocked=AppLockEnabled;AppLockMessage="测试资料恢复未完成，请重新打开测试版。"; }
        finally {IsResetting=false;IsBusy=false;if(!_disposed)_timer.Start();UpdateAppLockTimer();}
    }
    private void StopDataWorkForReset()
    {
        _timer.Stop();_statusTimer.Stop();_appLockTimer.Stop();++_monitorGeneration;
        _monitorRefreshQueued=false;_avatarRefresh?.Cancel();_avatarsDeferred=false;_avatarRetryAt=null;
    }
    private void PublishResetState(LocalState state,bool restored)
    {
        _initializing=true;
        try
        {
            _local=restored&&_store.RequiresUnlock?new():state;
            _local.AutoImportSteamAccounts=false;
            _accounts.Clear();_publicAccountPreview=[];_selected=null;
            _searchText="";_noteDraft="";_activeFolderId="*";_filter=0;
            _lockPending=false;_lockPendingReason="";_backgroundLockCountdownActive=false;
            _failedUnlockAttempts=0;_unlockRetryAt=default;_avatarRetryAt=null;_avatarsDeferred=false;
            ShowPasswordHint=false;NotifyPasswordHint();
            _demoSession=null;_demoGame=SteamGameSnapshot.None(isKnown:true);
            PublishActivity(_local.Activity);
            _isLocked=AppLockEnabled; // Publish the replacement snapshot as a single UI-thread operation.
            if(restored&&IsLocked) LoadPublicAccountPreview();else UpdateCounts();
            foreach(string name in new[]{nameof(IsLocked),nameof(IsUnlocked),nameof(AppLockEnabled),nameof(AppLockConfigurationError),nameof(AppLockStatus),
                nameof(AppLockActionLabel),nameof(AppLockControlEnabled),nameof(IsEncrypted),nameof(DataProtectionStatus),nameof(AppLockDelayMinutes),
                nameof(SelectedAccount),nameof(HasSelection),nameof(NoSelection),nameof(NoteDraft),nameof(SearchText),nameof(FolderTitle),
                nameof(TaskbarWidgetEnabled),nameof(TaskbarWidgetDock),nameof(TaskbarWidgetDockIsTray),nameof(TaskbarWidgetDockIsLeft),nameof(IsBackgroundLockCountdownActive)}) Notify(name);
            Requery();
        }
        finally{_initializing=false;}
    }
    private static void Requery()=>CommandManager.InvalidateRequerySuggested();
    private void SetView(int view) { if(!EnsureUnlocked()) return; _view=view; Notify(nameof(IsAccountsView)); Notify(nameof(IsHistoryView)); Notify(nameof(IsSettingsView)); Notify(nameof(IsAboutView)); }
    private void SetFilter(int filter) { if(!EnsureUnlocked()) return; _filter=filter; Notify(nameof(FilterAll)); Notify(nameof(FilterCached)); Notify(nameof(FilterFavorite)); RefreshFilter(); }
    private void RefreshFilter() { Accounts.Refresh(); Notify(nameof(IsEmpty)); Notify(nameof(EmptyTitle)); Notify(nameof(EmptyDescription)); }
    private void UpdateCounts() { Notify(nameof(AccountsSummary)); Notify(nameof(AvatarSummary)); Notify(nameof(AllAccounts)); RebuildFolders(); RefreshFilter(); RefreshSession(); Requery(); }
    private void RefreshSession()
    {
        if(_disposed) return;
        ++_monitorGeneration;
        if(IsDemo)
        {
            var snapshot=ReadSessionSnapshot(SteamPath);
            ApplySessionSnapshot(snapshot.Session,snapshot.Game);
            return;
        }
        // Reapply known state to newly imported account objects without waiting for I/O.
        ApplySessionSnapshot(_session,_game);
        _monitorRefreshQueued=true;
        _ = RefreshSessionAsync();
    }
    private (SteamSessionSnapshot Session,SteamGameSnapshot Game) ReadSessionSnapshot(string path)
    {
        if(IsDemo)
            return (_demoSession ?? new(true,_accounts.FirstOrDefault()?.SteamId,SteamSessionState.Online,"演示会话"),_demoGame);
        lock(_monitorGate)
        {
            var session=_sessions.Read(path);
            return (session,_games.Read(path,session));
        }
    }
    internal async Task RefreshSessionAsync()
    {
        // Only one read can be pending. UI mutations invalidate an older worker result.
        if(_disposed||_monitoring||IsBusy||IsResetting) return;
        if(IsDemo) { RefreshSession(); return; }
        _monitoring=true;
        try
        {
            do
            {
                _monitorRefreshQueued=false;
                long generation=++_monitorGeneration;
                string path=SteamPath;
                var snapshot=await Task.Run(()=>ReadSessionSnapshot(path));
                if(!_disposed&&generation==_monitorGeneration&&path==SteamPath&&!IsBusy)
                {
                    ApplySessionSnapshot(snapshot.Session,snapshot.Game);
                    _efficiency.SetBackgroundEfficiency(IsBackgroundEfficiencyRequested);
                }
                else if(!_disposed) _monitorRefreshQueued=true;
                // A foreground/import/path change during a read is merged into one fresh follow-up.
            } while(!_disposed&&!IsBusy&&_monitorRefreshQueued);
        }
        finally { _monitoring=false; }
    }
    private void ApplySessionSnapshot(SteamSessionSnapshot session,SteamGameSnapshot game)
    {
        bool wasPlaying=IsGameRunning,hadActive=HasActiveAccount;
        string previousName=_publishedActiveName;
        var previousSession=_session;
        var previousGame=_game;
        _session=session; _game=game;
        foreach(var account in _accounts)
        {
            bool active=!string.IsNullOrEmpty(account.SteamId)&&account.SteamId==_session.ActiveSteamId;
            account.ApplySession(active,_session.State,active ? SessionHint : "此账号当前未登录本机 Steam");
            account.ApplyGame(active&&IsGameRunning,active&&IsGameRunning ? _game.GameName : "",
                active&&IsGameRunning ? $"{_game.GameName} · {_game.Hint}" : "");
        }
        bool identityChanged=previousSession.ActiveSteamId!=_session.ActiveSteamId;
        if(hadActive!=HasActiveAccount||identityChanged) Notify(nameof(HasActiveAccount));
        if(previousName!=ActiveAccountName||identityChanged) Notify(nameof(ActiveAccountName));
        if(previousName!=ActiveAccountName) Notify(nameof(GameModeHint));
        if(previousSession!=_session||hadActive!=HasActiveAccount||previousName!=ActiveAccountName)
        {
            Notify(nameof(IsSessionOnline)); Notify(nameof(CurrentSessionDisplay));
            Notify(nameof(IsSessionOffline)); Notify(nameof(SessionSummary)); Notify(nameof(SessionHint)); Notify(nameof(ClientStatus));
            Requery();
        }
        _publishedActiveName=ActiveAccountName;
        if(wasPlaying!=IsGameRunning) ApplyGamePolicy();
        if(previousGame!=_game||wasPlaying!=IsGameRunning)
        {
            Notify(nameof(IsGameRunning)); Notify(nameof(RunningGameName)); Notify(nameof(GameModeHint));
            Notify(nameof(TaskbarWidgetTemporarilyHidden)); Notify(nameof(GameMonitoringIntervalSeconds));
        }
    }
    private void ApplyGamePolicy()
    {
        var policy=GameBackgroundPolicy.For(IsGameRunning,_foregroundInteractive);
        // The first session read happens before the DispatcherTimer is constructed.
        if(_timer is not null) _timer.Interval=policy.PollInterval;
        if(!policy.AllowAvatarWork&&_avatarRefresh is not null)
        { _avatarsDeferred=true; _avatarRefresh.Cancel(); }
        ApplyEfficiencyPolicy();
        Notify(nameof(EffectiveMonitoringIntervalSeconds));
    }
    private void ApplyEfficiencyPolicy()
    {
        if(!IsDemo) _efficiency.SetBackgroundEfficiency(IsBackgroundEfficiencyRequested);
        Notify(nameof(IsBackgroundEfficiencyRequested));
    }
    public void SetForegroundInteractive(bool foreground)
    {
        if(_disposed||_foregroundInteractive==foreground) return;
        _foregroundInteractive=foreground;
        ApplyGamePolicy();
        // Showing either control surface refreshes immediately instead of waiting for an idle poll.
        if(foreground)
        {
            _monitorRefreshQueued=true;
            _ = RefreshSessionAsync();
            if(_avatarsDeferred&&AvatarLoadTask.IsCompleted&&!IsGameRunning) StartAvatarLoading();
        }
    }
    internal void SetDemoSessionForVerification(SteamSessionSnapshot? snapshot)
    {
        if(!IsDemo) throw new InvalidOperationException("会话演示只允许使用隔离账号。");
        _demoSession=snapshot; RefreshSession();
    }
    internal void SetDemoGameForVerification(SteamGameSnapshot snapshot)
    {
        if(!IsDemo) throw new InvalidOperationException("游戏演示只允许使用隔离账号。");
        _demoGame=snapshot; RefreshSession();
    }
    private void LoadDemo()
    {
        _accounts.Add(new() { AccountName="northwind", DisplayName="Northwind", SteamId="76561198000000001", HasCachedLogin=true, IsCurrentSelection=true, LastLoginAt=DateTimeOffset.Now.AddHours(-2), IsFavorite=true, Note="主账号 · 和朋友一起玩的游戏", Accent="#E9EFFB" });
        _accounts.Add(new() { AccountName="weekend_player", DisplayName="周末玩家", SteamId="76561198000000002", HasCachedLogin=true, LastLoginAt=DateTimeOffset.Now.AddDays(-1), Note="单机游戏 / 离线使用", Accent="#EEF2E7" });
        _accounts.Add(new() { AccountName="little_orbit", DisplayName="Little Orbit", SteamId="76561198000000003", HasCachedLogin=false, LastLoginAt=DateTimeOffset.Now.AddDays(-8), Accent="#F4EBE4" });
        SelectedAccount=_accounts[0]; UpdateCounts();
    }
    private static SteamAccount CopyPublicIdentity(SteamAccount source)=>new() {
        AccountName=source.AccountName,DisplayName=source.DisplayName,SteamId=source.SteamId,HasCachedLogin=source.HasCachedLogin,
        LastLoginAt=source.LastLoginAt,IsCurrentSelection=source.IsCurrentSelection,AvatarPath=source.AvatarPath,Accent=source.Accent
    };
    private void LoadPublicAccountPreview()
    {
        _publicPreviewOnly=true;
        try
        {
            if(_dataContext?.SuppressAutomaticImport==true) _publicAccountPreview=[];
            else if(IsResetTest) _publicAccountPreview=_dataContext!.GetPublicAccountPreview();
            else if(IsDemo) _publicAccountPreview=[
                new(){AccountName="northwind",DisplayName="Northwind",SteamId="76561198000000001",HasCachedLogin=true,IsCurrentSelection=true},
                new(){AccountName="weekend_player",DisplayName="周末玩家",SteamId="76561198000000002",HasCachedLogin=true},
                new(){AccountName="little_orbit",DisplayName="Little Orbit",SteamId="76561198000000003"}];
            else _publicAccountPreview=IsPathValid?_steam.ReadAccounts(SteamPath):[];
            _accounts.Clear();
            foreach(var account in _publicAccountPreview) _accounts.Add(CopyPublicIdentity(account));
            SelectedAccount=_accounts.FirstOrDefault(a=>a.IsCurrentSelection)??_accounts.FirstOrDefault();
            UpdateCounts();StartAvatarLoading();
        }
        catch { _accounts.Clear();UpdateCounts(); }
    }
    private void LoadStoredAccounts()
    {
        if(IsResetting||_store.RequiresUnlock) return;
        if(IsResetTest) _publicAccountPreview=_dataContext!.GetPublicAccountPreview();
        var keep=SelectedAccount?.AccountName;
        _accounts.Clear();
        foreach(var metadata in _local.Accounts)
        {
            if(_local.HiddenAccounts.Contains(metadata.AccountName,StringComparer.OrdinalIgnoreCase)) continue;
            var publicIdentity=_publicAccountPreview.FirstOrDefault(a=>a.AccountName.Equals(metadata.AccountName,StringComparison.OrdinalIgnoreCase));
            var account=publicIdentity is null?new SteamAccount{AccountName=metadata.AccountName}:CopyPublicIdentity(publicIdentity);
            ApplyMetadata(account);_accounts.Add(account);
        }
        SelectedAccount=_accounts.FirstOrDefault(a=>a.AccountName==keep)??_accounts.FirstOrDefault(a=>a.IsCurrentSelection)??_accounts.FirstOrDefault();
        UpdateCounts();StartAvatarLoading();
    }
    private void LoadAccounts(bool explicitImport)
    {
        if(_store.RequiresUnlock||IsResetting) return;
        if(IsDemo&&_usesPersistentLocalStore || !explicitImport&&(!_local.AutoImportSteamAccounts||_dataContext?.SuppressAutomaticImport==true)) {LoadStoredAccounts();return;}
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
                var candidate=LocalAccountLibrary.Copy(_local); candidate.HiddenAccounts.Clear();candidate.AutoImportSteamAccounts=true;
                candidate.Activity=Activity.Take(100).ToList(); SaveState(candidate); _local.HiddenAccounts=candidate.HiddenAccounts;_local.AutoImportSteamAccounts=true;
                _dataContext?.MarkManualImportCompleted();
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
            if(explicitImport&&imported)
            {
                AddActivity("导入账号",$"读取 {_accounts.Count} 个本机账号","success","account");
                ShowStatus("导入完成",$"已读取 {_accounts.Count} 个本机账号，分组与备注已保留。","success");
            }
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
        if(IsDemo||IsResetting||_lifetime.IsCancellationRequested) return;
        if(!GameBackgroundPolicy.For(IsGameRunning,_foregroundInteractive).AllowAvatarWork) { _avatarsDeferred=true; return; }
        _avatarsDeferred=false;
        _avatarRefresh?.Cancel();
        var refresh=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _avatarRefresh=refresh;
        AvatarLoadTask=LoadAvatarsAsync(_accounts.ToArray(),SteamPath,refresh,_allowAvatarNetwork&&!_publicPreviewOnly);
    }
    private async Task LoadAvatarsAsync(SteamAccount[] accounts,string steamPath,CancellationTokenSource refresh,bool allowNetwork)
    {
        try
        {
            using var service=new AvatarService(steamPath,IsDemo,allowNetwork:allowNetwork,imageValidator:AvatarImageConverter.CanDecode);
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
            if(ReferenceEquals(_avatarRefresh,refresh))
            {
                if(refresh.IsCancellationRequested&&!_lifetime.IsCancellationRequested) _avatarsDeferred=true;
                _avatarRefresh=null; _avatarRetryAt=AvatarService.NextNetworkAttemptUtc; Notify(nameof(AvatarSummary));
            }
            refresh.Dispose();
        }
    }
    private AccountMetadata Metadata(SteamAccount a)
    {
        return LocalAccountLibrary.UpsertMetadata(_local,a);
    }
    private bool CanManage => !IsLocked&& !IsBusy&&!PendingConfirmation&&!IsAddAccountOpen&&!HasManagementDialog;
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
        if(!EnsureUnlocked()) return;
        if(id!="*"&&id!=""&&!LocalAccountLibrary.IsCustomFolder(_local,id)) return;
        ActiveFolderId=id; SetView(0);
        foreach(var folder in FolderEntries) folder.IsSelected=folder.Id==id;
        if(SelectedAccount is null||!Accounts.Contains(SelectedAccount)) SelectedAccount=Accounts.Cast<SteamAccount>().FirstOrDefault();
    }
    private void CommitCandidate(LocalState candidate,params ActivityEntry[] entries)
    {
        if(!EnsureUnlocked()) throw new UnauthorizedAccessException("请先解锁 Gameorder。");
        candidate.Activity=entries.Concat(Activity).Take(100).ToList();
        SaveState(candidate);
        _local.SteamPath=candidate.SteamPath; _local.Accounts=candidate.Accounts; _local.Folders=candidate.Folders;
        _local.TaskbarWidgetEnabled=candidate.TaskbarWidgetEnabled; _local.TaskbarWidgetDock=candidate.TaskbarWidgetDock;
        _local.AppLockDelayMinutes=candidate.AppLockDelayMinutes;_local.AutoImportSteamAccounts=candidate.AutoImportSteamAccounts;
        _local.HiddenAccounts=candidate.HiddenAccounts; _local.Activity=candidate.Activity;
        PublishActivity(candidate.Activity);
        foreach(var account in _accounts) ApplyMetadata(account);
        NoteDraft=SelectedAccount?.Note ?? "";
        UpdateCounts();
    }
    private void SaveState(LocalState state)
    {
        _store.Save(state);
        Notify(nameof(IsEncrypted));
        Notify(nameof(DataProtectionStatus));
    }
    private void OpenFolderDialog(bool rename)
    {
        if(!EnsureUnlocked()) return;
        _renameFolderId=rename ? ActiveFolderId : "";
        FolderDialogTitle=rename ? "重命名分组" : "新建分组";
        FolderDraftName=rename ? FolderTitle : ""; FolderFormError=""; IsFolderDialogOpen=true;
    }
    private void SaveFolder()
    {
        if(!EnsureUnlocked()) return;
        try
        {
            string id=_renameFolderId;
            var candidate=id.Length==0 ? LocalAccountLibrary.CreateFolder(_local,FolderDraftName,out id) : LocalAccountLibrary.RenameFolder(_local,id,FolderDraftName);
            var title=_renameFolderId.Length==0 ? "添加分组" : "重命名分组";
            bool changed=_renameFolderId.Length==0||FolderTitle!=FolderDraftName.Trim();
            CommitCandidate(candidate,changed ? [NewActivity(title,FolderDraftName.Trim(),"group")] : []);
            IsFolderDialogOpen=false; SelectFolder(id);
        }
        catch(Exception ex) { FolderFormError=ex.Message; }
    }
    private void RequestDeleteFolder()
    {
        if(!EnsureUnlocked()) return;
        var id=ActiveFolderId; var name=FolderTitle;
        Confirm("删除分组？",$"删除「{name}」，其中账号会移到未分组。账号和备注会保留。",()=>
        {
            try { CommitCandidate(LocalAccountLibrary.DeleteFolder(_local,id),NewActivity("删除分组",name,"group")); SelectFolder(""); }
            catch(Exception ex) { ShowStatus("分组未删除",ex.Message,"error"); }
            return Task.CompletedTask;
        });
    }
    private void OpenEditAccount()
    {
        if(!EnsureUnlocked()) return;
        if(SelectedAccount is not { } account) return;
        _editingAccount=account; EditDisplayName=account.DisplayName; EditNote=account.Note; EditFormError="";
        EditFolderChoice=FolderChoices.FirstOrDefault(f=>f.Id==account.FolderId) ?? FolderChoices.FirstOrDefault();
        IsEditAccountOpen=true;
    }
    private void SaveEditAccount()
    {
        if(!EnsureUnlocked()) return;
        if(_editingAccount is not { } account||!_accounts.Contains(account)) { EditFormError="账号已不在账号库中。"; return; }
        try
        {
            var candidate=LocalAccountLibrary.EditAccount(_local,account,EditDisplayName,EditNote,EditFolderChoice?.Id);
            var entries=new List<ActivityEntry>();
            if(account.DisplayName!=EditDisplayName.Trim()) entries.Add(NewActivity("修改昵称",$"{account.DisplayName} → {EditDisplayName.Trim()}","account"));
            if(account.Note!=EditNote) entries.Add(NewActivity("修改备注",account.DisplayName,"account"));
            if(account.FolderId!=(EditFolderChoice?.Id ?? "")) entries.Add(NewActivity("移动账号分组",$"{account.DisplayName} → {EditFolderChoice?.Name ?? "未分组"}","group"));
            CommitCandidate(candidate,entries.ToArray()); IsEditAccountOpen=false; _editingAccount=null;
            if(ActiveFolderId!="*"&&account.FolderId!=ActiveFolderId) SelectFolder(account.FolderId);
            SelectedAccount=account;
        }
        catch(Exception ex) { EditFormError=ex.Message; }
    }
    private bool SaveLocal()
    {
        try { _local.Activity=Activity.Take(100).ToList(); SaveState(_local); return true; }
        catch(Exception ex) { ShowStatus("无法保存本地资料",ex.Message,"error"); return false; }
    }
    private static ActivityEntry NewActivity(string title,string description,string category,string kind="success")
        => new() {Title=title,Description=description,Category=category,Kind=kind};
    private void AddActivity(string title,string description,string kind,string category="login")
    {
        var candidate=LocalAccountLibrary.Copy(_local);
        candidate.Activity=new[]{NewActivity(title,description,category,kind)}.Concat(Activity).Take(100).ToList();
        try { SaveState(candidate); _local.Activity=candidate.Activity; PublishActivity(candidate.Activity); }
        catch(Exception ex) { ShowStatus("记录未保存",ex.Message,"error"); }
    }
    private void PublishActivity(IEnumerable<ActivityEntry> entries)
    {
        var copy=entries.ToArray();
        Activity.Clear(); foreach(var entry in copy) Activity.Add(entry);
        RefreshActivityView();
    }
    private void RefreshActivityView()
    {
        ActivityView.Refresh(); Notify(nameof(ActivityEmpty)); Notify(nameof(ActivityCountSummary)); Notify(nameof(ActivityEmptyTitle)); Requery();
    }
    private void RequestClearActivity()
    {
        if(!EnsureUnlocked()) return;
        string category=ActivityCategory,search=ActivitySearchText;
        int count=ActivityView.Cast<ActivityEntry>().Count();
        if(count==0) return;
        Confirm($"清理 {count} 条操作记录？","清理当前筛选结果，账号、备注和分组会保留。",()=>
        {
            var source=LocalAccountLibrary.Copy(_local); source.Activity=Activity.ToList();
            var candidate=OperationLogService.CreateClearCandidate(source,category,search);
            try
            {
                SaveState(candidate); _local.Activity=candidate.Activity; PublishActivity(candidate.Activity);
                ShowStatus("记录已清理",$"已清理 {source.Activity.Count-candidate.Activity.Count} 条记录。","success");
            }
            catch(Exception ex) { ShowStatus("记录未清理",ex.Message,"error"); }
            return Task.CompletedTask;
        });
    }
    private void ScheduleStatusDismiss()
    {
        _statusTimer.Stop();
        if(!_lifetime.IsCancellationRequested&&!IsBusy&&!PendingConfirmation&&
            (OperationVisible||!string.IsNullOrEmpty(SettingsMessage)||!string.IsNullOrEmpty(TaskbarWidgetSettingsMessage)))
        { _statusTimer.Interval=TimeSpan.FromSeconds(StatusDismissSeconds); _statusTimer.Start(); }
        Notify(nameof(StatusDismissSeconds)); Notify(nameof(StatusDismissScheduled));
    }
    public void ShowStatus(string title,string message,string kind="info")
    { StatusTitle=title; StatusMessage=message; StatusKind=kind; OperationVisible=true; ScheduleStatusDismiss(); }
    private void ToggleFavorite()
    {
        if(!EnsureUnlocked()) return;
        if(SelectedAccount is not { } a) return;
        try
        {
            var candidate=LocalAccountLibrary.Copy(_local); bool favorite=!a.IsFavorite;
            LocalAccountLibrary.UpsertMetadata(candidate,a).IsFavorite=favorite;
            var draft=NoteDraft;
            CommitCandidate(candidate,NewActivity(favorite?"收藏账号":"取消收藏",a.DisplayName,"account")); NoteDraft=draft;
        }
        catch(Exception ex) { ShowStatus("收藏未保存",ex.Message,"error"); }
    }
    private void SaveNote()
    {
        if(!EnsureUnlocked()) return;
        if(SelectedAccount is not { } a) return;
        try
        {
            bool changed=a.Note!=NoteDraft;
            CommitCandidate(LocalAccountLibrary.EditAccount(_local,a,a.DisplayName,NoteDraft,a.FolderId),
                changed ? [NewActivity("修改备注",a.DisplayName,"account")] : []);
            ShowStatus("备注已保存",IsDemo?"演示备注仅保留在本次窗口。":"备注只保存在本机。","success");
        }
        catch(Exception ex) { ShowStatus("备注未保存",ex.Message,"error"); }
    }
    private void Confirm(string title,string message,Func<Task> action) { if(!EnsureUnlocked()) return; ConfirmationTitle=title; ConfirmationMessage=message; _confirmation=action; PendingConfirmation=true; }
    private void RequestRemove()
    {
        if(!EnsureUnlocked()) return;
        if(SelectedAccount is not { } account) return;
        Confirm("移出账号库？",$"将「{account.DisplayName}」从 Gameorder 隐藏。备注、分组和 Steam 登录数据会保留，重新导入可以找回。",()=>
        {
            try
            {
                var candidate=LocalAccountLibrary.Copy(_local); LocalAccountLibrary.UpsertMetadata(candidate,account);
                if(!candidate.HiddenAccounts.Contains(account.AccountName,StringComparer.OrdinalIgnoreCase)) candidate.HiddenAccounts.Add(account.AccountName);
                CommitCandidate(candidate,NewActivity("移出账号",account.DisplayName,"account")); _accounts.Remove(account); SelectedAccount=Accounts.Cast<SteamAccount>().FirstOrDefault(); UpdateCounts();
            }
            catch(Exception ex) { ShowStatus("账号未移出",ex.Message,"error"); }
            return Task.CompletedTask;
        });
    }
    private bool CanSwitch(bool offline)=>CanManage&&SelectedAccount is not null&&(IsDemo||IsPathValid)&&(!offline||SelectedAccount.HasCachedLogin);
    public Task SelectAndSwitchAsync(string steamId,SwitchMode mode)
    {
        if(!EnsureUnlocked()) return Task.CompletedTask;
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
        if(!EnsureUnlocked()) return Task.CompletedTask;
        if(!CanSwitch(offline)||SelectedAccount is not { } account) return Task.CompletedTask;
        string mode=offline?"离线":"在线";
        string message=$"将为「{account.DisplayName}」发起{mode}启动。"+(!IsDemo&&_steam.IsSteamRunning?"\nSteam 将正常退出并重新启动。请先保存游戏、完成下载中的重要任务。":"")+(offline?"\n离线需要本机有效的已记住登录；部分游戏仍需联网。":"\n如需密码或 Steam Guard 验证，请在 Steam 官方窗口完成。");
        Confirm($"使用{mode}模式切换",message,()=>ExecuteSwitch(account,offline));
        return Task.CompletedTask;
    }
    private async Task ExecuteSwitch(SteamAccount account,bool offline)
    {
        if(!EnsureUnlocked()) return;
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
        if(!EnsureUnlocked()) return;
        var name=NewAccountName.Trim(); var display=NewDisplayName.Trim();
        if(!Regex.IsMatch(name,@"^[A-Za-z0-9_]{2,64}$")) { AddFormError="请输入 2–64 位 Steam 登录名（字母、数字或下划线）。"; return; }
        if(display.Length>80) { AddFormError="显示名称不能超过 80 个字符。"; return; }
        if(_accounts.Any(a=>a.AccountName.Equals(name,StringComparison.OrdinalIgnoreCase))) { AddFormError="这个登录名已在账号库中。"; return; }
        var a=new SteamAccount { AccountName=name,DisplayName=string.IsNullOrWhiteSpace(display)?name:display,FolderId=LocalAccountLibrary.IsCustomFolder(_local,ActiveFolderId) ? ActiveFolderId : "" };
        try
        {
            var candidate=LocalAccountLibrary.Copy(_local); candidate.HiddenAccounts.RemoveAll(x=>x.Equals(name,StringComparison.OrdinalIgnoreCase)); LocalAccountLibrary.UpsertMetadata(candidate,a);
            CommitCandidate(candidate,NewActivity("添加账号",a.DisplayName,"account")); ApplyMetadata(a); _accounts.Add(a); SetFilter(0); SearchText=""; SelectedAccount=a; IsAddAccountOpen=false;
            UpdateCounts(); ShowStatus("账号已添加","点击在线登录，在 Steam 窗口完成首次验证；登录后可重新导入缓存状态。","success");
        }
        catch(Exception ex) { AddFormError=ex.Message; }
    }
    private void BrowseSteam()
    {
        if(!EnsureUnlocked()) return;
        var dialog=new OpenFileDialog { Title="选择 Steam 客户端",Filter="Steam 客户端 (steam.exe)|steam.exe",CheckFileExists=true };
        if(dialog.ShowDialog()==true) SteamPath=Path.GetDirectoryName(dialog.FileName)!;
    }
    private void DetectSteam() { if(!EnsureUnlocked()) return; SteamPath=_steam.DetectSteamPath(); SettingsMessage=IsPathValid?"已定位 Steam，保存后重新读取账号。":"未找到安装路径，请手动选择 steam.exe。"; }
    public void SetTaskbarWidgetDockPosition(string dock)=>TaskbarWidgetDock=dock;
    private void SaveTaskbarPreferences(bool enabled,string dock)
    {
        if(!EnsureUnlocked()) return;
        dock=LocalAccountLibrary.NormalizeTaskbarWidgetDock(dock);
        if(enabled==TaskbarWidgetEnabled&&dock==TaskbarWidgetDock) return;
        var candidate=LocalAccountLibrary.SetTaskbarPreferences(_local,enabled,dock);
        var entries=new List<ActivityEntry>();
        if(enabled!=TaskbarWidgetEnabled) entries.Add(NewActivity(enabled?"启用任务栏组件":"关闭任务栏组件","任务栏显示开关","settings"));
        if(dock!=TaskbarWidgetDock) entries.Add(NewActivity("调整任务栏位置",dock=="left"?"左侧":"托盘侧","settings"));
        candidate.Activity=entries.Concat(Activity).Take(100).ToList();
        try
        {
            SaveState(candidate);
            _local.Activity=candidate.Activity; PublishActivity(candidate.Activity);
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
        if(!EnsureUnlocked()) return;
        if(IsDemo) { SettingsMessage="演示模式下不会保存 Steam 路径。"; return; }
        var path=SteamPath.Trim().Trim('"'); if(File.Exists(path)&&Path.GetFileName(path).Equals("steam.exe",StringComparison.OrdinalIgnoreCase)) path=Path.GetDirectoryName(path)!;
        if(!Directory.Exists(path)||!File.Exists(Path.Combine(path,"steam.exe"))) { SettingsMessage="目录中未找到 steam.exe，请重新选择。"; return; }
        SteamPath=Path.GetFullPath(path); _local.SteamPath=SteamPath;
        if(SaveLocal()) { AddActivity("保存 Steam 路径",SteamPath,"success","settings"); SettingsMessage="路径已保存，账号库已更新。"; LoadAccounts(false); }
    }
    private Task RequestRestore()
    {
        if(!EnsureUnlocked()) return Task.CompletedTask;
        Confirm("恢复最近一次切换配置？","Steam 必须完全退出。将恢复最近一次切换前的账号选择和登录模式；Gameorder 备注不受影响。",async()=>
        {
            if(!EnsureUnlocked()) return;
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
    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true; ++_monitorGeneration;
        _timer.Stop(); _statusTimer.Stop(); _appLockTimer.Stop(); _lifetime.Cancel(); _avatarRefresh?.Cancel();
        _efficiency.Dispose(); _lifetime.Dispose();
    }
}
