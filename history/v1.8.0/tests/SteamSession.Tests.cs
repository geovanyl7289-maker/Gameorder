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
        public SteamProcessEvidence ReadProcess(string steamPath)=>_reads++==0 ? evidence : NextEvidence??evidence;
        public string? ReadConnectionLogTail(string steamPath)=>log;
    }
}
