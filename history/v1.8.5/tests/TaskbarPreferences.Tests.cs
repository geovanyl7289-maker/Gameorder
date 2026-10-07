using System.Text;
using Gameorder;

static class TaskbarPreferencesTests
{
    public static IReadOnlyList<(string Name,Func<Task> Run)> Tests =>
    [
        ("Legacy metadata defaults to an enabled tray-side taskbar widget",()=>Run(()=>
        {
            using var f=new PreferenceFixture();
            File.WriteAllText(f.Path,"""{"SteamPath":"","Accounts":[{"AccountName":"fixture_user","DisplayName":"旧昵称","Note":"旧备注","IsFavorite":true}],"HiddenAccounts":[],"Activity":[]}""");
            var store=new LocalStore(false,f.Path); var state=store.Load();
            Check(store.LoadError is null,"legacy metadata rejected"); Check(state.TaskbarWidgetEnabled,"legacy widget default is off"); Equal("tray",state.TaskbarWidgetDock);
            Equal("旧备注",state.Accounts.Single().Note);
        })),
        ("Taskbar visibility and left preset round-trip with account metadata",()=>Run(()=>
        {
            using var f=new PreferenceFixture(); var original=Initial();
            var candidate=LocalAccountLibrary.SetTaskbarPreferences(original,false,"left");
            new LocalStore(false,f.Path).Save(candidate); var loaded=new LocalStore(false,f.Path).Load();
            Check(!loaded.TaskbarWidgetEnabled,"disabled preference lost"); Equal("left",loaded.TaskbarWidgetDock);
            Equal("原备注",loaded.Accounts.Single().Note); Check(loaded.Accounts.Single().IsFavorite,"favorite lost");
            Check(original.TaskbarWidgetEnabled,"candidate changed original preference"); Equal("tray",original.TaskbarWidgetDock);
            Check(!Directory.EnumerateFiles(f.Root,"*.tmp").Any(),"successful save leaked temporary data");
        })),
        ("Missing, null and invalid dock values normalize without dropping notes",()=>Run(()=>
        {
            using var f=new PreferenceFixture();
            foreach(var dock in new[]{"null","\"unknown-preset\"","\"\"","\"TRAY\""})
            {
                File.WriteAllText(f.Path,$$"""{"SteamPath":"","TaskbarWidgetEnabled":false,"TaskbarWidgetDock":{{dock}},"Accounts":[{"AccountName":"fixture_user","DisplayName":"昵称","Note":"保留"}],"HiddenAccounts":[],"Activity":[]}""");
                var store=new LocalStore(false,f.Path); var loaded=store.Load();
                Check(store.LoadError is null,"recoverable dock value rejected library"); Equal("tray",loaded.TaskbarWidgetDock); Check(!loaded.TaskbarWidgetEnabled,"disabled value reset during normalization"); Equal("保留",loaded.Accounts.Single().Note);
            }
            Equal("left",LocalAccountLibrary.NormalizeTaskbarWidgetDock(" LEFT "));
        })),
        ("Account and folder edits preserve taskbar preferences",()=>Run(()=>
        {
            var source=LocalAccountLibrary.SetTaskbarPreferences(Initial(),false,"left");
            var folder=LocalAccountLibrary.CreateFolder(source,"常用",out var id);
            var renamed=LocalAccountLibrary.RenameFolder(folder,id,"主账号");
            var edited=LocalAccountLibrary.EditAccount(renamed,new SteamAccount { AccountName="fixture_user" },"新昵称","新备注",id);
            var deleted=LocalAccountLibrary.DeleteFolder(edited,id);
            foreach(var state in new[]{folder,renamed,edited,deleted}) { Check(!state.TaskbarWidgetEnabled,"account edit enabled the widget"); Equal("left",state.TaskbarWidgetDock); }
            Equal("原备注",source.Accounts.Single().Note); Equal("新备注",deleted.Accounts.Single().Note);
        })),
        ("Preference candidates and normalized saves leave source state untouched",()=>Run(()=>
        {
            using var f=new PreferenceFixture(); var source=Initial(); source.TaskbarWidgetDock="bad-preset";
            var candidate=LocalAccountLibrary.SetTaskbarPreferences(source,false,"left"); candidate.Accounts[0].Note="候选备注";
            Check(source.TaskbarWidgetEnabled,"candidate toggled live state"); Equal("bad-preset",source.TaskbarWidgetDock); Equal("原备注",source.Accounts[0].Note);
            new LocalStore(false,f.Path).Save(source);
            Equal("tray",new LocalStore(false,f.Path).Load().TaskbarWidgetDock); Equal("bad-preset",source.TaskbarWidgetDock);
        })),
        ("Read failures block preference saves and preserve the original file",()=>Run(()=>
        {
            using var f=new PreferenceFixture(); var original=Encoding.UTF8.GetBytes("{fixture-invalid-json}"); File.WriteAllBytes(f.Path,original);
            var store=new LocalStore(false,f.Path); var state=store.Load();
            var candidate=LocalAccountLibrary.SetTaskbarPreferences(state,false,"left"); ThrowsSaveFailure(()=>store.Save(candidate));
            Check(original.SequenceEqual(File.ReadAllBytes(f.Path)),"preference save replaced damaged data"); Check(state.TaskbarWidgetEnabled,"failed candidate changed live enable state"); Equal("tray",state.TaskbarWidgetDock);
            Check(!Directory.EnumerateFiles(f.Root,"*.tmp").Any(),"failed save leaked temporary data");
        })),
        ("Locked preference destination rejects the candidate atomically",()=>Run(()=>
        {
            using var f=new PreferenceFixture(); var state=LocalAccountLibrary.SetTaskbarPreferences(Initial(),false,"tray"); var store=new LocalStore(false,f.Path); store.Save(state);
            var original=File.ReadAllBytes(f.Path); var candidate=LocalAccountLibrary.SetTaskbarPreferences(state,true,"left");
            using(var locked=new FileStream(f.Path,FileMode.Open,FileAccess.Read,FileShare.None)) ThrowsSaveFailure(()=>store.Save(candidate));
            Check(original.SequenceEqual(File.ReadAllBytes(f.Path)),"locked destination was replaced"); Check(!state.TaskbarWidgetEnabled,"failed save changed live toggle"); Equal("tray",state.TaskbarWidgetDock);
            Check(!Directory.EnumerateFiles(f.Root,"*.tmp").Any(),"locked save leaked temporary data");
        })),
        ("Demo taskbar changes neither read nor write real preference files",()=>Run(()=>
        {
            using var f=new PreferenceFixture(); var real=new LocalStore(false,f.Path); real.Save(LocalAccountLibrary.SetTaskbarPreferences(Initial(),false,"left")); var original=File.ReadAllBytes(f.Path);
            var demo=new LocalStore(true,f.Path); var state=demo.Load(); Check(state.TaskbarWidgetEnabled,"demo read actual disabled setting"); Equal("tray",state.TaskbarWidgetDock);
            demo.Save(LocalAccountLibrary.SetTaskbarPreferences(state,false,"left")); Check(original.SequenceEqual(File.ReadAllBytes(f.Path)),"demo changed preferences");
            var fresh=System.IO.Path.Combine(f.Root,"demo-should-not-exist.json"); new LocalStore(true,fresh).Save(state); Check(!File.Exists(fresh),"demo created preference file");
        }))
    ];
    private static LocalState Initial()=>new() { Accounts=[new() { AccountName="fixture_user",DisplayName="原昵称",Note="原备注",IsFavorite=true,IsManual=true }] };
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
    private static void ThrowsSaveFailure(Action action)
    {
        try { action(); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected an atomic save failure");
    }
    private sealed class PreferenceFixture : IDisposable
    {
        public string Root { get; }=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Gameorder.TaskbarPrefs."+Guid.NewGuid().ToString("N"));
        public string Path=>System.IO.Path.Combine(Root,"accounts.json");
        public PreferenceFixture()=>Directory.CreateDirectory(Root);
        public void Dispose()
        {
            string resolved=System.IO.Path.GetFullPath(Root),temporaryRoot=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar;
            if(!resolved.StartsWith(temporaryRoot,StringComparison.OrdinalIgnoreCase)||!System.IO.Path.GetFileName(resolved).StartsWith("Gameorder.TaskbarPrefs.",StringComparison.Ordinal)) throw new InvalidOperationException("Refusing unsafe fixture cleanup");
            Directory.Delete(resolved,true);
        }
    }
}
