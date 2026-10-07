namespace Gameorder;

/// <summary>Transient scheduling choices, separate from saved preferences.</summary>
public sealed record GameBackgroundPolicy(TimeSpan PollInterval,bool AllowAvatarWork,
    bool ShouldLowerOwnProcessPriority,bool ShouldSuspendTaskbar)
{
    public bool ShouldUseEcoQoS { get; init; }
    private static readonly GameBackgroundPolicy Idle=new(TimeSpan.FromSeconds(5),true,false,false);
    private static readonly GameBackgroundPolicy Playing=new(TimeSpan.FromSeconds(20),false,true,true);
    private static readonly GameBackgroundPolicy Background=new(TimeSpan.FromSeconds(15),false,true,false) {ShouldUseEcoQoS=true};
    private static readonly GameBackgroundPolicy GameBackground=Playing with {ShouldUseEcoQoS=true};
    private static readonly GameBackgroundPolicy GameForeground=Playing with {ShouldLowerOwnProcessPriority=false};
    public static GameBackgroundPolicy For(bool gameRunning)=>gameRunning?Playing:Idle;
    public static GameBackgroundPolicy For(bool gameRunning,bool foregroundInteractive)=>gameRunning
        ? foregroundInteractive?GameForeground:GameBackground : foregroundInteractive?Idle:Background;
    public bool IsTaskbarEffectivelyVisible(bool userEnabled)=>userEnabled&&!ShouldSuspendTaskbar;
}