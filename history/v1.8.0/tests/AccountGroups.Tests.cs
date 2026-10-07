using System.Text;
using Gameorder;

static class AccountGroupsTests
{
    public static IReadOnlyList<(string Name,Func<Task> Run)> Tests =>
    [
        ("Legacy local JSON loads all accounts into ungrouped",()=>Run(()=>
        {
            using var f=new GroupFixture();
            File.WriteAllText(f.Path,"""{"SteamPath":"","Accounts":[{"AccountName":"legacy_user","DisplayName":"老账号","Note":"保留备注","IsFavorite":true,"IsManual":true}],"HiddenAccounts":[],"Activity":[]}""");
            var store=new LocalStore(false,f.Path); var state=store.Load();
            Check(store.LoadError is null,"legacy JSON failed"); Equal(0,state.Folders.Count); Equal("",state.Accounts.Single().FolderId);
            Equal("保留备注",state.Accounts.Single().Note); Check(state.Accounts.Single().IsFavorite,"legacy favorite lost");
            store.Save(state); Equal("保留备注",new LocalStore(false,f.Path).Load().Accounts.Single().Note);
        })),
        ("Folder edits persist IDs, Chinese names and all account metadata",()=>Run(()=>
        {
            using var f=new GroupFixture(); var state=Initial();
            state=LocalAccountLibrary.CreateFolder(state,"主账号 🎮",out var id);
            state=LocalAccountLibrary.EditAccount(state,Account(),"新名称","第一行\n第二行",id);
            new LocalStore(false,f.Path).Save(state); var loaded=new LocalStore(false,f.Path).Load();
            Equal(id,loaded.Folders.Single().Id); Equal("主账号 🎮",loaded.Folders.Single().Name);
            Equal(id,loaded.Accounts.Single().FolderId); Equal("新名称",loaded.Accounts.Single().DisplayName); Equal("第一行\n第二行",loaded.Accounts.Single().Note);
            Check(loaded.Accounts.Single().IsFavorite,"favorite lost");
        })),
        ("Invalid and dangling folder IDs become ungrouped without losing accounts",()=>Run(()=>
        {
            using var f=new GroupFixture(); var valid=Guid.NewGuid().ToString("D");
            File.WriteAllText(f.Path,$$"""{"SteamPath":"","Folders":[null,{"Id":"..\\invalid","Name":"无效"},{"Id":"{{valid}}","Name":"有效"}],"Accounts":[{"AccountName":"alpha","DisplayName":"Alpha","Note":"保留","FolderId":"{{Guid.NewGuid():D}}"},{"AccountName":"beta","DisplayName":"Beta","Note":"第二个","FolderId":null}],"HiddenAccounts":[],"Activity":[]}""");
            var store=new LocalStore(false,f.Path); var state=store.Load();
            Check(store.LoadError is null,"recoverable folder data failed entire account library"); Equal(1,state.Folders.Count); Equal(2,state.Accounts.Count);
            Check(state.Accounts.All(a=>a.FolderId==""),"dangling folder not normalized"); Equal("保留",state.Accounts[0].Note);
        })),
        ("Delete folder only ungroups accounts and preserves notes, favorites and hidden accounts",()=>Run(()=>
        {
            var source=LocalAccountLibrary.CreateFolder(Initial(),"主账号",out var id); source.Accounts[0].FolderId=id;
            source.HiddenAccounts.Add("alpha"); var result=LocalAccountLibrary.DeleteFolder(source,id);
            Equal(0,result.Folders.Count); Equal(1,result.Accounts.Count); Equal("",result.Accounts[0].FolderId);
            Equal("保留备注",result.Accounts[0].Note); Check(result.Accounts[0].IsFavorite,"favorite deleted"); Equal("alpha",result.HiddenAccounts.Single());
            Equal(id,source.Accounts[0].FolderId); Equal(1,source.Folders.Count);
            Throws<ArgumentException>(()=>LocalAccountLibrary.DeleteFolder(source,"*"));
        })),
        ("Folder names are trimmed and unique across case and rename",()=>Run(()=>
        {
            var source=LocalAccountLibrary.CreateFolder(Initial(),"  Main  ",out var first);
            Equal("Main",source.Folders.Single().Name);
            Throws<ArgumentException>(()=>LocalAccountLibrary.CreateFolder(source," main ",out _));
            source=LocalAccountLibrary.CreateFolder(source,"Other",out var second);
            Throws<ArgumentException>(()=>LocalAccountLibrary.RenameFolder(source,second,"MAIN"));
            var result=LocalAccountLibrary.RenameFolder(source,first," MAIN "); Equal(first,result.Folders[0].Id); Equal("MAIN",result.Folders[0].Name); Equal("Main",source.Folders[0].Name);
            foreach(var name in new[]{"","未分组","全部账号","line\nnext",new string('x',33)}) Throws<ArgumentException>(()=>LocalAccountLibrary.CreateFolder(source,name,out _));
        })),
        ("Imported Steam account retains local folder and remark without changing login name",()=>Run(()=>
        {
            var state=LocalAccountLibrary.CreateFolder(Initial(),"主账号",out var id); state.Accounts[0].FolderId=id;
            var imported=new SteamAccount { AccountName="ALPHA",DisplayName="Steam 新昵称",SteamId="76561198000000001",HasCachedLogin=true };
            LocalAccountLibrary.ApplyMetadata(state,imported);
            Equal("ALPHA",imported.AccountName); Equal("本地昵称",imported.DisplayName); Equal("保留备注",imported.Note); Equal(id,imported.FolderId); Equal("主账号",imported.FolderName);
            var refreshed=LocalAccountLibrary.RenameFolder(state,id,"常用"); LocalAccountLibrary.ApplyMetadata(refreshed,imported); Equal("常用",imported.FolderName);
        })),
        ("Editing display, remark and folder does not mutate the source or Steam login identity",()=>Run(()=>
        {
            var source=LocalAccountLibrary.CreateFolder(Initial(),"小号",out var id); var live=Account();
            var candidate=LocalAccountLibrary.EditAccount(source,live,"显示用名称","  新备注  ",id);
            Equal("本地昵称",source.Accounts[0].DisplayName); Equal("保留备注",source.Accounts[0].Note); Equal("",source.Accounts[0].FolderId);
            Equal("alpha",candidate.Accounts[0].AccountName); Equal("显示用名称",candidate.Accounts[0].DisplayName); Equal("新备注",candidate.Accounts[0].Note); Equal(id,candidate.Accounts[0].FolderId);
            Equal("alpha",live.AccountName); Equal("原来昵称",live.DisplayName); Equal("原来备注",live.Note);
        })),
        ("Failed atomic edit save preserves original file and live state",()=>Run(()=>
        {
            using var f=new GroupFixture(); var damaged=Encoding.UTF8.GetBytes("{broken original file}"); File.WriteAllBytes(f.Path,damaged);
            var store=new LocalStore(false,f.Path); store.Load(); var source=Initial(); var live=Account();
            var candidate=LocalAccountLibrary.EditAccount(source,live,"不应提交","不能覆盖","");
            Throws<IOException>(()=>store.Save(candidate));
            Check(damaged.SequenceEqual(File.ReadAllBytes(f.Path)),"failed save overwrote original"); Equal("本地昵称",source.Accounts[0].DisplayName); Equal("保留备注",source.Accounts[0].Note); Equal("原来备注",live.Note);
            Check(!Directory.EnumerateFiles(f.Root,"*.tmp").Any(),"failed save leaked candidate");
        })),
        ("Canceled folder and account candidates cannot partially commit",()=>Run(()=>
        {
            var source=Initial(); var folderDraft=LocalAccountLibrary.CreateFolder(source,"取消的分组",out var id);
            var editDraft=LocalAccountLibrary.EditAccount(folderDraft,Account(),"取消的名称","取消的备注",id);
            Equal(0,source.Folders.Count); Equal("本地昵称",source.Accounts[0].DisplayName); Equal("保留备注",source.Accounts[0].Note); Equal("",source.Accounts[0].FolderId);
            Throws<ArgumentException>(()=>LocalAccountLibrary.EditAccount(source,Account(),"名称","备注",Guid.NewGuid().ToString("D")));
            Equal("",source.Accounts[0].FolderId); Equal(id,editDraft.Accounts[0].FolderId);
        })),
        ("Locked destination rejects edit without overwriting data or leaking temporary files",()=>Run(()=>
        {
            using var f=new GroupFixture(); var source=Initial(); var store=new LocalStore(false,f.Path); store.Save(source);
            var original=File.ReadAllBytes(f.Path); var candidate=LocalAccountLibrary.EditAccount(source,Account(),"不能提交","锁定文件","");
            using(var locked=new FileStream(f.Path,FileMode.Open,FileAccess.Read,FileShare.None)) ThrowsSaveFailure(()=>store.Save(candidate));
            Check(original.SequenceEqual(File.ReadAllBytes(f.Path)),"locked save replaced original data");
            Equal("本地昵称",source.Accounts[0].DisplayName); Equal("保留备注",source.Accounts[0].Note);
            Check(!Directory.EnumerateFiles(f.Root,"*.tmp").Any(),"locked save leaked candidate file");
        })),
        ("Hidden account import restores its persisted group and remark",()=>Run(()=>
        {
            using var f=new GroupFixture(); var state=LocalAccountLibrary.CreateFolder(Initial(),"恢复分组",out var id); state.Accounts[0].FolderId=id; state.HiddenAccounts.Add("alpha");
            new LocalStore(false,f.Path).Save(state); var loaded=new LocalStore(false,f.Path).Load(); loaded.HiddenAccounts.Clear();
            var imported=new SteamAccount { AccountName="alpha",SteamId="76561198000000001" }; LocalAccountLibrary.ApplyMetadata(loaded,imported);
            Equal(id,imported.FolderId); Equal("恢复分组",imported.FolderName); Equal("保留备注",imported.Note); Equal(1,loaded.Accounts.Count);
        })),
        ("Avatar note preview updates visibility and flattens multiline remarks",()=>Run(()=>
        {
            var a=new SteamAccount(); var notifications=new List<string?>(); a.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
            a.Note="主账号\r\n周末游戏"; Check(a.HasNote,"remark visibility remained false"); Equal("主账号 周末游戏",a.NotePreview);
            Check(notifications.Contains(nameof(SteamAccount.HasNote))&&notifications.Contains(nameof(SteamAccount.NotePreview)),"dependent remark properties not notified");
            a.Note="   "; Check(!a.HasNote,"blank remark still visible");
        }))
    ];

    private static SteamAccount Account()=>new() { AccountName="alpha",DisplayName="原来昵称",Note="原来备注",SteamId="76561198000000001",IsFavorite=true };
    private static LocalState Initial()=>new() { Accounts=[new() { AccountName="alpha",DisplayName="本地昵称",Note="保留备注",IsFavorite=true }], Activity=[new() { Title="保留活动" }] };
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool result,string message) { if(!result) throw new Exception(message); }
    private static void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private static void ThrowsSaveFailure(Action action) { try { action(); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return; } throw new Exception("Expected a blocked filesystem save"); }
    private sealed class GroupFixture:IDisposable
    {
        public string Root { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Gameorder-group-tests-"+Guid.NewGuid().ToString("N"));
        public string Path=>System.IO.Path.Combine(Root,"accounts.json");
        public GroupFixture()=>Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var root=System.IO.Path.GetFullPath(Root); var temp=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))+System.IO.Path.DirectorySeparatorChar;
            if(!root.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!System.IO.Path.GetFileName(root).StartsWith("Gameorder-group-tests-",StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup target");
            Directory.Delete(root,true);
        }
    }
}
