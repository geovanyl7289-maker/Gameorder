using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Gameorder;

/// <summary>Explicit application-owned storage boundaries. Reset never traverses a Steam installation.</summary>
public sealed class GameorderDataContext
{
    public const string TestPassword="12345678";
    private const string TestMarkerText="GAMEORDER RESET TEST CONTEXT v1";
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly string[] _avatarDirectories;
    private readonly string _testMarker,_seedMarker,_importMarker;
    public string Root {get;}
    public string AccountsPath {get;}
    public string SecurityPath {get;}
    public string AvatarsPath=>_avatarDirectories[0];
    public string BackupsPath {get;}
    public bool IsResetTest {get;}
    public bool SuppressAutomaticImport=>File.Exists(_importMarker);
    public LocalStore Store {get;}
    public AppLockService LockService {get;}
    private GameorderDataContext(string root,bool test)
    {
        Root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));IsResetTest=test;
        var appData=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AccountsPath=test?Path.Combine(Root,"accounts.json"):Path.Combine(appData,"FloatOrder","accounts.json");
        SecurityPath=Path.Combine(Root,"security.golock");BackupsPath=Path.Combine(Root,"backups");
        _avatarDirectories=test?[Path.Combine(Root,"avatars")]:[Path.Combine(appData,"FloatOrder","avatars"),Path.Combine(Root,"avatars")];
        _testMarker=Path.Combine(Root,"reset-test.context");_seedMarker=Path.Combine(Root,"fixture-ready.marker");_importMarker=Path.Combine(Root,"manual-import-required.marker");
        LockService=new AppLockService(new AppLockFileStorage(SecurityPath));Store=new LocalStore(false,AccountsPath,LockService);
    }
    public static GameorderDataContext CreateProduction()=>new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Gameorder"),false);
    public static GameorderDataContext CreateTest(string? root=null)
    {
        var explicitRoot=root is not null;
        root??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Gameorder","ResetTest");
        if(!Path.IsPathFullyQualified(root)) throw new ArgumentException("测试资料目录必须是绝对路径。");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(explicitRoot&&!full.StartsWith("H:\\Documents\\Playground\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("显式开发测试目录必须位于 H:\\Documents\\Playground 内。");
        ValidateNoReparseOrSteam(full);
        bool initialize=!Directory.Exists(full)||!Directory.EnumerateFileSystemEntries(full).Any();
        if(!initialize&&!ValidMarker(Path.Combine(full,"reset-test.context"))) throw new IOException("非空目录没有 Gameorder 测试归属标记，拒绝操作。");
        Directory.CreateDirectory(full);
        if(initialize) File.WriteAllText(Path.Combine(full,"reset-test.context"),TestMarkerText,new UTF8Encoding(false));
        var context=new GameorderDataContext(full,true);
        if(initialize) context.RestoreTestDataAsync().GetAwaiter().GetResult();
        return context;
    }
    public IReadOnlyList<SteamAccount> GetPublicAccountPreview()
    {
        if(!IsResetTest||!File.Exists(_seedMarker)) return [];
        return Enumerable.Range(1,3).Select(i=>new SteamAccount {AccountName="reset_test_"+i,DisplayName=i switch {1=>"测试主账号",2=>"测试小号",_=>"测试周末玩家"},
            SteamId="7656119800000010"+i,HasCachedLogin=i!=2,Accent=i switch {1=>"#CDDEFF",2=>"#D0EFDC",_=>"#FFE1C4"},AvatarPath=Path.Combine(AvatarsPath,"test-avatar-"+i+".png")}).ToArray();
    }
    public void MarkManualImportCompleted()
    {ValidateContext();ValidateOwnedFile(_importMarker);File.Delete(_importMarker);}
    public async Task<LocalState> ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {return await Task.Run(ResetCore).ConfigureAwait(false);} finally {_gate.Release();}
    }
    public async Task<LocalState> RestoreTestDataAsync()
    {
        if(!IsResetTest) throw new InvalidOperationException("只有隔离测试环境可以重建演示资料。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(()=>
            {
                _=ResetCore();
                var main=Guid.NewGuid().ToString("D");var secondary=Guid.NewGuid().ToString("D");
                var state=new LocalState {AutoImportSteamAccounts=true,AppLockDelayMinutes=5,Folders=[new(){Id=main,Name="测试主账号"},new(){Id=secondary,Name="测试小号"}],
                    Accounts=Enumerable.Range(1,3).Select(i=>new AccountMetadata {AccountName="reset_test_"+i,DisplayName=i==1?"测试主账号":i==2?"测试小号":"测试周末玩家",Note="仅用于重置测试的备注 "+i,FolderId=i==1?main:secondary,IsFavorite=i==1,IsManual=true}).ToList(),
                    Activity=[new(){Title="测试资料已准备",Description="这些账号、备注、头像和备份均为独立演示数据",Kind="info",Category="account"}]};
                Store.Save(state);Directory.CreateDirectory(AvatarsPath);Directory.CreateDirectory(Path.Combine(BackupsPath,new string('f',64)));
                // Original geometric PNG portraits; no real cached Steam avatar is read.
                for(int i=1;i<=3;i++) File.WriteAllBytes(Path.Combine(AvatarsPath,"test-avatar-"+i+".png"),ResetTestAvatarFactory.Create(i));
                var fake=Encoding.UTF8.GetBytes("Gameorder reset-test backup fixture");
                try {File.WriteAllBytes(Path.Combine(BackupsPath,new string('f',64),"synthetic.gobk"),WindowsDataProtection.Protect(fake));} finally {CryptographicOperations.ZeroMemory(fake);}
                if(!LockService.Enable(TestPassword,out var error)) throw new IOException(error);
                File.WriteAllText(_seedMarker,"PUBLIC SYNTHETIC FIXTURE ONLY",new UTF8Encoding(false));File.Delete(_importMarker);LockService.Lock();
                _=Store.Load();return new LocalState {AutoImportSteamAccounts=true,AppLockDelayMinutes=5};
            }).ConfigureAwait(false);
        }
        finally {_gate.Release();}
    }
    private LocalState ResetCore()
    {
        ValidateContext();
        var directories=_avatarDirectories.Append(BackupsPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Validate the entire deletion plan before deleting the first file, including every descendant.
        var plans=directories.Select(PlanDirectory).ToArray();
        var ownedFiles=new List<string>();
        foreach(var target in new[]{AccountsPath,SecurityPath,_seedMarker}) {ValidateOwnedFile(target);if(File.Exists(target)) ownedFiles.Add(target);}
        foreach(var target in new[]{AccountsPath,SecurityPath})
        {
            var parent=Path.GetDirectoryName(target)!;
            if(!Directory.Exists(parent)) continue;
            foreach(var candidate in Directory.EnumerateFiles(parent,Path.GetFileName(target)+".*.tmp"))
                if(IsOwnedTemporary(target,candidate)) {ValidateOwnedFile(candidate);ownedFiles.Add(candidate);}
        }
        Directory.CreateDirectory(Root);File.WriteAllText(_importMarker,"MANUAL IMPORT REQUIRED AFTER DESTRUCTIVE RESET",new UTF8Encoding(false));
        LockService.Lock();
        foreach(var plan in plans)
        {
            foreach(var file in plan.Files) {ValidateNoReparseOrSteam(file);File.Delete(file);}
            foreach(var directory in plan.Directories.OrderByDescending(p=>p.Length)) {ValidateNoReparseOrSteam(directory);Directory.Delete(directory,false);}
        }
        // Keep the safety configuration until all destructive deletions and the empty state are durable.
        foreach(var file in ownedFiles.Where(p=>!p.Equals(SecurityPath,StringComparison.OrdinalIgnoreCase))) {ValidateOwnedFile(file);File.Delete(file);}
        var empty=new LocalState {AutoImportSteamAccounts=false,AppLockDelayMinutes=5};
        new LocalStore(false,AccountsPath).Save(empty);
        ValidateOwnedFile(SecurityPath);File.Delete(SecurityPath);
        if(File.Exists(SecurityPath)||directories.Any(Directory.Exists)) throw new IOException("仍有本地资料未清空，重置未完成。");
        LockService.ResetAfterDestruction();Store.ResetAfterDestruction();return Store.Load();
    }
    private void ValidateContext()
    {
        ValidateNoReparseOrSteam(Root);
        if(IsResetTest)
        {if(!ValidMarker(_testMarker)) throw new IOException("测试目录归属标记缺失或损坏，拒绝重置。");}
        else
        {
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Gameorder");
            if(!Path.GetFullPath(expected).Equals(Root,StringComparison.OrdinalIgnoreCase)) throw new IOException("正式资料根不匹配。");
        }
        foreach(var file in new[]{AccountsPath,SecurityPath,_importMarker,_seedMarker}) ValidateOwnedFile(file);
    }
    private void ValidateOwnedFile(string path)
    {
        var full=Path.GetFullPath(path);var parent=Path.GetDirectoryName(full)!;
        var allowed=IsResetTest?new[]{Root}:new[]{Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FloatOrder")};
        if(!allowed.Any(p=>parent.Equals(Path.GetFullPath(p),StringComparison.OrdinalIgnoreCase))) throw new IOException("文件不属于 Gameorder 资料边界。");
        ValidateNoReparseOrSteam(full);
    }
    private (List<string> Files,List<string> Directories) PlanDirectory(string root)
    {
        root=Path.GetFullPath(root);ValidateNoReparseOrSteam(root);var files=new List<string>();var directories=new List<string>();
        if(!Directory.Exists(root)) return (files,directories);
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var current))
        {
            if(files.Count+directories.Count>100000) throw new IOException("资料目录超出安全清理数量限制。");
            ValidateNoReparseOrSteam(current);directories.Add(current);
            foreach(var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var full=Path.GetFullPath(entry);
                if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("清理路径越界。");
                ValidateNoReparseOrSteam(full);
                if((File.GetAttributes(full)&FileAttributes.Directory)!=0) pending.Push(full);else files.Add(full);
            }
        }
        return (files,directories);
    }
    private static bool IsOwnedTemporary(string target,string candidate)
    {var name=Path.GetFileName(candidate);var prefix=Path.GetFileName(target)+".";return name.StartsWith(prefix,StringComparison.Ordinal)&&name.EndsWith(".tmp",StringComparison.Ordinal)&&Guid.TryParseExact(name[prefix.Length..^4],"N",out _);}
    private static bool ValidMarker(string path)
    {try {return File.Exists(path)&&new FileInfo(path).Length<128&&File.ReadAllText(path)==TestMarkerText;}catch(IOException){return false;}}
    private static void ValidateNoReparseOrSteam(string path)
    {
        var current=Path.GetFullPath(path);
        while(!string.IsNullOrEmpty(current))
        {
            try {if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0) throw new IOException("资料路径含链接或重解析点，拒绝操作。");}
            catch(FileNotFoundException) { } catch(DirectoryNotFoundException) { }
            if(Directory.Exists(current)&&(File.Exists(Path.Combine(current,"steam.exe"))||Directory.Exists(Path.Combine(current,"steamapps")))) throw new IOException("拒绝操作 Steam 安装或游戏目录。");
            current=Path.GetDirectoryName(current)??"";
        }
    }
}
