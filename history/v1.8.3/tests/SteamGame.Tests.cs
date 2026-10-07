using System.Text;
using Gameorder;

internal static class SteamGameTests
{
    private const uint User = 123456;
    private const string SteamId = "76561197960389184";
    private static readonly DateTimeOffset Start = new(new DateTime(2030, 1, 2, 10, 0, 0));
    private static SteamSessionSnapshot Session(SteamSessionState state = SteamSessionState.Online) => new(true, SteamId, state, "fixture");
    private static SteamProcessEvidence Process(uint user = User) => new(true, true, user, Start);
    private static SteamGameEvidence Game(uint id = 730) => new(id, true, "fixture game tracking");
    public static IEnumerable<(string Name, Func<Task> Run)> Tests =>
    [
        Case("Game running belongs only to verified local active account", () =>
        {
            var result = Read(new FakePlatform { Name = "Counter-Strike 2" });
            Assert(result.IsRunning && result.IsKnown && result.SteamId == SteamId && result.AppId == 730 && result.GameName == "Counter-Strike 2");
        }),
        Case("Offline and unclear connection states can still confirm a local game", () =>
        {
            foreach (var state in new[] { SteamSessionState.Offline, SteamSessionState.Unknown })
                Assert(Read(new FakePlatform(), Session(state)).IsRunning);
        }),
        Case("Stopped Steam never reads stale game evidence", () =>
        {
            var fake = new FakePlatform();
            var result = Read(fake, new(false, null, SteamSessionState.Stopped, "fixture"));
            Assert(!result.IsRunning && result.IsKnown && fake.ProcessReads == 0 && fake.GameReads == 0);
        }),
        Case("Unclear client read remains unknown rather than confirmed stopped", () =>
        {
            var result = Read(new FakePlatform(), new(false, null, SteamSessionState.Unknown, "read failure"));
            Assert(!result.IsRunning && !result.IsKnown);
        }),
        Case("Unconfirmed active account never receives a running game", () =>
        {
            var fake = new FakePlatform();
            Assert(!Read(fake, new(true, null, SteamSessionState.Unknown, "fixture")).IsRunning && fake.GameReads == 0);
        }),
        Case("Other account game cannot be assigned to selected account", () =>
        {
            var fake = new FakePlatform { Evidence = Process(User + 1) };
            Assert(!Read(fake).IsRunning && fake.GameReads == 0);
        }),
        Case("Offline runtime identity can confirm a game when registry ActiveUser is zero", () =>
        {
            var fake=new FakePlatform{Evidence=new(true,true,0,Start,new(User,false))};
            Assert(Read(fake,Session(SteamSessionState.Offline)).IsRunning);
        }),
        Case("Conflicting or changing runtime identity cannot attach a game", () =>
        {
            foreach(var evidence in new[]{new SteamProcessEvidence(true,true,User+1,Start,new(User,false)),new(true,true,User,Start,new(0,false,false))})
            {
                var fake=new FakePlatform{Evidence=evidence};
                Assert(!Read(fake).IsRunning&&fake.GameReads==0);
            }
        }),
        Case("Offline runtime account switch still fences previous Steam game launches", () =>
        {
            var now=Start.AddMinutes(5);
            var cache=new SteamGameTrackingCache(()=>now);
            cache.ObserveIdentity(new(true,true,0,Start,new(User,false)));
            cache.PrepareRead(@"C:\FakeSteam",Start);
            Assert(cache.Collect(Added(2,730,88),Start,_=>true).Count==1);
            cache.ObserveIdentity(new(true,true,0,Start,new(User+1,false)));
            cache.PrepareRead(@"C:\FakeSteam",Start);
            Assert(cache.Collect(Added(2,730,88),Start,_=>true).Count==0);
        }),
        Case("Missing ActiveUser and unrelated Steam PID are unconfirmed", () =>
        {
            foreach (var evidence in new[] { Process(0), new SteamProcessEvidence(true, false, User, Start), new(true, true, User, null) })
                Assert(!Read(new FakePlatform { Evidence = evidence }).IsRunning);
        }),
        Case("Invalid Steam IDs cannot attach gaming state", () =>
        {
            foreach (var id in new[] { "", "1", "not-an-id", "76561197960265728", "18446744073709551615", "+76561197960389184" })
            {
                var fake = new FakePlatform();
                Assert(!Read(fake, new(true, id, SteamSessionState.Online, "fixture")).IsRunning && fake.GameReads == 0);
            }
        }),
        Case("Account switch during manifest read clears running game", () =>
        {
            var fake = new FakePlatform { NextProcess = Process(User + 1) };
            Assert(!Read(fake).IsRunning);
        }),
        Case("Steam restart during game read clears running game", () =>
        {
            var fake = new FakePlatform { NextProcess = new(true, true, User, Start.AddMinutes(1)) };
            Assert(!Read(fake).IsRunning);
        }),
        Case("Game exit or change during title read is not published", () =>
        {
            foreach (var next in new[] { new SteamGameEvidence(0, true, "exit"), Game(570), new(730, false, "unknown") })
                Assert(!Read(new FakePlatform { NextGame = next }).IsRunning);
        }),
        Case("Missing title retains app identity with a clear fallback", () =>
        {
            var result = Read(new FakePlatform { Name = null });
            Assert(result.IsRunning && result.GameName == "Steam 游戏 · 730");
            result = Read(new FakePlatform { GameEvidence = Game(0x80000001), Name = null });
            Assert(result.IsRunning && result.GameName == "Steam 正在游戏");
        }),
        Case("No running evidence never substitutes remembered title", () =>
        {
            var fake = new FakePlatform { GameEvidence = new(0, true, "not running"), Name = "stale title" };
            var result = Read(fake);
            Assert(!result.IsRunning && result.IsKnown && result.GameName == "" && fake.TitleReads == 0);
        }),
        Case("Unknown game observation does not become running", () =>
        {
            var result = Read(new FakePlatform { GameEvidence = new(730, false, "unconfirmed") });
            Assert(!result.IsRunning && !result.IsKnown);
        }),
        Case("Game title controls are removed and length is bounded", () =>
        {
            Assert(SteamGameService.CleanName("  Game\n\t\0  ") == "Game");
            Assert(SteamGameService.CleanName("\0\n\t") is null);
            Assert(SteamGameService.CleanName(new string('A', 300))?.Length == 160);
        }),
        Case("Read failure produces unknown status without throwing", () =>
        {
            var result = Read(new FakePlatform { ThrowOnGame = true });
            Assert(!result.IsRunning && !result.IsKnown);
        }),
        Case("Steam tracking parses quoted executable paths and exit records", () =>
        {
            var log = Line(2, "AppID 730 adding PID 88 as a tracked process \"\"C:\\Fake Steam\\game.exe\" -steam\"")
                + Line(3, "AppID 570 adding PID 99 as a tracked process \"C:\\Fake Steam\\dota.exe\"")
                + Line(4, "AppID 570 no longer tracking PID 99, exit code 0");
            var tracked = SteamGameLog.FindTrackedProcesses(log, Start, Start.AddMinutes(5));
            Assert(tracked.Count == 1 && tracked[0].AppId == 730 && tracked[0].Pid == 88 && tracked[0].Executable == @"C:\Fake Steam\game.exe");
        }),
        Case("Steam remove-running-list event clears all game processes", () =>
        {
            var log = Added(2, 730, 88) + Added(3, 730, 89) + Line(4, "Remove 730 from running list");
            Assert(SteamGameLog.FindTrackedProcesses(log, Start, Start.AddMinutes(5)).Count == 0);
        }),
        Case("Previous client and future game tracking are ignored", () =>
        {
            var log = Added(-10, 730, 88) + Added(500, 730, 89);
            Assert(SteamGameLog.FindTrackedProcesses(log, Start, Start.AddMinutes(5)).Count == 0);
        }),
        Case("Verified launch survives bounded tail rotation and later exit removes it", () =>
        {
            var previous = SteamGameLog.FindTrackedProcesses(Added(2, 730, 88), Start, Start.AddMinutes(5));
            var retained = SteamGameLog.FindTrackedProcesses(Line(200, "SSGL: persona state flags"), Start, Start.AddMinutes(5), previous);
            Assert(retained.Count == 1 && retained[0].Pid == 88);
            var exited = SteamGameLog.FindTrackedProcesses(Line(220, "Remove 730 from running list"), Start, Start.AddMinutes(5), retained);
            Assert(exited.Count == 0);
            Assert(SteamGameLog.FindTrackedProcesses("", Start.AddMinutes(10), Start.AddMinutes(20), previous).Count == 0);
        }),
        Case("Cold start bootstraps launch outside normal tail then retains verified PID", () =>
        {
            using var f = new FilesFixture();
            var path = Path.Combine(f.Root, "gameprocess_log.txt");
            File.WriteAllText(path, Added(2, 730, 88) + new string('x', 400 * 1024) + "\n" + Line(200, "SSGL: persona state flags"), new UTF8Encoding(false));
            var normalTail = SteamGameFiles.ReadTail(path, SteamGameTrackingCache.TailBytes);
            Assert(SteamGameLog.FindTrackedProcesses(normalTail, Start, Start.AddMinutes(5)).Count == 0);
            var cache = new SteamGameTrackingCache(() => Start.AddMinutes(5));
            cache.ObserveIdentity(Process());
            var bootstrap = cache.PrepareRead(f.Root, Start);
            Assert(bootstrap == 8 * 1024 * 1024);
            var first = cache.Collect(SteamGameFiles.ReadTail(path, bootstrap), Start, _ => true);
            Assert(first.Count == 1 && first[0].AppId == 730);
            Assert(cache.PrepareRead(f.Root, Start) == 256 * 1024);
            Assert(cache.Collect(normalTail, Start, _ => true).Count == 1);
        }),
        Case("Observed account change fences older game launches in same Steam process", () =>
        {
            var now = Start.AddMinutes(5);
            var cache = new SteamGameTrackingCache(() => now);
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            Assert(cache.Collect(Added(2, 730, 88), Start, _ => true).Count == 1);
            cache.ObserveIdentity(Process(User + 1));
            Assert(cache.PrepareRead(@"C:\FakeSteam", Start) == SteamGameTrackingCache.BootstrapBytes);
            Assert(cache.Collect(Added(2, 730, 88), Start, _ => true).Count == 0);
            Assert(cache.Collect(Added(301, 570, 99), Start, _ => true).Single().AppId == 570);
        }),
        Case("Unclear process read cannot erase prior account identity fence", () =>
        {
            var cache = new SteamGameTrackingCache(() => Start.AddMinutes(5));
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            cache.Collect(Added(2, 730, 88), Start, _ => true);
            cache.ObserveIdentity(new(false, false, 0, null));
            cache.ObserveIdentity(Process(User + 1));
            cache.PrepareRead(@"C:\FakeSteam", Start);
            Assert(cache.Collect(Added(2, 730, 88), Start, _ => true).Count == 0);
        }),
        Case("Logout and return to earlier account cannot revive pre-switch game", () =>
        {
            var now = Start.AddMinutes(5);
            var cache = new SteamGameTrackingCache(() => now);
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            cache.Collect(Added(2, 730, 88), Start, _ => true);
            cache.ObserveIdentity(Process(0));
            now = now.AddSeconds(20);
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            Assert(cache.Collect(Added(2, 730, 88), Start, _ => true).Count == 0);
        }),
        Case("New Steam process resets observed account epoch and rejects previous client launches", () =>
        {
            var now = Start.AddMinutes(5);
            var cache = new SteamGameTrackingCache(() => now);
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            cache.ObserveIdentity(Process(User + 1));
            var restarted = Start.AddMinutes(10);
            now = restarted.AddMinutes(5);
            cache.ObserveIdentity(new(true, true, User + 1, restarted));
            cache.PrepareRead(@"C:\FakeSteam", restarted);
            var result = cache.Collect(Added(2, 730, 88) + Added(620, 570, 99), restarted, _ => true);
            Assert(result.Count == 1 && result[0].AppId == 570);
        }),
        Case("Unreadable bootstrap is retried once log becomes available", () =>
        {
            var cache = new SteamGameTrackingCache(() => Start.AddMinutes(5));
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            Assert(cache.Collect(null, Start, _ => true).Count == 0);
            Assert(cache.PrepareRead(@"C:\FakeSteam", Start) == SteamGameTrackingCache.BootstrapBytes);
            Assert(cache.Collect(Added(2, 730, 88), Start, _ => true).Count == 1);
            Assert(cache.PrepareRead(@"C:\FakeSteam", Start) == SteamGameTrackingCache.TailBytes);
        }),
        Case("Changed Steam path requests bounded bootstrap and clears old cached PID", () =>
        {
            var cache = new SteamGameTrackingCache(() => Start.AddMinutes(5));
            cache.ObserveIdentity(Process());
            cache.PrepareRead(@"C:\FakeSteam", Start);
            cache.Collect(Added(2, 730, 88), Start, _ => true);
            Assert(cache.PrepareRead(@"D:\OtherSteam", Start) == SteamGameTrackingCache.BootstrapBytes);
            Assert(cache.Collect("", Start, _ => true).Count == 0);
        }),
        Case("Malformed game IDs PIDs and relative paths are ignored", () =>
        {
            var log = Line(2, "AppID 0 adding PID 88 as a tracked process \"C:\\game.exe\"")
                + Line(3, "AppID 4294967296 adding PID 88 as a tracked process \"C:\\game.exe\"")
                + Line(4, "AppID 730 adding PID 0 as a tracked process \"C:\\game.exe\"")
                + Line(5, "AppID 730 adding PID 88 as a tracked process \"game.exe\"");
            Assert(SteamGameLog.FindTrackedProcesses(log, Start, Start.AddMinutes(5)).Count == 0);
        }),
        Case("Stale registry Running flags without live tracked PID do not claim a game", () =>
        {
            var tracked = SteamGameLog.FindTrackedProcesses(Added(2, 730, 88), Start, Start.AddMinutes(5));
            var result = SteamGameLog.Resolve(new(730, [730, 648800], true), true, tracked, _ => false);
            Assert(!result.IsKnown && result.AppId == 0);
        }),
        Case("Steam live tracked game takes precedence over a stale registry app", () =>
        {
            var tracked = SteamGameLog.FindTrackedProcesses(Added(2, 730, 88), Start, Start.AddMinutes(5));
            var result = SteamGameLog.Resolve(new(648800, [648800], true), true, tracked, _ => true);
            Assert(result.IsKnown && result.AppId == 730);
        }),
        Case("Positive RunningAppID chooses the corresponding live Steam game", () =>
        {
            var tracked = SteamGameLog.FindTrackedProcesses(Added(2, 730, 88) + Added(3, 570, 99), Start, Start.AddMinutes(5));
            Assert(SteamGameLog.Resolve(new(730, [730, 570], true), true, tracked, _ => true).AppId == 730);
        }),
        Case("No log and no registry remain unknown", () =>
        {
            Assert(!SteamGameLog.Resolve(new(null, [], false), false, [], _ => true).IsKnown);
            Assert(SteamGameLog.Resolve(new(0, [], true), true, [], _ => true).IsKnown);
        }),
        Case("Game title resolves an exact local app manifest", () =>
        {
            using var f = new FilesFixture();
            f.Manifest(f.Root, 730, "Counter-Strike 2");
            Assert(new SteamGameFiles().ReadGameName(f.Root, 730) == "Counter-Strike 2");
        }),
        Case("Game title resolves a second Steam library", () =>
        {
            using var f = new FilesFixture();
            f.Manifest(f.Library, 570, "Dota 2");
            File.WriteAllText(Path.Combine(f.Root, "steamapps", "libraryfolders.vdf"),
                "\"libraryfolders\" { \"0\" { \"path\" " + Quote(f.Root) + " } \"1\" { \"path\" " + Quote(f.Library) + " } }");
            Assert(new SteamGameFiles().ReadGameName(f.Root, 570) == "Dota 2");
        }),
        Case("Missing malformed mismatched manifests use no invented title", () =>
        {
            using var f = new FilesFixture();
            Assert(new SteamGameFiles().ReadGameName(f.Root, 730) is null);
            f.Manifest(f.Root, 730, "wrong", 570);
            Assert(new SteamGameFiles().ReadGameName(f.Root, 730) is null);
            File.WriteAllText(f.ManifestPath(f.Root, 730), "\"AppState\" { \"name\" \"broken\"");
            Assert(new SteamGameFiles().ReadGameName(f.Root, 730) is null);
        }),
        Case("Game title cache is bounded and refreshes after expiration", () =>
        {
            using var f = new FilesFixture();
            var now = Start;
            var files = new SteamGameFiles(() => now);
            f.Manifest(f.Root, 730, "first");
            Assert(files.ReadGameName(f.Root, 730) == "first");
            f.Manifest(f.Root, 730, "second");
            Assert(files.ReadGameName(f.Root, 730) == "first");
            now = now.AddMinutes(6);
            Assert(files.ReadGameName(f.Root, 730) == "second");
            Assert(files.ReadGameName(f.Root, 570) is null);
            f.Manifest(f.Root, 570, "Dota 2");
            now = now.AddSeconds(31);
            Assert(files.ReadGameName(f.Root, 570) == "Dota 2");
        }),
        Case("Oversized game files are ignored and log tail skips a cut line", () =>
        {
            using var f = new FilesFixture();
            var file = Path.Combine(f.Root, "large.txt");
            File.WriteAllText(file, new string('x', 100) + "\nfinal\n", new UTF8Encoding(false));
            Assert(SteamGameFiles.ReadBounded(file, 20) is null);
            Assert(SteamGameFiles.ReadTail(file, 20) == "final\n");
        })
    ];
    private static SteamGameSnapshot Read(FakePlatform platform, SteamSessionSnapshot? session = null) => new SteamGameService(platform).Read(@"C:\FakeSteam", session ?? Session());
    private static string Line(int seconds, string body) => $"[{Start.AddSeconds(seconds):yyyy-MM-dd HH:mm:ss}] {body}\n";
    private static string Added(int seconds, uint app, int pid) => Line(seconds, $"AppID {app} adding PID {pid} as a tracked process \"C:\\game.exe\"");
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static (string, Func<Task>) Case(string name, Action run) => (name, () => { run(); return Task.CompletedTask; });
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Steam game evidence assertion failed"); }
    private sealed class FakePlatform : ISteamGamePlatform
    {
        public SteamProcessEvidence Evidence { get; init; } = Process();
        public SteamProcessEvidence? NextProcess { get; init; }
        public SteamGameEvidence GameEvidence { get; init; } = Game();
        public SteamGameEvidence? NextGame { get; init; }
        public string? Name { get; init; } = "fixture game";
        public bool ThrowOnGame { get; init; }
        public int ProcessReads, GameReads, TitleReads;
        public SteamProcessEvidence ReadProcess(string steamPath) => ProcessReads++ == 0 ? Evidence : NextProcess ?? Evidence;
        public SteamGameEvidence ReadGame(string steamPath, DateTimeOffset clientStartedAt)
        {
            if (ThrowOnGame) throw new IOException("fixture access failure");
            return GameReads++ == 0 ? GameEvidence : NextGame ?? GameEvidence;
        }
        public string? ReadGameName(string steamPath, uint appId) { TitleReads++; return Name; }
    }
    private sealed class FilesFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Gameorder-game-tests-" + Guid.NewGuid().ToString("N"));
        public string Library => Path.Combine(Root, "second-library");
        public FilesFixture() { Directory.CreateDirectory(Path.Combine(Root, "steamapps")); Directory.CreateDirectory(Path.Combine(Library, "steamapps")); }
        public string ManifestPath(string library, uint app) => Path.Combine(library, "steamapps", $"appmanifest_{app}.acf");
        public void Manifest(string library, uint app, string name, uint? declaredApp = null) =>
            File.WriteAllText(ManifestPath(library, app), "\"AppState\" { \"appid\" \"" + (declaredApp ?? app) + "\" \"name\" " + Quote(name) + " }");
        public void Dispose()
        {
            var target = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("Gameorder-game-tests-", StringComparison.Ordinal))
                throw new IOException("Unsafe fixture cleanup target.");
            Directory.Delete(target, true);
        }
    }
}