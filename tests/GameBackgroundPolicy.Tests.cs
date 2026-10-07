using Gameorder;
using System.Text.Json;

static class GameBackgroundPolicyTests
{
    public static IReadOnlyList<(string Name,Func<Task> Run)> Tests =>
    [
        ("Game policy reduces periodic work and pauses avatar work",()=>Run(()=>
        {
            var idle=GameBackgroundPolicy.For(false); var playing=GameBackgroundPolicy.For(true);
            Check(idle.PollInterval==TimeSpan.FromSeconds(5)&&playing.PollInterval==TimeSpan.FromSeconds(20),"poll intervals");
            Check(idle.AllowAvatarWork&&!playing.AllowAvatarWork,"avatar work");
            Check(!idle.ShouldLowerOwnProcessPriority&&playing.ShouldLowerOwnProcessPriority,"own priority");
            Check(!idle.ShouldSuspendTaskbar&&playing.ShouldSuspendTaskbar,"widget pause");
        })),
        ("Transient game mode restores widget only when user enabled it",()=>Run(()=>
        {
            foreach(bool enabled in new[]{false,true})
            {
                Check(!GameBackgroundPolicy.For(true).IsTaskbarEffectivelyVisible(enabled),"playing widget visible");
                Check(GameBackgroundPolicy.For(false).IsTaskbarEffectivelyVisible(enabled)==enabled,"ended widget ignores preference");
            }
        })),
        ("Only active account displays a running game",()=>Run(()=>
        {
            var a=new SteamAccount(); a.ApplyGame(true,"Fixture game","fixture evidence");
            Check(!a.IsPlaying&&a.RunningGameName=="","inactive game shown");
            a.ApplySession(true,SteamSessionState.Online,"online");
            Check(a.IsPlaying&&a.RunningGameName=="Fixture game"&&a.GamePresenceLabel=="游戏中","active game missing");
            a.ApplySession(false,SteamSessionState.Online,"other account");
            Check(!a.IsPlaying&&a.RunningGameName==""&&a.GamePresenceLabel==a.PresenceLabel,"other account retained game");
        })),
        ("Game state clears without changing account notes or connection status",()=>Run(()=>
        {
            var a=new SteamAccount{Note="keep",IsFavorite=true};
            a.ApplySession(true,SteamSessionState.Offline,"offline"); a.ApplyGame(true,"Fixture game","proof");
            Check(a.IsOffline&&a.IsPlaying,"offline game hidden");
            a.ApplyGame(false,"","");
            Check(!a.IsPlaying&&a.GamePresenceLabel==a.PresenceLabel&&a.Note=="keep"&&a.IsFavorite&&a.IsOffline,"game end damaged metadata/status");
        })),
        ("Game lifecycle never rewrites persisted widget settings",()=>Run(()=>
        {
            var state=new LocalState{TaskbarWidgetEnabled=true,TaskbarWidgetDock="left"};
            var before=JsonSerializer.Serialize(state);
            foreach(bool running in new[]{true,false,true,false}) _=GameBackgroundPolicy.For(running).IsTaskbarEffectivelyVisible(state.TaskbarWidgetEnabled);
            Check(before==JsonSerializer.Serialize(state),"transient policy changed persistence");
        }))
    ];
    private static Task Run(Action action){action();return Task.CompletedTask;}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
