using System.Text;
using Gameorder;

// This executable only uses temporary fixtures and FakeSteamPlatform.
// It never opens the Windows registry and never starts or stops the real Steam client.
var tests = new List<(string Name, Func<Task> Run)>
{
    ("KeyValues comments, escapes and Unicode", () => Sync(() =>
    {
        var document = ValveKeyValues.Parse("// comment\n\"users\" { /* inline */ \"42\" { \"PersonaName\" \"浮序 \\\"玩家\\\"\" \"Path\" \"C:\\unknown\" } }");
        var entry = document.Find("users")!.Children[0];
        Equal("浮序 \"玩家\"", entry.GetValue("PersonaName"));
        Equal("C:\\unknown", entry.GetValue("Path"));
    })),
    ("KeyValues modifies only requested spans", () => Sync(() =>
    {
        var text = "\"users\"\r\n{\r\n\t\"42\"\r\n\t{\r\n\t\t\"AccountName\" \"alpha\" // keep this\r\n\t\t\"SecretDummy\" \"leave-me-exactly\"\r\n\t\t\"AutoLogin\" \"0\"\r\n\t}\r\n}\r\n";
        var document = ValveKeyValues.Parse(text);
        var entry = document.Find("users")!.Children[0];
        var changed = document.WithValues([new(entry, "AutoLogin", "1")]);
        Equal(text.Replace("\"AutoLogin\" \"0\"", "\"AutoLogin\" \"1\"", StringComparison.Ordinal), changed);
        var added = document.WithValues([new(entry, "WantsOfflineMode", "1")]);
        Equal("1", ValveKeyValues.Parse(added).Find("users")!.Children[0].GetValue("WantsOfflineMode"));
        Check(added.Contains("\"SecretDummy\" \"leave-me-exactly\"", StringComparison.Ordinal), "unrelated field lost");
    })),
    ("KeyValues malformed input rejected", () => Sync(() =>
    {
        Throws<FormatException>(() => ValveKeyValues.Parse("\"users\" { \"42\" { \"field\" \"broken"));
        Throws<FormatException>(() => ValveKeyValues.Parse("\"users\" {"));
    })),
    ("Import exposes public fields and cache eligibility", () => Sync(() =>
    {
        using var f = new Fixture();
        var accounts = f.Service.ReadAccounts(f.Root);
        Equal(2, accounts.Count);
        var alpha = accounts.Single(a => a.AccountName == "alpha");
        Check(alpha.HasCachedLogin, "cached alpha not detected");
        Check(!accounts.Single(a => a.AccountName == "beta").HasCachedLogin, "AllowAutoLogin=0 ignored");
        Equal("浮序 玩家", alpha.DisplayName);
        Check(alpha.LastLoginAt.HasValue, "timestamp missing");
        Check(alpha.IsCurrentSelection, "legacy selection missing");
    })),
    ("Import gives registry one selection and respects modern AutoLogin", () => Sync(() =>
    {
        using var f = new Fixture();
        File.WriteAllText(f.LoginFile, File.ReadAllText(f.LoginFile).Replace("\"MostRecent\" \"1\"", "\"AutoLogin\" \"0\"\n        \"MostRecent\" \"1\"", StringComparison.Ordinal));
        f.Platform.State = new("beta", 1);
        Equal(1, f.Service.ReadAccounts(f.Root).Count(a => a.IsCurrentSelection));
        Equal("beta", f.Service.ReadAccounts(f.Root).Single(a => a.IsCurrentSelection).AccountName);
        f.Platform.State = new(null, 1);
        Equal(0, f.Service.ReadAccounts(f.Root).Count(a => a.IsCurrentSelection));
    })),
    ("Online selection updates old and new fields", async () =>
    {
        using var f = new Fixture();
        var beta = f.Service.ReadAccounts(f.Root).Single(a => a.AccountName == "beta");
        await f.Service.SwitchAsync(f.Root, beta, false);
        var users = f.Users();
        Equal("0", users.Children[0].GetValue("AutoLogin"));
        Equal("0", users.Children[0].GetValue("MostRecent"));
        Equal("1", users.Children[1].GetValue("AutoLogin"));
        Equal("1", users.Children[1].GetValue("MostRecent"));
        Equal("0", users.Children[1].GetValue("WantsOfflineMode"));
        Equal("0", users.Children[1].GetValue("SkipOfflineModeWarning"));
        Equal("beta", f.Platform.State.AutoLoginUser);
        Equal(1, f.Platform.Launches);
        Check(File.ReadAllText(f.LoginFile).Contains("\"SecretDummy\"\t\"never-touch\" // preserved", StringComparison.Ordinal), "unrelated contents changed");
    }),
    ("Offline requires remembered login and sets flags", async () =>
    {
        using var f = new Fixture();
        var alpha = f.Service.ReadAccounts(f.Root).Single(a => a.AccountName == "alpha");
        await f.Service.SwitchAsync(f.Root, alpha, true);
        Equal("1", f.Users().Children[0].GetValue("WantsOfflineMode"));
        Equal("1", f.Users().Children[0].GetValue("SkipOfflineModeWarning"));
        Equal(1, f.Platform.State.RememberPassword);
    }),
    ("Uncached offline login rejected without writes", async () =>
    {
        using var f = new Fixture();
        var before = File.ReadAllBytes(f.LoginFile);
        var beta = f.Service.ReadAccounts(f.Root).Single(a => a.AccountName == "beta");
        beta.HasCachedLogin = true; // A stale UI flag must not bypass fresh config eligibility.
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, beta, true));
        BytesEqual(before, File.ReadAllBytes(f.LoginFile));
        Equal(0, f.Platform.Launches);
    }),
    ("Manual account online and offline restrictions", async () =>
    {
        using var f = new Fixture();
        var manual = new SteamAccount { AccountName = "manual_user" };
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, manual, true));
        await f.Service.SwitchAsync(f.Root, manual, false);
        Equal("manual_user", f.Platform.State.AutoLoginUser);
        Check(f.Users().Children.All(u => u.GetValue("AutoLogin") == "0"), "manual selection didn't clear old selection");
    }),
    ("Manual online works without existing loginusers.vdf", async () =>
    {
        using var f = new Fixture();
        File.Delete(f.LoginFile);
        await f.Service.SwitchAsync(f.Root, new SteamAccount { AccountName = "fresh_user" }, false);
        Check(!File.Exists(f.LoginFile), "manual login invented a VDF file");
        f.Service.RestoreLatestBackup(f.Root);
        Equal("alpha", f.Platform.State.AutoLoginUser);
    }),
    ("Invalid login name rejected before client operations", async () =>
    {
        using var f = new Fixture();
        foreach (var name in new[] { "alpha -offline", "..\\evil", "a", "测试账号", "alpha\nnext" })
            await ThrowsAsync<ArgumentException>(() => f.Service.SwitchAsync(f.Root, new SteamAccount { AccountName = name }, false));
        Equal(0, f.Platform.Launches);
        Equal(0, f.Platform.ShutdownRequests);
    }),
    ("Steam exits gracefully before configuration writes", async () =>
    {
        using var f = new Fixture();
        f.Platform.Running = true;
        f.Platform.ExitWhenRequested = true;
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        Equal(1, f.Platform.ShutdownRequests);
        Equal(1, f.Platform.Launches);
    }),
    ("Client shutdown timeout never writes", async () =>
    {
        using var f = new Fixture();
        f.Platform.Running = true;
        f.Platform.ExitWhenRequested = false;
        var original = File.ReadAllBytes(f.LoginFile);
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, f.Alpha(), true));
        BytesEqual(original, File.ReadAllBytes(f.LoginFile));
        Equal(new SteamRegistryState("alpha", 1), f.Platform.State);
        Equal(0, f.Platform.RegistryWrites);
        Equal(0, f.Platform.Launches);
    }),
    ("Launch failure rolls file and registry back", async () =>
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LoginFile);
        f.Platform.FailLaunch = true;
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, f.Alpha(), true));
        BytesEqual(original, File.ReadAllBytes(f.LoginFile));
        Equal(new SteamRegistryState("alpha", 1), f.Platform.State);
        Check(!f.Service.CanRestoreBackup(f.Root), "failed operation left misleading backup");
    }),
    ("Registry partial failure rolls file and settings back", async () =>
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LoginFile);
        f.Platform.FailNextWrite = true;
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, f.Alpha(), true));
        BytesEqual(original, File.ReadAllBytes(f.LoginFile));
        Equal(new SteamRegistryState("alpha", 1), f.Platform.State);
        Equal(0, f.Platform.Launches);
    }),
    ("Steam restarting on failure preserves backup without writes", async () =>
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LoginFile);
        f.Platform.OnLaunch = () => { f.Platform.Running = true; throw new IOException("fixture launch returned error after process start"); };
        await ThrowsAsync<IOException>(() => f.Service.SwitchAsync(f.Root, f.Alpha(), true));
        Check(!original.SequenceEqual(File.ReadAllBytes(f.LoginFile)), "running client's file was rolled back");
        Check(f.Service.CanRestoreBackup(f.Root), "recovery backup was lost");
        Equal(1, f.Platform.RegistryWrites);
        f.Platform.Running = false;
        f.Service.RestoreLatestBackup(f.Root);
        BytesEqual(original, File.ReadAllBytes(f.LoginFile));
    }),
    ("Concurrent registry update is preserved during rollback", async () =>
    {
        using var f = new Fixture();
        var account = f.Alpha();
        f.Platform.OnLaunch = () => { f.Platform.State = new("external_user", 0); throw new IOException("fixture failure with external registry write"); };
        await ThrowsAsync<IOException>(() => f.Service.SwitchAsync(f.Root, account, true));
        Equal(new SteamRegistryState("external_user", 0), f.Platform.State);
        Check(f.Service.CanRestoreBackup(f.Root), "conflict backup was lost");
    }),
    ("DPAPI backup restores exact original bytes", async () =>
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LoginFile);
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        Check(f.Service.CanRestoreBackup(f.Root), "backup missing");
        var backup = Directory.GetFiles(f.Backups, "*.gobk", SearchOption.AllDirectories).Single();
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains("never-touch", StringComparison.Ordinal), "backup not encrypted");
        f.Service.RestoreLatestBackup(f.Root);
        BytesEqual(original, File.ReadAllBytes(f.LoginFile));
        Check(!f.Service.CanRestoreBackup(f.Root), "consumed backup still offered");
    }),
    ("Restore refuses running client and owned field changes", async () =>
    {
        using var f = new Fixture();
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        f.Platform.Running = true;
        Throws<InvalidOperationException>(() => f.Service.RestoreLatestBackup(f.Root));
        f.Platform.Running = false;
        var changed = File.ReadAllText(f.LoginFile).Replace("\"WantsOfflineMode\"\t\t\"1\"", "\"WantsOfflineMode\"\t\t\"0\"", StringComparison.Ordinal);
        File.WriteAllText(f.LoginFile, changed);
        var external = File.ReadAllBytes(f.LoginFile);
        Throws<IOException>(() => f.Service.RestoreLatestBackup(f.Root));
        BytesEqual(external, File.ReadAllBytes(f.LoginFile));
    }),
    ("Restore preserves Steam's newer unrelated metadata", async () =>
    {
        using var f = new Fixture();
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        File.WriteAllText(f.LoginFile, File.ReadAllText(f.LoginFile).Replace("1700000000", "1900000000", StringComparison.Ordinal) + "\n// new Steam metadata");
        f.Service.RestoreLatestBackup(f.Root);
        Equal("1900000000", f.Users().Children[0].GetValue("Timestamp"));
        Check(File.ReadAllText(f.LoginFile).Contains("// new Steam metadata", StringComparison.Ordinal), "unrelated metadata lost");
        Check(f.Users().Children[0].GetValue("WantsOfflineMode") is null, "new owned field not removed");
        Equal("1", f.Users().Children[0].GetValue("MostRecent"));
    }),
    ("Concurrent configuration write is never overwritten", async () =>
    {
        using var f = new Fixture();
        var account = f.Alpha();
        f.Platform.OnNextRegistryRead = () => File.AppendAllText(f.LoginFile, "\n// external change before commit");
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, account, true));
        Check(File.ReadAllText(f.LoginFile).Contains("external change before commit", StringComparison.Ordinal), "concurrent change overwritten");
        Equal(0, f.Platform.Launches);
        Equal(0, f.Platform.RegistryWrites);
    }),
    ("UTF8 BOM remains intact after switch", async () =>
    {
        using var f = new Fixture();
        File.WriteAllText(f.LoginFile, Fixture.Config, new UTF8Encoding(true));
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        Check(File.ReadAllBytes(f.LoginFile).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM lost");
    }),
    ("UTF16 encoding remains intact after switch", async () =>
    {
        using var f = new Fixture();
        File.WriteAllText(f.LoginFile, Fixture.Config, new UnicodeEncoding(false, true));
        await f.Service.SwitchAsync(f.Root, f.Alpha(), true);
        Check(File.ReadAllBytes(f.LoginFile).AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }), "UTF16 BOM lost");
        Equal("浮序 玩家", f.Service.ReadAccounts(f.Root).Single(a => a.AccountName == "alpha").DisplayName);
    }),
    ("Stale Steam ID cannot select a different account", async () =>
    {
        using var f = new Fixture();
        var account = f.Alpha();
        account.SteamId = "76561198000000002";
        await ThrowsAsync<InvalidOperationException>(() => f.Service.SwitchAsync(f.Root, account, false));
        Equal(0, f.Platform.Launches);
    }),
    ("LocalStore demo never creates or overwrites a data file", () => Sync(() =>
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "demo-data.json");
        var demo = new LocalStore(true, path);
        Equal(0, demo.Load().Accounts.Count);
        demo.Save(new LocalState { SteamPath = f.Root, Accounts = [new() { AccountName = "alpha", DisplayName = "演示玩家" }] });
        Check(!File.Exists(path), "demo created local user data");
        var canary = Encoding.UTF8.GetBytes("existing file must remain untouched — 现有资料");
        File.WriteAllBytes(path, canary);
        Equal(0, demo.Load().Accounts.Count);
        Check(demo.LoadError is null, "demo read an unrelated existing file");
        demo.Save(new LocalState());
        BytesEqual(canary, File.ReadAllBytes(path));
    })),
    ("LocalStore preserves Chinese notes, favorites and manual accounts", () => Sync(() =>
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "account-data.json");
        var state = new LocalState
        {
            SteamPath = f.Root,
            Accounts = [new() { AccountName = "alpha", DisplayName = "浮序 玩家", Note = "周末游戏账号 · 中文备注 🎮", IsFavorite = true, IsManual = true }],
            HiddenAccounts = ["hidden_user"],
            Activity = [new() { Title = "资料已保存", Description = "中文活动记录", Kind = "info", Time = DateTimeOffset.FromUnixTimeSeconds(1700000000) }]
        };
        new LocalStore(false, path).Save(state);
        var store = new LocalStore(false, path);
        var loaded = store.Load();
        Check(store.LoadError is null, "valid saved data reported a load error");
        Equal(f.Root, loaded.SteamPath);
        Equal("浮序 玩家", loaded.Accounts.Single().DisplayName);
        Equal("周末游戏账号 · 中文备注 🎮", loaded.Accounts.Single().Note);
        Check(loaded.Accounts.Single().IsFavorite && loaded.Accounts.Single().IsManual, "account flags were lost");
        Equal("hidden_user", loaded.HiddenAccounts.Single());
        Equal("资料已保存", loaded.Activity.Single().Title);
        Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), loaded.Activity.Single().Time);
        Check(!Directory.EnumerateFiles(f.Root, "account-data.json.*.tmp").Any(), "save leaked a temporary file");
    })),
    ("LocalStore corrupted JSON cannot be overwritten after a load error", () => Sync(() =>
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "damaged-data.json");
        var damaged = Encoding.UTF8.GetBytes("{\"Accounts\": [ broken — 保留原文");
        File.WriteAllBytes(path, damaged);
        var store = new LocalStore(false, path);
        Equal(0, store.Load().Accounts.Count);
        Check(!string.IsNullOrWhiteSpace(store.LoadError), "damaged JSON had no actionable load error");
        Throws<IOException>(() => store.Save(new LocalState()));
        BytesEqual(damaged, File.ReadAllBytes(path));
    })),
    ("LocalStore valid JSON with null activity is safely preserved", () => Sync(() =>
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "invalid-fields.json");
        var original = Encoding.UTF8.GetBytes("{\"SteamPath\":\"\",\"Accounts\":[],\"HiddenAccounts\":[],\"Activity\":[null]}");
        File.WriteAllBytes(path, original);
        var store = new LocalStore(false, path);
        Equal(0, store.Load().Activity.Count);
        Check(!string.IsNullOrWhiteSpace(store.LoadError), "invalid activity entry had no load error");
        Throws<IOException>(() => store.Save(new LocalState { Accounts = [new() { AccountName = "replacement" }] }));
        BytesEqual(original, File.ReadAllBytes(path));
    })),
    ("Backup retention keeps at most ten completed switches", async () =>
    {
        using var f = new Fixture();
        var account = f.Alpha();
        for (var i = 0; i < 12; i++) await f.Service.SwitchAsync(f.Root, account, i % 2 == 0);
        Equal(10, Directory.GetFiles(f.Backups, "*.gobk", SearchOption.AllDirectories).Length);
    })
};

tests.AddRange(AvatarServiceTests.Tests);
tests.AddRange(SteamSessionTests.Tests);
tests.AddRange(AccountGroupsTests.Tests);
tests.AddRange(InstanceServiceTests.Tests);
tests.AddRange(TaskbarPreferencesTests.Tests);
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} isolated tests passed. No real Steam client or registry was used.");
return failures == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}', got '{actual}'."); }
static void BytesEqual(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "file bytes differ");
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }

sealed class FakeSteamPlatform : ISteamPlatform
{
    public bool Running;
    public bool ExitWhenRequested;
    public bool FailLaunch;
    public bool FailNextWrite;
    public int RegistryWrites;
    public int Launches;
    public int ShutdownRequests;
    public Action? OnNextRegistryRead;
    public Action? OnLaunch;
    public SteamRegistryState State = new("alpha", 1);
    public string? GetSteamPath() => null;
    public bool IsSteamRunning => Running;
    public SteamRegistryState ReadLoginSettings() { var callback = OnNextRegistryRead; OnNextRegistryRead = null; callback?.Invoke(); return State; }
    public void WriteLoginSettings(SteamRegistryState state)
    {
        RegistryWrites++;
        State = state;
        if (FailNextWrite) { FailNextWrite = false; throw new IOException("fixture registry write failure"); }
    }
    public Task RequestShutdownAsync(string steamExe, CancellationToken cancellationToken) { ShutdownRequests++; if (ExitWhenRequested) Running = false; return Task.CompletedTask; }
    public void Launch(string steamExe) { OnLaunch?.Invoke(); if (FailLaunch) throw new IOException("fixture launch failure"); Launches++; }
}

sealed class Fixture : IDisposable
{
    public static readonly string Config = """
        // fixture only — no real credentials
        "users"
        {
            "76561198000000001"
            {
                "AccountName" "alpha"
                "PersonaName" "浮序 玩家"
                "RememberPassword" "1"
                "AllowAutoLogin" "1"
                "MostRecent" "1"
                "Timestamp" "1700000000"
                "SecretDummy"\t"never-touch" // preserved
            }
            "76561198000000002"
            {
                "AccountName" "beta"
                "PersonaName" "Beta"
                "RememberPassword" "1"
                "AllowAutoLogin" "0"
                "AutoLogin" "0"
                "WantsOfflineMode" "1"
                "SkipOfflineModeWarning" "1"
                "Timestamp" "1690000000"
            }
        }
        """.Replace("\\t", "\t", StringComparison.Ordinal);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FloatOrder-isolated-tests-" + Guid.NewGuid().ToString("N"));
    public string LoginFile => Path.Combine(Root, "config", "loginusers.vdf");
    public string Backups => Path.Combine(Root, "test-backups");
    public FakeSteamPlatform Platform { get; } = new();
    public SteamService Service { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LoginFile)!);
        File.WriteAllBytes(Path.Combine(Root, "steam.exe"), []);
        File.WriteAllText(LoginFile, Config.Replace("\\t", "\t", StringComparison.Ordinal), new UTF8Encoding(false));
        Service = new SteamService(Platform, Backups, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5));
    }
    public SteamAccount Alpha() => Service.ReadAccounts(Root).Single(a => a.AccountName == "alpha");
    public KeyValuesEntry Users() => ValveKeyValues.Parse(File.ReadAllText(LoginFile)).Find("users")!;
    public void Dispose()
    {
        var root = Path.GetFullPath(Root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("FloatOrder-isolated-tests-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup target.");
        Directory.Delete(root, true);
    }
}
