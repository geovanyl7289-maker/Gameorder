using Gameorder;

internal static class SteamSessionTests
{
    private const uint User = 123456;
    private const string SteamId = "76561197960389184";
    private static readonly DateTimeOffset Start = new(new DateTime(2030, 1, 2, 10, 0, 0));
    public static IEnumerable<(string Name, Func<Task> Run)> Tests =>
    [
        Case("Stopped Steam ignores stale ActiveUser and logged-on log", () =>
        {
            var result=Read(new(false,true,User,Start),Line(5,"Logged On",User));
            Assert(result.State==SteamSessionState.Stopped&&!result.IsOnline&&result.ActiveSteamId is null);
        }),
        Case("Running unrelated PID cannot identify a current user", () =>
        {
            var result=Read(new(true,false,User,Start),Line(5,"Logged On",User));
            Assert(result.State==SteamSessionState.Unknown&&result.ActiveSteamId is null&&!result.IsOnline);
        }),
        Case("ActiveUser zero means login pending, not last selected account", () =>
        {
            var result=Read(new(true,true,0,Start),Line(5,"Logged On",User));
            Assert(result.State==SteamSessionState.AwaitingLogin&&result.ActiveSteamId is null&&!result.IsOnline);
        }),
        Case("Current process plus matching Logged On confirms online", () =>
        {
            var result=Read(new(true,true,User,Start),Line(5,"Logged On",User));
            Assert(result.ActiveSteamId==SteamId&&result.IsOnline&&!result.IsOffline);
        }),
        Case("Previous process log does not confirm this process online", () =>
        {
            var result=Read(new(true,true,User,Start),Line(-30,"Logged On",User));
            Assert(result.State==SteamSessionState.Unknown&&result.ActiveSteamId==SteamId&&!result.IsOnline);
        }),
        Case("Latest disconnect overrides an earlier logged-on prefix", () =>
        {
            var result=Read(new(true,true,User,Start),Line(5,"Logged On",User)+Line(6,"Logged On",User,"Disconnected('remote closed')"));
            Assert(result.IsOffline&&!result.IsOnline);
        }),
        Case("Logoff payload immediately invalidates online state", () =>
        {
            var result=Read(new(true,true,User,Start),Line(5,"Logged On",User)+Line(6,"Logged On",User,"Initiating LogOff"));
            Assert(result.State==SteamSessionState.AwaitingLogin&&!result.IsOnline&&!result.IsOffline);
        }),
        Case("Offline log proves no live connection", () =>
        {
            var result=Read(new(true,true,User,Start),Line(5,"Offline",User));
            Assert(result.IsOffline&&result.Hint.Contains("网络中断",StringComparison.Ordinal));
        }),
        Case("Connecting and Logging On are not successful login", () =>
        {
            foreach(var state in new[]{"Connecting","Connected","Logging On"})
            {
                var result=Read(new(true,true,User,Start),Line(5,state,User));
                Assert(result.State==SteamSessionState.AwaitingLogin&&!result.IsOnline);
            }
        }),
        Case("Missing or unclear connection log remains unconfirmed", () =>
        {
            foreach(var log in new string?[]{null,"",Line(5,"Unexpected new client state",User)})
            {
                var result=Read(new(true,true,User,Start),log);
                Assert(result.State==SteamSessionState.Unknown&&!result.IsOnline);
            }
        }),
        Case("Latest other-user log cannot revive old account online", () =>
        {
            var result=Read(new(true,true,User,Start),Line(5,"Logged On",User)+Line(6,"Logging On",User+1));
            Assert(result.State==SteamSessionState.Unknown&&!result.IsOnline);
        }),
        Case("Process or user changes during read clears active identity", () =>
        {
            var fake=new FakePlatform(new(true,true,User,Start),Line(5,"Logged On",User))
                { NextEvidence=new(true,true,User+1,Start) };
            var result=new SteamSessionService(fake,()=>Start.AddMinutes(5)).Read("fixture");
            Assert(result.State==SteamSessionState.AwaitingLogin&&result.ActiveSteamId is null&&!result.IsOnline);
        }),
        Case("Future and malformed timestamps do not assert online", () =>
        {
            foreach(var log in new[]{Line(500,"Logged On",User),"[bad time] [Logged On, 4, 2] [U:1:123456]"})
            {
                var result=Read(new(true,true,User,Start),log);
                Assert(!result.IsOnline);
            }
        }),
        Case("Offline cached runtime login confirms identity with ActiveUser zero and no new connection log", () =>
        {
            var fake=new FakePlatform(new(true,true,0,Start,new(User,false)),null);
            var result=new SteamSessionService(fake,()=>Start.AddMinutes(5)).Read("fixture");
            Assert(result.IsOffline&&result.ActiveSteamId==SteamId&&!result.IsOnline&&fake.LogReads==0);
            Assert(result.Hint.Contains("网络断开",StringComparison.Ordinal));
        }),
        Case("Online runtime connection overrides a retained offline log or preference", () =>
        {
            var fake=new FakePlatform(new(true,true,User,Start,new(User,true)),Line(5,"Offline",User));
            var result=new SteamSessionService(fake,()=>Start.AddMinutes(5)).Read("fixture");
            Assert(result.IsOnline&&!result.IsOffline&&result.ActiveSteamId==SteamId&&fake.LogReads==0);
        }),
        Case("Current disconnected runtime user overrides an older Logged On log", () =>
        {
            var result=Read(new(true,true,User,Start,new(User,false)),Line(5,"Logged On",User));
            Assert(result.IsOffline&&!result.IsOnline);
        }),
        Case("Conflicting registry and runtime users cannot identify either account", () =>
        {
            var evidence=new SteamProcessEvidence(true,true,User+1,Start,new(User,false));
            var result=Read(evidence,Line(5,"Logged On",User+1));
            Assert(evidence.ConfirmedAccountId==0&&result.ActiveSteamId is null&&!result.IsOffline&&!result.IsOnline);
        }),
        Case("Runtime identity changing within read cannot reuse a retained ActiveUser", () =>
        {
            var result=Read(new(true,true,User,Start,new(0,false,false)),Line(5,"Logged On",User));
            Assert(result.ActiveSteamId is null&&!result.IsOnline&&!result.IsOffline);
        }),
        Case("Runtime account changes between process reads clear offline result", () =>
        {
            var fake=new FakePlatform(new(true,true,0,Start,new(User,false)),null)
                {NextEvidence=new(true,true,0,Start,new(User+1,false))};
            var result=new SteamSessionService(fake,()=>Start.AddMinutes(5)).Read("fixture");
            Assert(result.ActiveSteamId is null&&!result.IsOffline&&!result.IsOnline);
        }),
        Case("Runtime connection changes between reads do not publish a stale connection state", () =>
        {
            var fake=new FakePlatform(new(true,true,User,Start,new(User,true)),null)
                {NextEvidence=new(true,true,User,Start,new(User,false))};
            var result=new SteamSessionService(fake,()=>Start.AddMinutes(5)).Read("fixture");
            Assert(result.ActiveSteamId is null&&!result.IsOnline&&!result.IsOffline);
        }),
        Case("Runtime local user cannot bypass unmatched Steam process or missing start time", () =>
        {
            foreach(var evidence in new[]{new SteamProcessEvidence(true,false,0,Start,new(User,false)),new(true,true,0,null,new(User,false))})
            {
                var result=Read(evidence,null);
                Assert(evidence.ConfirmedAccountId==0&&result.ActiveSteamId is null&&!result.IsOffline);
            }
        }),
        Case("Second precision log can confirm a process started during that same second", () =>
        {
            foreach(var state in new[]{"Logged On","Offline"})
            {
                var result=Read(new(true,true,User,Start.AddMilliseconds(750)),Line(0,state,User));
                Assert(state=="Logged On"?result.IsOnline:result.IsOffline);
            }
        }),
        Case("Previous whole second log remains stale with fractional process start", () =>
        {
            var result=Read(new(true,true,User,Start.AddMilliseconds(750)),Line(-1,"Offline",User));
            Assert(result.State==SteamSessionState.Unknown&&!result.IsOnline&&!result.IsOffline);
        }),
        Case("Remembered selected offline target alone cannot confirm a local login", () =>
        {
            var selected=new SteamAccount{SteamId=SteamId,HasCachedLogin=true,IsCurrentSelection=true};
            var result=Read(new(true,true,0,Start),null);
            Assert(result.ActiveSteamId is null&&!result.IsOffline&&!selected.IsOffline);
        }),
        Case("Connection tail reuses unchanged text and rereads appended or same-length modified files", () =>
        {
            using var f=new LogFixture();
            var cache=new SteamConnectionLogCache();
            File.WriteAllText(f.Path,"first\n");
            var first=cache.Read(f.Path);
            Assert(ReferenceEquals(first,cache.Read(f.Path)));
            File.AppendAllText(f.Path,"next\n");
            Assert(cache.Read(f.Path)=="first\nnext\n");
            File.WriteAllText(f.Path,"other\nnext\n");
            File.SetLastWriteTimeUtc(f.Path,DateTime.UtcNow.AddSeconds(5));
            Assert(cache.Read(f.Path)=="other\nnext\n");
        }),
        Case("Connection tail cache keeps paths separate and clears deleted file content", () =>
        {
            using var f=new LogFixture();
            var other=System.IO.Path.Combine(f.Root,"other_log.txt");
            File.WriteAllText(f.Path,"alpha\n");File.WriteAllText(other,"bravo\n");
            var now=DateTime.UtcNow;
            File.SetLastWriteTimeUtc(f.Path,now);File.SetLastWriteTimeUtc(other,now);
            var cache=new SteamConnectionLogCache();
            Assert(cache.Read(f.Path)=="alpha\n"&&cache.Read(other)=="bravo\n");
            File.Delete(other);
            Assert(cache.Read(other) is null&&cache.Read(f.Path)=="alpha\n");
        }),
        Case("Connection tail remains bounded and discards first incomplete line", () =>
        {
            using var f=new LogFixture();
            File.WriteAllText(f.Path,new string('x',300*1024)+"\nlast-complete-line\n");
            Assert(new SteamConnectionLogCache().Read(f.Path)=="last-complete-line\n");
        }),
        Case("MostRecent metadata never creates active or online status", () =>
        {
            var account=new SteamAccount{SteamId=SteamId,IsCurrentSelection=true};
            Assert(!account.IsActive&&!account.IsOnline&&!account.IsOffline);
            account.ApplySession(false,SteamSessionState.Online,"other user is current");
            Assert(!account.IsActive&&!account.IsOnline);
            account.ApplySession(true,SteamSessionState.AwaitingLogin,"pending");
            Assert(account.IsActive&&!account.IsOnline&&account.PresenceLabel=="登录中");
            account.ApplySession(true,SteamSessionState.Online,"confirmed");
            Assert(account.IsActive&&account.IsOnline&&account.PresenceLabel=="在线");
            account.ApplySession(false,SteamSessionState.Stopped,"exited");
            Assert(!account.IsActive&&!account.IsOnline&&!account.IsOffline);
        })
    ];
    private static (string,Func<Task>) Case(string name,Action run)=>(name,()=>{run();return Task.CompletedTask;});
    private static SteamSessionSnapshot Read(SteamProcessEvidence evidence,string? log)=>new SteamSessionService(new FakePlatform(evidence,log),()=>Start.AddMinutes(5)).Read("fixture");
    private static string Line(int seconds,string state,uint accountId,string body="observed")=>$"[{Start.AddSeconds(seconds):yyyy-MM-dd HH:mm:ss}] [{state}, 4, 2] [U:1:{accountId}] {body}\n";
    private static void Assert(bool condition) { if(!condition) throw new InvalidOperationException("Session evidence assertion failed"); }
    private sealed class FakePlatform(SteamProcessEvidence evidence,string? log):ISteamSessionPlatform
    {
        private int _reads;
        public SteamProcessEvidence? NextEvidence {get;init;}
        public int LogReads {get;private set;}
        public SteamProcessEvidence ReadProcess(string steamPath)=>_reads++==0 ? evidence : NextEvidence??evidence;
        public string? ReadConnectionLogTail(string steamPath) {LogReads++;return log;}
    }
    private sealed class LogFixture:IDisposable
    {
        internal string Root {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Gameorder-session-tests-"+Guid.NewGuid().ToString("N"));
        internal string Path=>System.IO.Path.Combine(Root,"connection_log.txt");
        internal LogFixture()=>Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var target=System.IO.Path.GetFullPath(Root);
            var temporary=System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))+System.IO.Path.DirectorySeparatorChar;
            if(!target.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!System.IO.Path.GetFileName(target).StartsWith("Gameorder-session-tests-",StringComparison.Ordinal))
                throw new IOException("Unsafe session fixture cleanup target.");
            Directory.Delete(target,true);
        }
    }
}
