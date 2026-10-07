using System.IO;
using System.Text;
using System.Text.Json;

namespace Gameorder;

public sealed class LocalStore(bool demo, string? storagePath = null)
{
    private readonly string _path = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatOrder", "accounts.json");
    public string? LoadError { get; private set; }
    private bool _readFailed;
    public LocalState Load()
    {
        if (demo || !File.Exists(_path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<LocalState>(File.ReadAllText(_path)) ?? throw new InvalidDataException("本地数据为空");
            state.Accounts ??= []; state.HiddenAccounts ??= []; state.Activity ??= []; state.Folders ??= [];
            if(state.SteamPath is null || state.Accounts.Any(a=>a is null || string.IsNullOrWhiteSpace(a.AccountName) || a.DisplayName is null || a.Note is null)
                || state.HiddenAccounts.Any(string.IsNullOrWhiteSpace) || state.Activity.Any(a=>a is null || a.Title is null || a.Description is null || a.Kind is null))
                throw new InvalidDataException("本地账号资料含有无效字段");
            LocalAccountLibrary.NormalizeFolders(state);
            LocalAccountLibrary.NormalizeTaskbarPreferences(state);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { _readFailed=true; LoadError="本地账号备注文件无法读取。原文件已保留，本次不会覆盖它。"; return new(); }
    }
    public void Save(LocalState state)
    {
        if (demo) return;
        if (_readFailed) throw new IOException(LoadError);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(LocalAccountLibrary.Copy(state), new JsonSerializerOptions { WriteIndented=true }), new UTF8Encoding(false)); File.Move(temporary,_path,true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

// Edits build a separate candidate. The view model publishes it only after atomic LocalStore.Save succeeds.
public static class LocalAccountLibrary
{
    public static LocalState Copy(LocalState source) => new()
    {
        SteamPath=source.SteamPath,
        TaskbarWidgetEnabled=source.TaskbarWidgetEnabled,
        TaskbarWidgetDock=NormalizeTaskbarWidgetDock(source.TaskbarWidgetDock),
        Accounts=source.Accounts.Select(a=>new AccountMetadata { AccountName=a.AccountName,DisplayName=a.DisplayName,Note=a.Note,IsFavorite=a.IsFavorite,IsManual=a.IsManual,FolderId=a.FolderId }).ToList(),
        Folders=source.Folders.Select(f=>new AccountFolder { Id=f.Id,Name=f.Name }).ToList(),
        HiddenAccounts=[..source.HiddenAccounts],
        Activity=source.Activity.Select(a=>new ActivityEntry { Title=a.Title,Description=a.Description,Time=a.Time,Kind=a.Kind,Category=a.Category }).ToList()
    };

    public static string NormalizeTaskbarWidgetDock(string? dock)=>string.Equals(dock?.Trim(),"left",StringComparison.OrdinalIgnoreCase)?"left":"tray";
    public static void NormalizeTaskbarPreferences(LocalState state)=>state.TaskbarWidgetDock=NormalizeTaskbarWidgetDock(state.TaskbarWidgetDock);
    public static LocalState SetTaskbarPreferences(LocalState source,bool enabled,string? dock)
    {
        var candidate=Copy(source);
        candidate.TaskbarWidgetEnabled=enabled;
        candidate.TaskbarWidgetDock=NormalizeTaskbarWidgetDock(dock);
        return candidate;
    }

    public static void NormalizeFolders(LocalState state)
    {
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        state.Folders=(state.Folders ?? []).Where(f=>f is not null&&Guid.TryParseExact(f.Id,"D",out _)&&IsValidName(f.Name)&&ids.Add(f.Id)&&names.Add(f.Name.Trim()))
            .Select(f=>new AccountFolder { Id=Guid.Parse(f.Id).ToString("D"),Name=f.Name.Trim() }).ToList();
        foreach(var account in state.Accounts)
            account.FolderId=Guid.TryParse(account.FolderId,out var id)&&state.Folders.Any(f=>f.Id==id.ToString("D")) ? id.ToString("D") : "";
    }

    private static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name)&&name.Trim().Length<=32&&!name.Any(char.IsControl)
        &&name.Trim() is not "全部账号" and not "未分组";
    private static string ValidName(LocalState state,string name,string? exceptId=null)
    {
        if(!IsValidName(name)) throw new ArgumentException("分组名需为 1–32 个字符，不能使用保留名称或换行。");
        name=name.Trim();
        if(state.Folders.Any(f=>f.Id!=exceptId&&f.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已存在同名分组。");
        return name;
    }
    public static bool IsCustomFolder(LocalState state,string? folderId) => !string.IsNullOrEmpty(folderId)&&state.Folders.Any(f=>f.Id==folderId);
    public static string FolderName(LocalState state,string? folderId) => state.Folders.FirstOrDefault(f=>f.Id==folderId)?.Name ?? "未分组";

    public static LocalState CreateFolder(LocalState source,string name,out string id)
    {
        var candidate=Copy(source); name=ValidName(candidate,name); id=Guid.NewGuid().ToString("D");
        candidate.Folders.Add(new() { Id=id,Name=name }); return candidate;
    }
    public static LocalState RenameFolder(LocalState source,string id,string name)
    {
        var candidate=Copy(source); var folder=candidate.Folders.SingleOrDefault(f=>f.Id==id) ?? throw new ArgumentException("请选择一个自定义分组。");
        folder.Name=ValidName(candidate,name,id); return candidate;
    }
    public static LocalState DeleteFolder(LocalState source,string id)
    {
        if(!IsCustomFolder(source,id)) throw new ArgumentException("请选择一个自定义分组。");
        var candidate=Copy(source); candidate.Folders.RemoveAll(f=>f.Id==id);
        foreach(var a in candidate.Accounts.Where(a=>a.FolderId==id)) a.FolderId="";
        return candidate;
    }
    public static void ApplyMetadata(LocalState state,SteamAccount account)
    {
        var metadata=state.Accounts.FirstOrDefault(a=>a.AccountName.Equals(account.AccountName,StringComparison.OrdinalIgnoreCase));
        if(metadata is not null)
        {
            if(!string.IsNullOrWhiteSpace(metadata.DisplayName)) account.DisplayName=metadata.DisplayName;
            account.Note=metadata.Note; account.IsFavorite=metadata.IsFavorite;
            account.FolderId=IsCustomFolder(state,metadata.FolderId) ? metadata.FolderId : "";
        }
        account.FolderName=FolderName(state,account.FolderId);
    }
    public static AccountMetadata UpsertMetadata(LocalState candidate,SteamAccount account)
    {
        var metadata=candidate.Accounts.FirstOrDefault(a=>a.AccountName.Equals(account.AccountName,StringComparison.OrdinalIgnoreCase));
        if(metadata is null) { metadata=new() { AccountName=account.AccountName,IsManual=string.IsNullOrEmpty(account.SteamId) }; candidate.Accounts.Add(metadata); }
        metadata.DisplayName=account.DisplayName; metadata.Note=account.Note; metadata.IsFavorite=account.IsFavorite;
        metadata.FolderId=IsCustomFolder(candidate,account.FolderId) ? account.FolderId : "";
        return metadata;
    }
    public static LocalState EditAccount(LocalState source,SteamAccount account,string displayName,string note,string? folderId)
    {
        displayName=displayName.Trim(); note=note.Trim(); folderId ??= "";
        if(displayName.Length>80) throw new ArgumentException("显示名称不能超过 80 个字符。");
        if(note.Length>1000) throw new ArgumentException("备注不能超过 1000 个字符。");
        if(folderId.Length>0&&!IsCustomFolder(source,folderId)) throw new ArgumentException("分组已不存在，请重新选择。");
        var candidate=Copy(source); var metadata=UpsertMetadata(candidate,account);
        metadata.DisplayName=string.IsNullOrEmpty(displayName) ? account.AccountName : displayName;
        metadata.Note=note; metadata.FolderId=folderId; return candidate;
    }
}
