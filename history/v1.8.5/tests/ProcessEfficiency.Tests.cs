using System.Diagnostics;
using Gameorder;

public static class ProcessEfficiencyTests
{
    public static void Register(List<(string Name,Func<Task> Run)> tests)
    {
        tests.Add(Case("Efficiency applies both mechanisms and restores exact prior policy",()=>
        {
            var fake=new Fake {State=new(1,2,2),Priority=ProcessPriorityClass.AboveNormal};
            using var service=new ProcessEfficiencyService(fake);
            var bg=service.SetBackgroundEfficiency(true);
            Check(bg.Applied&&bg.EcoQoSApplied&&bg.PriorityLowered&&bg.Supported);
            Check(fake.State.ControlMask==3&&fake.State.StateMask==3&&fake.Priority==ProcessPriorityClass.BelowNormal);
            int writes=fake.PowerWrites+fake.PriorityWrites;
            service.SetBackgroundEfficiency(true); Check(fake.PowerWrites+fake.PriorityWrites==writes);
            fake.State=new(1,7,7);
            var front=service.SetBackgroundEfficiency(false);
            Check(!front.Applied&&!front.RestorePending&&!front.EcoQoSApplied&&!front.PriorityLowered);
            Check(fake.State.ControlMask==6&&fake.State.StateMask==6);
            Check(fake.Priority==ProcessPriorityClass.AboveNormal);
            service.SetBackgroundEfficiency(true); service.Dispose();
            Check(fake.State.ControlMask==6&&fake.Priority==ProcessPriorityClass.AboveNormal);
        }));
        tests.Add(Case("Efficiency unsupported power API retains safe own-priority fallback",()=>
        {
            var now=DateTimeOffset.UtcNow;
            var fake=new Fake {ReadPowerError=87};
            using var service=new ProcessEfficiencyService(fake,()=>now);
            var bg=service.SetBackgroundEfficiency(true);
            Check(!bg.Supported&&!bg.EcoQoSApplied&&bg.PriorityLowered);
            now=now.AddMinutes(1); service.SetBackgroundEfficiency(true);
            Check(fake.PowerReads==1&&fake.PowerWrites==0);
            service.SetBackgroundEfficiency(false); Check(fake.Priority==ProcessPriorityClass.Normal);
        }));
        tests.Add(Case("Efficiency failed restores remain pending and retry without write storms",()=>
        {
            var now=DateTimeOffset.UtcNow;
            var fake=new Fake();
            using var service=new ProcessEfficiencyService(fake,()=>now);
            service.SetBackgroundEfficiency(true); fake.PowerWriteError=5; fake.PriorityWriteError=5;
            var result=service.SetBackgroundEfficiency(false);
            Check(result.RestorePending&&result.LastError==5&&result.EcoQoSApplied&&result.PriorityLowered);
            int writes=fake.PowerWrites+fake.PriorityWrites; service.SetBackgroundEfficiency(false);
            Check(fake.PowerWrites+fake.PriorityWrites==writes);
            fake.PowerWriteError=0; fake.PriorityWriteError=0; now=now.AddSeconds(31);
            result=service.SetBackgroundEfficiency(false);
            Check(!result.RestorePending&&result.LastError==0&&fake.State.ControlMask==0&&fake.Priority==ProcessPriorityClass.Normal);
        }));
        tests.Add(Case("Efficiency partial apply failure restores only successfully changed policy",()=>
        {
            var fake=new Fake {PowerWriteError=5};
            using var service=new ProcessEfficiencyService(fake);
            var result=service.SetBackgroundEfficiency(true);
            Check(!result.EcoQoSApplied&&result.PriorityLowered&&result.LastError==5);
            service.SetBackgroundEfficiency(false); Check(!service.Snapshot.RestorePending&&fake.Priority==ProcessPriorityClass.Normal);
        }));
        tests.Add(Case("Efficiency preexisting EcoQoS and Idle priority are not rewritten",()=>
        {
            var fake=new Fake {State=new(1,1,1),Priority=ProcessPriorityClass.Idle};
            using var service=new ProcessEfficiencyService(fake);
            service.SetBackgroundEfficiency(true); service.SetBackgroundEfficiency(false);
            Check(fake.PowerWrites==0&&fake.PriorityWrites==0&&fake.Priority==ProcessPriorityClass.Idle);
        }));
        tests.Add(Case("Memory priority lowers only own default pages and restores exact initial value",()=>
        {
            foreach(uint before in new uint[]{3,4,5})
            {
                var fake=new Fake {MemoryPriority=before};
                using var service=new ProcessEfficiencyService(fake);
                var background=service.SetBackgroundEfficiency(true);
                Check(background.MemoryPriorityLowered&&background.MemoryPrioritySupported&&fake.MemoryPriority==2);
                service.SetBackgroundEfficiency(true); Check(fake.MemoryWrites==1);
                service.SetBackgroundEfficiency(false);
                Check(fake.MemoryPriority==before&&!service.Snapshot.MemoryPriorityLowered&&!service.Snapshot.RestorePending);
                service.SetBackgroundEfficiency(true); service.Dispose(); Check(fake.MemoryPriority==before);
            }
        }));
        tests.Add(Case("Existing very-low or low memory priority is never raised or overwritten",()=>
        {
            foreach(uint before in new uint[]{1,2})
            {
                var fake=new Fake {MemoryPriority=before};
                using var service=new ProcessEfficiencyService(fake);
                Check(service.SetBackgroundEfficiency(true).MemoryPriorityLowered);
                service.SetBackgroundEfficiency(false); service.Dispose();
                Check(fake.MemoryPriority==before&&fake.MemoryWrites==0);
            }
        }));
        tests.Add(Case("Unsupported memory priority is independent from working scheduling APIs",()=>
        {
            var now=DateTimeOffset.UtcNow;
            var fake=new Fake {MemoryReadError=50};
            using var service=new ProcessEfficiencyService(fake,()=>now);
            var background=service.SetBackgroundEfficiency(true);
            Check(background.Supported&&background.EcoQoSApplied&&background.PriorityLowered&&!background.MemoryPrioritySupported&&!background.MemoryPriorityLowered);
            now=now.AddMinutes(1); service.SetBackgroundEfficiency(true);
            Check(fake.MemoryReads==1&&fake.MemoryWrites==0);
            service.SetBackgroundEfficiency(false);
            Check(!service.Snapshot.RestorePending&&fake.Priority==ProcessPriorityClass.Normal&&fake.State.StateMask==0);
        }));
        tests.Add(Case("Memory-only restore failures preserve pending state and recover without write storms",()=>
        {
            var now=DateTimeOffset.UtcNow;
            var fake=new Fake {MemoryPriority=4};
            using var service=new ProcessEfficiencyService(fake,()=>now);
            service.SetBackgroundEfficiency(true); fake.MemoryWriteError=5;
            var foreground=service.SetBackgroundEfficiency(false);
            Check(foreground.RestorePending&&foreground.MemoryPriorityLowered&&foreground.LastError==5&&!foreground.EcoQoSApplied&&!foreground.PriorityLowered);
            int writes=fake.MemoryWrites; service.SetBackgroundEfficiency(false); Check(fake.MemoryWrites==writes);
            fake.MemoryWriteError=0; fake.MemoryReadError=5; now=now.AddSeconds(31);
            Check(service.SetBackgroundEfficiency(false).RestorePending&&fake.MemoryPriority==2);
            fake.MemoryReadError=0; now=now.AddSeconds(31);
            Check(!service.SetBackgroundEfficiency(false).RestorePending&&fake.MemoryPriority==4);
        }));
        tests.Add(Case("Failed memory applies retry transient errors and never restore untouched defaults",()=>
        {
            var now=DateTimeOffset.UtcNow;
            var fake=new Fake {MemoryWriteError=5};
            using var service=new ProcessEfficiencyService(fake,()=>now);
            Check(!service.SetBackgroundEfficiency(true).MemoryPriorityLowered&&fake.MemoryPriority==5);
            fake.MemoryWriteError=0; now=now.AddSeconds(31);
            Check(service.SetBackgroundEfficiency(true).MemoryPriorityLowered&&fake.MemoryPriority==2);
            service.SetBackgroundEfficiency(false); Check(fake.MemoryPriority==5);
            var invalid=new Fake {MemoryPriority=0};
            using var invalidService=new ProcessEfficiencyService(invalid);
            Check(!invalidService.SetBackgroundEfficiency(true).MemoryPrioritySupported&&invalid.MemoryWrites==0);
        }));
        tests.Add(Case("Foreground/background efficiency policy preserves legacy and game behavior",()=>
        {
            Check(GameBackgroundPolicy.For(false).PollInterval.TotalSeconds==5&&GameBackgroundPolicy.For(true).PollInterval.TotalSeconds==20);
            var foreground=GameBackgroundPolicy.For(false,true); var background=GameBackgroundPolicy.For(false,false);
            var gamefront=GameBackgroundPolicy.For(true,true); var gameback=GameBackgroundPolicy.For(true,false);
            Check(foreground.PollInterval.TotalSeconds==5&&!foreground.ShouldUseEcoQoS&&!foreground.ShouldLowerOwnProcessPriority);
            Check(background.PollInterval.TotalSeconds==15&&background.ShouldUseEcoQoS&&background.ShouldLowerOwnProcessPriority);
            Check(gamefront.PollInterval.TotalSeconds==20&&!gamefront.ShouldUseEcoQoS&&!gamefront.ShouldLowerOwnProcessPriority&&gamefront.ShouldSuspendTaskbar);
            Check(gameback.PollInterval.TotalSeconds==20&&gameback.ShouldUseEcoQoS&&gameback.ShouldSuspendTaskbar);
        }));
    }
    private static (string,Func<Task>) Case(string name,Action action)=>(name,()=> {action(); return Task.CompletedTask;});
    private static void Check(bool valid) {if(!valid) throw new InvalidOperationException("Own-process efficiency fixture assertion failed");}
    private sealed class Fake:IOwnProcessEfficiencyPlatform
    {
        public OwnProcessPowerState State=new(1,0,0);
        public ProcessPriorityClass Priority=ProcessPriorityClass.Normal;
        public int PowerReads,PowerWrites,PriorityWrites,ReadPowerError,PowerWriteError,PriorityWriteError;
        public uint MemoryPriority=5;
        public int MemoryReads,MemoryWrites,MemoryReadError,MemoryWriteError;
        public bool TryReadMemoryPriority(out uint priority,out int error) {MemoryReads++;priority=MemoryPriority;error=MemoryReadError;return error==0;}
        public bool TryWriteMemoryPriority(uint priority,out int error) {MemoryWrites++;error=MemoryWriteError;if(error!=0)return false;MemoryPriority=priority;return true;}
        public bool TryReadPowerState(out OwnProcessPowerState state,out int error) {PowerReads++;state=State;error=ReadPowerError;return error==0;}
        public bool TryWritePowerState(OwnProcessPowerState state,out int error) {PowerWrites++;error=PowerWriteError;if(error!=0)return false;State=state;return true;}
        public bool TryReadPriority(out ProcessPriorityClass priority,out int error) {priority=Priority;error=0;return true;}
        public bool TryWritePriority(ProcessPriorityClass priority,out int error) {PriorityWrites++;error=PriorityWriteError;if(error!=0)return false;Priority=priority;return true;}
    }
}
