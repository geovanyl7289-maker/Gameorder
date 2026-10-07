namespace Gameorder;

public enum ForgotPasswordPhase { Confirmation, CoolingDown, ReadyToHold, Holding, Authorized, Cancelled, Completed, Failed }

/// <summary>Monotonic, cancellable consent state. Expiry alone never authorizes a destructive reset.</summary>
public sealed class ForgotPasswordFlow : ObservableObject
{
    public const string RequiredConfirmation="我已经确认";
    public static readonly TimeSpan CoolingDuration=TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HoldingDuration=TimeSpan.FromSeconds(3);
    private readonly TimeProvider _time;
    private long _coolingStarted,_holdStarted;
    private ForgotPasswordPhase _phase=ForgotPasswordPhase.Confirmation;
    private string _confirmation="";
    private ResetConsent? _consent;
    private bool _consumed;
    public ForgotPasswordFlow(TimeProvider? timeProvider=null)=>_time=timeProvider??TimeProvider.System;
    public ForgotPasswordPhase Phase {get=>_phase; private set { if(Set(ref _phase,value)) NotifyState(); } }
    public string ConfirmationText {get=>_confirmation; set { if(Phase!=ForgotPasswordPhase.Confirmation) return; if(Set(ref _confirmation,value)) Notify(nameof(CanStartCooling)); } }
    public bool CanStartCooling=>Phase==ForgotPasswordPhase.Confirmation&&ConfirmationText==RequiredConfirmation;
    public bool IsConfirmation=>Phase==ForgotPasswordPhase.Confirmation;
    public bool IsCooling=>Phase==ForgotPasswordPhase.CoolingDown;
    public bool CanHold=>Phase is ForgotPasswordPhase.ReadyToHold or ForgotPasswordPhase.Holding;
    public bool IsHolding=>Phase==ForgotPasswordPhase.Holding;
    public bool IsCancelled=>Phase==ForgotPasswordPhase.Cancelled;
    public int CoolingSecondsRemaining=>Phase==ForgotPasswordPhase.CoolingDown?
        Math.Max(0,(int)Math.Ceiling((CoolingDuration-_time.GetElapsedTime(_coolingStarted)).TotalSeconds)):0;
    public double HoldProgress=>Phase==ForgotPasswordPhase.Holding?Math.Clamp(_time.GetElapsedTime(_holdStarted).TotalSeconds/HoldingDuration.TotalSeconds,0,1):0;
    public string StatusText=>Phase switch {
        ForgotPasswordPhase.Confirmation=>"输入“我已经确认”后继续",
        ForgotPasswordPhase.CoolingDown=>$"冷静确认 · {CoolingSecondsRemaining} 秒",
        ForgotPasswordPhase.ReadyToHold=>"连续长按 3 秒才会永久清空",
        ForgotPasswordPhase.Holding=>$"请保持按住 · {Math.Max(0,HoldingDuration.TotalSeconds-_time.GetElapsedTime(_holdStarted).TotalSeconds):0.0} 秒",
        ForgotPasswordPhase.Authorized=>"正在永久清空…",
        ForgotPasswordPhase.Cancelled=>"已取消，资料未更改",
        ForgotPasswordPhase.Completed=>"本机资料已清空",
        _=>"清空未完成"};
    public bool StartCooling()
    {
        if(!CanStartCooling) return false;
        _coolingStarted=_time.GetTimestamp(); Phase=ForgotPasswordPhase.CoolingDown; return true;
    }
    public void Tick()
    {
        if(Phase==ForgotPasswordPhase.CoolingDown&&_time.GetElapsedTime(_coolingStarted)>=CoolingDuration)
            Phase=ForgotPasswordPhase.ReadyToHold;
        // Holding requires the dialog to observe a continuous live press and explicitly ask for consent.
        NotifyState();
    }
    public bool BeginHold()
    {
        Tick();
        if(Phase!=ForgotPasswordPhase.ReadyToHold) return false;
        _holdStarted=_time.GetTimestamp(); Phase=ForgotPasswordPhase.Holding; return true;
    }
    public ResetConsent? CompleteHold()
    {
        if(Phase!=ForgotPasswordPhase.Holding||_time.GetElapsedTime(_holdStarted)<HoldingDuration) return null;
        _consent=new(this); Phase=ForgotPasswordPhase.Authorized; return _consent;
    }
    public void Cancel()
    {
        if(Phase==ForgotPasswordPhase.Completed||_consumed) return;
        _consent?.Invalidate(); _consent=null; _confirmation=""; Phase=ForgotPasswordPhase.Cancelled; Notify(nameof(ConfirmationText));
    }
    internal bool Consume(ResetConsent consent)
    {
        if(Phase!=ForgotPasswordPhase.Authorized||!ReferenceEquals(consent,_consent)||!consent.Take()) return false;
        _consent=null; _consumed=true; return true;
    }
    internal void CompleteReset(bool succeeded) { if(_consumed) Phase=succeeded?ForgotPasswordPhase.Completed:ForgotPasswordPhase.Failed; }
    private void NotifyState()
    {
        foreach(var name in new[]{nameof(CanStartCooling),nameof(IsConfirmation),nameof(IsCooling),nameof(CanHold),nameof(IsHolding),
            nameof(IsCancelled),nameof(CoolingSecondsRemaining),nameof(HoldProgress),nameof(StatusText)}) Notify(name);
    }
}

/// <summary>Single-use proof owned by the consent flow; never created by an arbitrary button click.</summary>
public sealed class ResetConsent
{
    private readonly ForgotPasswordFlow _owner;
    private int _valid=1;
    internal ResetConsent(ForgotPasswordFlow owner)=>_owner=owner;
    internal void Invalidate()=>Interlocked.Exchange(ref _valid,0);
    internal bool Take()=>Interlocked.Exchange(ref _valid,0)==1;
    internal bool TryConsume()=>_owner.Consume(this);
    internal void CompleteReset(bool succeeded)=>_owner.CompleteReset(succeeded);
}

public sealed record AppLockDelayOption(int Minutes,string Label);
