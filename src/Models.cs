using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Gameorder;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field,value)) return false; field=value; Notify(name); return true; }
}

public sealed class SteamAccount : ObservableObject
{
    public string SteamId { get; set; } = "";
    public string AccountName { get; set; } = "";
    private string _displayName = "";
    public string DisplayName { get => string.IsNullOrWhiteSpace(_displayName) ? AccountName : _displayName; set { if (Set(ref _displayName,value)) Notify(nameof(Initials)); } }
    public bool HasCachedLogin { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public bool IsCurrentSelection { get; set; }
    private bool _isActive;
    private SteamSessionState _presenceState = SteamSessionState.Stopped;
    private string _presenceHint = "此账号当前未登录本机 Steam";
    public bool IsActive => _isActive;
    public bool IsOnline => IsActive && _presenceState == SteamSessionState.Online;
    public bool IsOffline => IsActive && _presenceState == SteamSessionState.Offline;
    public string PresenceLabel => !IsActive ? "未登录" : _presenceState switch
    {
        SteamSessionState.Online => "在线",
        SteamSessionState.Offline => "离线登录",
        SteamSessionState.AwaitingLogin => "登录中",
        _ => "待确认"
    };
    public string PresenceHint => _presenceHint;
    public void ApplySession(bool active, SteamSessionState state, string hint)
    {
        if (_isActive == active && _presenceState == state && _presenceHint == hint) return;
        _isActive = active; _presenceState = state; _presenceHint = hint;
        Notify(nameof(IsActive)); Notify(nameof(IsOnline)); Notify(nameof(IsOffline));
        Notify(nameof(PresenceLabel)); Notify(nameof(PresenceHint)); Notify(nameof(IsPlaying)); Notify(nameof(RunningGameName)); Notify(nameof(GamePresenceLabel)); Notify(nameof(GamePresenceHint));
    }
    private bool _isPlaying;
    private string _runningGameName="", _runningGameHint="";
    public bool IsPlaying => IsActive&&_isPlaying;
    public string RunningGameName => IsPlaying?_runningGameName:"";
    public string GamePresenceLabel => IsPlaying?"游戏中":PresenceLabel;
    public string GamePresenceHint => IsPlaying?_runningGameHint:PresenceHint;
    public void ApplyGame(bool running,string gameName,string hint)
    {
        if(_isPlaying==running&&_runningGameName==gameName&&_runningGameHint==hint) return;
        _isPlaying=running; _runningGameName=gameName; _runningGameHint=hint;
        Notify(nameof(IsPlaying)); Notify(nameof(RunningGameName)); Notify(nameof(GamePresenceLabel)); Notify(nameof(GamePresenceHint));
    }
    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set { if (Set(ref _isFavorite,value)) Notify(nameof(FavoriteLabel)); } }
    private string _note="";
    public string Note { get=>_note; set { if(Set(ref _note,value ?? "")) { Notify(nameof(HasNote)); Notify(nameof(NotePreview)); } } }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public string NotePreview => string.Join(" ",Note.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries)).Trim();
    private string _folderId="", _folderName="未分组";
    public string FolderId { get=>_folderId; set=>Set(ref _folderId,value ?? ""); }
    public string FolderName { get=>_folderName; set=>Set(ref _folderName,value); }
    public string Accent { get; set; } = "#E9EFFB";
    private string? _avatarPath;
    public string? AvatarPath { get=>_avatarPath; set { if(Set(ref _avatarPath,value)) { Notify(nameof(HasAvatar)); Notify(nameof(IsDefaultAvatar)); Notify(nameof(AvatarHint)); } } }
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarPath);
    public bool IsDefaultAvatar => AvatarPath is not null && System.IO.Path.GetFileNameWithoutExtension(AvatarPath).EndsWith(".steam-default",StringComparison.OrdinalIgnoreCase);
    private bool _avatarLoading;
    public bool AvatarLoading { get=>_avatarLoading; set { if(Set(ref _avatarLoading,value)) Notify(nameof(AvatarHint)); } }
    public string AvatarHint => AvatarLoading ? "头像加载中…" : HasAvatar ? IsDefaultAvatar ? "Steam 默认头像" : "Steam 头像" : string.IsNullOrEmpty(SteamId) ? "首次登录后获取头像" : "头像暂不可用";
    public string Initials { get { var s = DisplayName.Trim(); return s.Length == 0 ? "S" : (char.IsSurrogate(s[0]) && s.Length > 1 ? s[..2] : s[..1]).ToUpperInvariant(); } }
    public string CacheLabel => HasCachedLogin ? "已记住登录" : "需在 Steam 验证";
    public string LastLoginLabel => LastLoginAt.HasValue ? LastLoginAt.Value.ToLocalTime().ToString("yyyy.MM.dd  HH:mm") : "尚无本机登录记录";
    public string FavoriteLabel => IsFavorite ? "已收藏" : "收藏账号";
    public string SelectionHint => IsCurrentSelection ? "Steam 上次选择" : (string.IsNullOrEmpty(SteamId) ? "手动添加" : "本机账号");
}

public sealed class ActivityEntry
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Kind { get; set; } = "info";
    public string Category { get; set; } = "";
    [JsonIgnore] public string EffectiveCategory => OperationLogService.CategoryOf(this);
    [JsonIgnore] public string CategoryLabel => OperationLogService.CategoryLabel(this);
    public string TimeLabel => Time.ToLocalTime().ToString("MM.dd  HH:mm:ss");
}

public sealed class AccountMetadata
{
    public string AccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsFavorite { get; set; }
    public bool IsManual { get; set; }
    public string FolderId { get; set; } = "";
}

public sealed class AccountFolder : ObservableObject
{
    public string Id { get; set; } = "";
    private string _name="";
    public string Name { get=>_name; set=>Set(ref _name,value); }
    private int _accountCount;
    [JsonIgnore] public int AccountCount { get=>_accountCount; set=>Set(ref _accountCount,value); }
    private bool _isSelected;
    [JsonIgnore] public bool IsSelected { get=>_isSelected; set=>Set(ref _isSelected,value); }
}

public sealed class LocalState
{
    public string SteamPath { get; set; } = "";
    public bool TaskbarWidgetEnabled { get; set; } = true;
    public string TaskbarWidgetDock { get; set; } = "tray";
    public int AppLockDelayMinutes { get; set; } = 5;
    public bool AutoImportSteamAccounts { get; set; } = true;
    public List<AccountMetadata> Accounts { get; set; } = [];
    public List<AccountFolder> Folders { get; set; } = [];
    public List<string> HiddenAccounts { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];
}
