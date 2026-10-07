using System.IO;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Security.Cryptography;

namespace Gameorder;

public sealed class LocalStore
{
    private readonly bool _demo;
    private readonly string _path;
    private readonly AppLockService? _appLock;
    private readonly object _fileGate=new();
    private bool _readFailed,_loaded;
    private byte[]? _knownFileHash;
    public string StoragePath=>_path;
    public bool IsDemo=>_demo;
    public string? LoadError {get;private set;}
    public bool IsEncrypted {get;private set;}
    public bool IsPasswordProtected {get;private set;}
    public bool RequiresUnlock {get;private set;}
    public bool WasMigrated {get;private set;}
    public bool CanSave=>!_demo&&!_readFailed&&!RequiresUnlock&&(_appLock is null||!_appLock.Enabled||_appLock.IsAuthenticated);
    public string DataProtectionStatus {get;private set;}
    public LocalStore(bool demo,string? storagePath=null,AppLockService? appLockService=null)
    {
        _demo=demo; _path=storagePath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FloatOrder","accounts.json");
        _appLock=appLockService;
        DataProtectionStatus=demo?"演示数据，不读取或保存本机资料":"当前 Windows 用户加密保护";
        if(!demo) _appLock?.AttachStore(this);
    }
    internal bool HasPasswordProtectedFile
    {
        get
        {
            try { using var stream=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.Read); Span<byte> magic=stackalloc byte[8]; return stream.Read(magic)==8&&EncryptedLocalFile.HasPasswordMagic(magic); }
            catch(FileNotFoundException) {return false;} catch(DirectoryNotFoundException) {return false;}
        }
    }
    public LocalState Load()=>_appLock is null?LoadCore(false,null,Guid.Empty):_appLock.AccessStore(LoadCore);
    private LocalState LoadCore(bool passwordRequired,byte[]? key,Guid keyId)
    {
        lock(_fileGate)
        {
            if(_demo||_readFailed) return new();
            if(passwordRequired&&key is null) {RequiresUnlock=true;DataProtectionStatus="账号资料由应用密码保护，请先解锁";return new();}
            RequiresUnlock=false;
            byte[]? disk=null,json=null; LocalState? parsed=null; bool migrating=false;
            try
            {
                EncryptedLocalFile.RejectInterruptedWrite(_path);
                disk=EncryptedLocalFile.ReadIfExists(_path);_loaded=true;
                if(disk is null) {_knownFileHash=null;IsEncrypted=false;IsPasswordProtected=false;return new();}
                _knownFileHash=SHA256.HashData(disk);
                IsPasswordProtected=EncryptedLocalFile.HasPasswordMagic(disk);IsEncrypted=IsPasswordProtected||EncryptedLocalFile.HasEncryptedMagic(disk);
                if(IsPasswordProtected&&(!passwordRequired||key is null))
                {RequiresUnlock=true;LoadError="密码保护资料的安全配置缺失或未解锁。原文件已保留，不能覆盖；忘记密码只能清空本地资料重置。";DataProtectionStatus="缺少有效密码密钥，资料已保留";return new();}
                json=Decode(disk,key,keyId); parsed=ParseAndValidate(json);
                if(!IsEncrypted||passwordRequired&&!IsPasswordProtected)
                {
                    migrating=true;var encrypted=Encode(json,passwordRequired?key:null,keyId);
                    EncryptedLocalFile.WriteAtomically(_path,encrypted,_knownFileHash);_knownFileHash=SHA256.HashData(encrypted);
                    IsEncrypted=true;IsPasswordProtected=passwordRequired;WasMigrated=true;
                }
                LoadError=null;DataProtectionStatus=IsPasswordProtected?"应用密码参与 AES-GCM 加密保护":"已由当前 Windows 用户加密保护";
                return parsed;
            }
            catch(Exception ex) when(IsStorageFailure(ex))
            {
                _readFailed=true;LoadError=migrating?"资料加密迁移未完成。原文件已保留，本次不能覆盖。":"本地账号资料无法读取或解密。原文件已保留，本次不能覆盖。";
                DataProtectionStatus="资料无法读取或迁移，原文件已保留，保存已停用";return parsed??new();
            }
            finally {Clear(json);Clear(disk);}
        }
    }
    public void Save(LocalState state)
    {
        if(_appLock is null) SaveCore(state,false,null,Guid.Empty);
        else _appLock.AccessStore((required,key,id)=>{SaveCore(state,required,key,id);return true;});
    }
    private void SaveCore(LocalState state,bool passwordRequired,byte[]? key,Guid keyId)
    {
        lock(_fileGate)
        {
            if(_demo) return;
            try {EncryptedLocalFile.RejectInterruptedWrite(_path);}
            catch(IOException ex) {_readFailed=true;LoadError=ex.Message;throw;}
            if(passwordRequired&&key is null) {RequiresUnlock=true;throw new UnauthorizedAccessException("请先解锁密码保护的账号资料。");}
            if(!_loaded&&!_readFailed) _=LoadCore(passwordRequired,key,keyId);
            if(_readFailed||RequiresUnlock) throw new IOException(LoadError??"资料尚未解锁，不能覆盖。");
            byte[]? json=null;
            try
            {
                Validate(state);var candidate=LocalAccountLibrary.Copy(state);LocalAccountLibrary.NormalizeFolders(candidate);LocalAccountLibrary.NormalizeTaskbarPreferences(candidate);
                json=EncryptedLocalFile.SerializeBounded(candidate);var encrypted=Encode(json,passwordRequired?key:null,keyId);
                EncryptedLocalFile.WriteAtomically(_path,encrypted,_knownFileHash);_knownFileHash=SHA256.HashData(encrypted);
                IsEncrypted=true;IsPasswordProtected=passwordRequired;DataProtectionStatus=passwordRequired?"应用密码参与 AES-GCM 加密保护":"已由当前 Windows 用户加密保护";
            }
            catch(LocalFileChangedException)
            {_readFailed=true;LoadError="本地资料已由其他操作更改。原文件已保留，请重新打开读取。";throw new IOException(LoadError);}
            finally {Clear(json);}
        }
    }
    // Called while AppLockService holds its gate: every store operation uses the same gate ordering.
    internal void ChangeProtection(byte[]? currentKey,Guid currentKeyId,byte[]? targetKey,Guid targetKeyId,Action commitConfiguration,Action? finishConfiguration,Action rollbackConfiguration,
        Action<string,string>? replaceForVerification=null,Action<int>? waitForVerification=null)
    {
        lock(_fileGate)
        {
            if(_demo) {commitConfiguration();finishConfiguration?.Invoke();return;}
            if(_readFailed) throw new IOException(LoadError);
            byte[]? original=null,json=null,changed=null,rollbackBytes=null;bool configurationAttempted=false,metadataCommitted=false;
            try
            {
                EncryptedLocalFile.RejectInterruptedWrite(_path);
                original=EncryptedLocalFile.ReadIfExists(_path);
                var beforeHash=original is null?null:SHA256.HashData(original);
                if(_loaded&&(_knownFileHash is null?beforeHash is not null:beforeHash is null||!CryptographicOperations.FixedTimeEquals(_knownFileHash,beforeHash))) throw new LocalFileChangedException();
                json=original is null?EncryptedLocalFile.SerializeBounded(new LocalState()):Decode(original,currentKey,currentKeyId);
                _=ParseAndValidate(json); changed=Encode(json,targetKey,targetKeyId);
                // Legacy rollback preserves equivalent JSON under DPAPI, never writes plaintext back.
                rollbackBytes=original is null?null:EncryptedLocalFile.HasEncryptedMagic(original)||EncryptedLocalFile.HasPasswordMagic(original)?original.ToArray():EncryptedLocalFile.Encrypt(json);
                EncryptedLocalFile.WriteAtomically(_path,changed,beforeHash,_=>{configurationAttempted=true;commitConfiguration();},replaceForVerification,waitForVerification);
                metadataCommitted=true;finishConfiguration?.Invoke();
                _knownFileHash=SHA256.HashData(changed);_loaded=true;RequiresUnlock=false;LoadError=null;IsEncrypted=true;IsPasswordProtected=targetKey is not null;
                DataProtectionStatus=IsPasswordProtected?"应用密码参与 AES-GCM 加密保护":"已由当前 Windows 用户加密保护";
            }
            catch(Exception failure)
            {
                // If native replacement lost its target, the surviving replacement
                // already uses the newly committed key. Keep that key configuration
                // and ciphertext together, and block reads/saves until recovery.
                bool interrupted=configurationAttempted&&original is not null&&!File.Exists(_path);
                bool rollbackFailed=false,configurationRestored=!interrupted;
                // A committed metadata change with a finish step is Disable:
                // restore the password key configuration before restoring AES.
                // If its persistence fails, keep the committed DPAPI data so a
                // restart never leaves AES as the sole copy without its key.
                if(configurationAttempted&&!interrupted)
                {try {rollbackConfiguration();}catch {rollbackFailed=true;configurationRestored=false;}}
                if(metadataCommitted&&configurationRestored)
                {
                    try {if(rollbackBytes is null) File.Delete(_path);else EncryptedLocalFile.WriteAtomically(_path,rollbackBytes,SHA256.HashData(changed!));
                        _knownFileHash=rollbackBytes is null?null:SHA256.HashData(rollbackBytes);_loaded=true;}
                    catch {rollbackFailed=true;}
                }
                else if(metadataCommitted)
                {
                    _knownFileHash=SHA256.HashData(changed!);_loaded=true;
                    IsEncrypted=true;IsPasswordProtected=targetKey is not null;
                }
                if(interrupted||rollbackFailed) {_readFailed=true;LoadError="保护方式迁移未完成，可恢复的加密资料已保留；不要覆盖或删除资料目录。";throw new IOException(LoadError,failure);}
                throw;
            }
            finally {Clear(json);Clear(original);Clear(changed);Clear(rollbackBytes);}
        }
    }
    internal void ValidateAuthenticatedProtection(byte[] key,Guid id)
    {
        lock(_fileGate)
        {
            byte[]? disk=null,json=null;
            try
            {
                EncryptedLocalFile.RejectInterruptedWrite(_path);
                disk=EncryptedLocalFile.ReadIfExists(_path);
                if(disk is null) return;
                json=Decode(disk,key,id);_=ParseAndValidate(json);
                if(!EncryptedLocalFile.HasPasswordMagic(disk))
                {var encrypted=Encode(json,key,id);EncryptedLocalFile.WriteAtomically(_path,encrypted,SHA256.HashData(disk));_knownFileHash=SHA256.HashData(encrypted);_loaded=true;WasMigrated=true;}
                RequiresUnlock=false;LoadError=null;
            }
            finally {Clear(json);Clear(disk);}
        }
    }
    internal void ResetAfterDestruction()
    {lock(_fileGate) {_readFailed=false;_loaded=false;_knownFileHash=null;RequiresUnlock=false;LoadError=null;IsEncrypted=false;IsPasswordProtected=false;WasMigrated=false;DataProtectionStatus="本地资料已清空";}}
    private static byte[] Decode(byte[] disk,byte[]? key,Guid id)=>EncryptedLocalFile.HasPasswordMagic(disk)?key is null?throw new CryptographicException("资料需要应用密码密钥"):EncryptedLocalFile.DecryptWithPassword(disk,key,id):EncryptedLocalFile.HasEncryptedMagic(disk)?EncryptedLocalFile.Decrypt(disk):EncryptedLocalFile.LegacyJsonToUtf8(disk);
    private static byte[] Encode(byte[] json,byte[]? key,Guid id)=>key is null?EncryptedLocalFile.Encrypt(json):EncryptedLocalFile.EncryptWithPassword(json,key,id);
    private static void Clear(byte[]? bytes) {if(bytes is not null) CryptographicOperations.ZeroMemory(bytes);}
    private static LocalState ParseAndValidate(byte[] json)
    {
        var state=JsonSerializer.Deserialize<LocalState>(json,new JsonSerializerOptions {MaxDepth=32})??throw new InvalidDataException("本地数据为空");
        state.Accounts??=[];state.HiddenAccounts??=[];state.Activity??=[];state.Folders??=[];Validate(state,true);
        LocalAccountLibrary.NormalizeFolders(state);LocalAccountLibrary.NormalizeTaskbarPreferences(state);return state;
    }
    private static void Validate(LocalState state,bool allowRecoverableFolders=false)
    {
        if(state is null||state.SteamPath is null||state.Accounts is null||state.HiddenAccounts is null||state.Activity is null||state.Folders is null
            ||state.Accounts.Count>10000||state.HiddenAccounts.Count>10000||state.Folders.Count>10000||state.Activity.Count>50000
            ||state.Accounts.Any(a=>a is null||string.IsNullOrWhiteSpace(a.AccountName)||a.DisplayName is null||a.Note is null)
            ||state.HiddenAccounts.Any(string.IsNullOrWhiteSpace)||state.Activity.Any(a=>a is null||a.Title is null||a.Description is null||a.Kind is null)
            ||(!allowRecoverableFolders&&state.Folders.Any(f=>f is null))) throw new InvalidDataException("本地资料字段或大小无效");
    }
    internal static bool IsStorageFailure(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception or CryptographicException or PlatformNotSupportedException or ArgumentException or AggregateException;
}
// Edits build a separate candidate. The view model publishes it only after atomic LocalStore.Save succeeds.
public static class LocalAccountLibrary
{
    public static LocalState Copy(LocalState source) => new()
    {
        SteamPath=source.SteamPath,
        AppLockDelayMinutes=source.AppLockDelayMinutes,
        AutoImportSteamAccounts=source.AutoImportSteamAccounts,
        TaskbarWidgetEnabled=source.TaskbarWidgetEnabled,
        TaskbarWidgetDock=NormalizeTaskbarWidgetDock(source.TaskbarWidgetDock),
        Accounts=source.Accounts.Select(a=>new AccountMetadata { AccountName=a.AccountName,DisplayName=a.DisplayName,Note=a.Note,IsFavorite=a.IsFavorite,IsManual=a.IsManual,FolderId=a.FolderId }).ToList(),
        Folders=source.Folders.Select(f=>new AccountFolder { Id=f.Id,Name=f.Name }).ToList(),
        HiddenAccounts=[..source.HiddenAccounts],
        Activity=source.Activity.Select(a=>new ActivityEntry { Title=a.Title,Description=a.Description,Time=a.Time,Kind=a.Kind,Category=a.Category }).ToList()
    };

    public static string NormalizeTaskbarWidgetDock(string? dock)=>string.Equals(dock?.Trim(),"left",StringComparison.OrdinalIgnoreCase)?"left":"tray";
    public static void NormalizeTaskbarPreferences(LocalState state)
    {state.TaskbarWidgetDock=NormalizeTaskbarWidgetDock(state.TaskbarWidgetDock);state.AppLockDelayMinutes=state.AppLockDelayMinutes is 0 or 1 or 3 or 5 or 10?state.AppLockDelayMinutes:5;}
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
