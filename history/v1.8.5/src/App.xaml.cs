using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Gameorder;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showWindowWait;
    private InstanceMessageService? _messages;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--read-only-check",StringComparer.OrdinalIgnoreCase)) { RunReadOnlyCheck(e.Args); return; }
        bool Has(string flag)=>e.Args.Contains(flag,StringComparer.OrdinalIgnoreCase);
        string? Value(string flag)=>e.Args.SkipWhile(a=>!a.Equals(flag,StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        bool smoke=Has("--ui-smoke"),avatarPreview=Has("--avatar-preview"),shellPreview=Has("--shell-preview"),instanceSmoke=Has("--instance-smoke"),quickSmoke=Has("--quick-ui-smoke");
        string? session=Value("--instance-session");
        bool child=session is not null;
        if((new[]{smoke,avatarPreview,shellPreview,instanceSmoke,quickSmoke}.Count(x=>x)>1)||Has("--instance-session")&&!Guid.TryParseExact(session,"N",out _))
        { StartupError("验证参数不能混用。",child||instanceSmoke); return; }
        if(instanceSmoke) session=Guid.NewGuid().ToString("N");
        bool demo=smoke||quickSmoke||instanceSmoke||child||Has("--demo");
        string instanceNamespace=instanceSmoke||child ? "Gameorder.InstanceVerification."+session : quickSmoke ? "Gameorder.QuickVerification" : shellPreview ? "Gameorder.ShellVerification" : avatarPreview ? "Gameorder.AvatarVerification" : demo ? "Gameorder.Demo" : "Gameorder";
        var command=ParseCommand(e.Args,child);
        if(command is null) { StartupError("快捷操作参数无效。",child||instanceSmoke); return; }
        if((smoke||avatarPreview||shellPreview||instanceSmoke||quickSmoke)&&command.Action!="activate")
        { StartupError("验证模式不能与真实快捷操作混用。",true); return; }
        _instance=new Mutex(true,"Local\\"+instanceNamespace,out bool created);
        if(!created)
        {
            var response=InstanceMessageService.SendAsync(instanceNamespace,command,child).GetAwaiter().GetResult();
            if(!response.Accepted&&command.Action=="activate"&&!demo&&!avatarPreview&&!shellPreview)
            {
                try { using var signal=EventWaitHandle.OpenExisting("Local\\Gameorder.ShowWindow"); signal.Set(); response=new(true,"legacy-activated"); }
                catch(WaitHandleCannotBeOpenedException) { }
            }
            if(!response.Accepted&&!child) MessageBox.Show(response.Code=="busy" ? "当前有操作或管理窗口，请完成后重试。" : "快捷操作未完成，请打开 Gameorder 后重试。","Gameorder",MessageBoxButton.OK,MessageBoxImage.Information);
            Shutdown(response.Accepted?0:6); return;
        }
        if(child) { Shutdown(6); return; }
        if(command.Action=="exit") { Shutdown(0); return; }
        DispatcherUnhandledException+=HandleException;
        try
        {
            if(smoke||avatarPreview||shellPreview||instanceSmoke||quickSmoke) UiVerification.EnableBindingChecks();
            if(!demo&&!smoke&&!avatarPreview) ShellIdentity.SetApplicationIdentity(shellPreview);
            MainWindow=new MainWindow(demo,!shellPreview&&!e.Args.Contains("--local-avatars",StringComparer.OrdinalIgnoreCase),enableShellIntegration:!smoke&&!avatarPreview&&!demo,persistTaskbarPosition:!shellPreview);
            bool background=Has("--background")&&!smoke&&!quickSmoke&&!avatarPreview&&!shellPreview&&!instanceSmoke;
            if(background) { MainWindow.ShowActivated=false; MainWindow.ShowInTaskbar=false; MainWindow.Opacity=0; }
            MainWindow.Show();
            if(background)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>
                { ((MainWindow)MainWindow).HideToBackground(); MainWindow.Opacity=1; MainWindow.ShowActivated=true; }));
            if(!smoke&&!quickSmoke&&!avatarPreview&&!shellPreview)
            {
                _messages=new InstanceMessageService(instanceNamespace,instanceSmoke,HandleInstanceMessageAsync,
                    ()=>Dispatcher.BeginInvoke(new Action(()=>((MainWindow)MainWindow).RequestExit())));
            }
            if(!demo&&!avatarPreview&&!shellPreview)
            {
                _showWindow=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\Gameorder.ShowWindow");
                _showWindowWait=ThreadPool.RegisterWaitForSingleObject(_showWindow,(_,timedOut)=>
                { if(!timedOut&&!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(()=>((MainWindow)MainWindow).ShowFromTray())); },null,Timeout.Infinite,false);
            }
            if(smoke||avatarPreview||shellPreview||instanceSmoke||quickSmoke)
            {
                string flag=instanceSmoke?"--instance-smoke":quickSmoke?"--quick-ui-smoke":shellPreview?"--shell-preview":avatarPreview?"--avatar-preview":"--ui-smoke";
                string output=Value(flag)??Path.Combine(AppContext.BaseDirectory,"ui-verification");
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
                {
                    try
                    {
                        if(instanceSmoke) await RunInstanceSmokeAsync((MainWindow)MainWindow,session!,output);
                        else if(quickSmoke) await UiVerification.RunQuick((MainWindow)MainWindow,output);
                        else if(shellPreview) await UiVerification.RunShell((MainWindow)MainWindow,output);
                        else if(avatarPreview) await UiVerification.RunAvatars((MainWindow)MainWindow,output);
                        else await UiVerification.Run((MainWindow)MainWindow,output);
                        ((MainWindow)MainWindow).RequestExit(); Shutdown(0);
                    }
                    catch(Exception ex) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString()); Shutdown(2); }
                }));
            }
            else if(command.Action!="activate")
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
                {
                    var response=await ((MainWindow)MainWindow).HandleInstanceCommandAsync(command);
                    if(!response.Accepted) ((MainViewModel)MainWindow.DataContext).ShowStatus("快捷操作未完成","请完成当前操作或刷新账号后重试。","info");
                }));
        }
        catch(Exception ex) { MessageBox.Show($"无法启动 Gameorder：{ex.Message}","Gameorder",MessageBoxButton.OK,MessageBoxImage.Error); Shutdown(1); }
    }

    private Task<InstanceResponse> HandleInstanceMessageAsync(InstanceCommand command)
    {
        if(Dispatcher.HasShutdownStarted) return Task.FromResult(new InstanceResponse(false,"closing"));
        return Dispatcher.InvokeAsync(()=>((MainWindow)MainWindow).HandleInstanceCommandAsync(command,command.Action=="test-cancel")).Task.Unwrap();
    }

    public static InstanceCommand? ParseCommand(string[] arguments,bool allowTestCancel=false)
    {
        bool Has(string flag)=>arguments.Contains(flag,StringComparer.OrdinalIgnoreCase);
        string? Value(string flag)=>arguments.SkipWhile(a=>!a.Equals(flag,StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        if(new[]{Has("--switch-account"),Has("--quick-menu"),Has("--exit"),Has("--instance-cancel")}.Count(x=>x)>1) return null;
        if(arguments.Count(a=>a.Equals("--switch-account",StringComparison.OrdinalIgnoreCase))>1||arguments.Count(a=>a.Equals("--mode",StringComparison.OrdinalIgnoreCase))>1) return null;
        var command=Has("--switch-account") ? new InstanceCommand("switch",Value("--switch-account"),Value("--mode"))
            : Has("--quick-menu") ? new("quick-menu") : Has("--exit") ? new("exit") : Has("--instance-cancel") ? new("test-cancel") : new("activate");
        if(Has("--mode")&&command.Action!="switch") return null;
        return InstanceMessageService.IsValid(command,allowTestCancel)?command:null;
    }

    private void StartupError(string message,bool silent)
    { if(!silent) MessageBox.Show(message,"Gameorder",MessageBoxButton.OK,MessageBoxImage.Information); Shutdown(2); }

    private void RunReadOnlyCheck(string[] arguments)
    {
        string output=arguments.SkipWhile(a=>a!="--read-only-check").Skip(1).FirstOrDefault()??Path.Combine(AppContext.BaseDirectory,"verification","local-import.json");
        try
        {
            var service=new SteamService(); string path=service.DetectSteamPath();
            var accounts=string.IsNullOrEmpty(path)?Array.Empty<SteamAccount>():service.ReadAccounts(path);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new {SteamFound=!string.IsNullOrEmpty(path),AccountCount=accounts.Count,CachedCount=accounts.Count(a=>a.HasCachedLogin),ReadOnly=true,SteamStarted=false,CredentialsIncluded=false})); Shutdown(0);
        }
        catch(Exception ex) { File.WriteAllText(output+".error.txt",ex.Message); Shutdown(2); }
    }

    private static async Task RunInstanceSmokeAsync(MainWindow window,string session,string output)
    {
        Directory.CreateDirectory(output);
        var vm=(MainViewModel)window.DataContext;
        var checks=new List<string>();
        void Check(bool value,string text) { if(!value) throw new InvalidOperationException(text); checks.Add(text); }
        async Task<int> Child(params string[] arguments)
        {
            string executable=Environment.ProcessPath??throw new InvalidOperationException("无法定位验证程序。");
            var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"Gameorder.dll"));
            start.ArgumentList.Add("--instance-session"); start.ArgumentList.Add(session);
            foreach(string argument in arguments) start.ArgumentList.Add(argument);
            using var process=Process.Start(start)??throw new InvalidOperationException("无法创建验证子进程。");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); return process.ExitCode;
        }
        Check(vm.IsDemo&&!window.ShellIntegrationEnabled,"验证实例与真实账号、系统菜单隔离");
        var account=vm.AllAccounts.First(a=>a.HasCachedLogin);
        Check(await Child("--switch-account",account.SteamId,"--mode","online")==0,"第二进程在线请求送达");
        Check(vm.PendingConfirmation&&ReferenceEquals(vm.SelectedAccount,account)&&!vm.IsBusy&&vm.Activity.Count==0,"只打开确认，未执行 Steam 切换");
        Check(await Child("--instance-cancel")==0&&!vm.PendingConfirmation,"第二进程取消请求不执行切换");
        Check(await Child("--switch-account",account.SteamId,"--mode","offline")==0&&vm.PendingConfirmation,"第二进程离线请求送达");
        await Child("--instance-cancel");
        Check(await Child("--switch-account","00000000000000000","--mode","online")==6&&!vm.PendingConfirmation,"未知账号请求被拒绝");
        Check(await Child("--switch-account",account.SteamId,"--mode","invalid")==2&&!vm.PendingConfirmation,"无效模式被拒绝");
        vm.AddAccountCommand.Execute(null); vm.NewAccountName="ipc_manual_a"; vm.NewDisplayName="IPC 手动账号 A"; vm.SaveNewAccountCommand.Execute(null);
        var manual=vm.SelectedAccount!;
        vm.AddAccountCommand.Execute(null); vm.NewAccountName="ipc_manual_b"; vm.NewDisplayName="IPC 手动账号 B"; vm.SaveNewAccountCommand.Execute(null);
        Check(await Child("--switch-account","account:ipc_manual_a","--mode","online")==0&&ReferenceEquals(vm.SelectedAccount,manual)&&vm.PendingConfirmation,"手动账号按精确登录名选择，空 SteamID 不串号");
        await Child("--instance-cancel");
        Check(!vm.IsBusy&&!vm.PendingConfirmation&&vm.Activity.Count==0,"跨进程验证从未确认或执行真实操作");
        File.WriteAllText(Path.Combine(output,"instance-result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks,SameUserPipe=true,MessageByteLimit=InstanceMessageService.MaximumMessageBytes,RealSteamStarted=false,SteamConfigurationChanged=false},new JsonSerializerOptions {WriteIndented=true}));
    }

    private void HandleException(object sender,DispatcherUnhandledExceptionEventArgs e)
    { e.Handled=true; MessageBox.Show($"操作未完成：{e.Exception.Message}\n请重试或检查 Steam 路径。","Gameorder",MessageBoxButton.OK,MessageBoxImage.Warning); }
    protected override void OnExit(ExitEventArgs e)
    { _messages?.Dispose(); _showWindowWait?.Unregister(null); _showWindow?.Dispose(); _instance?.Dispose(); base.OnExit(e); }
}

