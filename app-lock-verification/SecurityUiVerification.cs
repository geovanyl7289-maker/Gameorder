using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using Gameorder;

internal static class SecurityUiVerification
{
    private const string Password="demo-only-password-190",NewPassword="another-demo-password-190";
    private const string PasswordHint="毕业时用过的昵称",ChangedHint="第一次旅行的城市";
    private const BindingFlags Private=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly List<string> Checks=[];
    private static void Check(bool valid,string name) { if(!valid) throw new InvalidOperationException(name); Checks.Add(name); }
    private static object? Invoke(object obj,string name,params object?[] args)=>obj.GetType().GetMethod(name,Private)!.Invoke(obj,args);
    private static object? Field(object obj,string name)=>obj.GetType().GetField(name,Private)!.GetValue(obj);
    private static void SetProperty(object obj,string name,object value)=>obj.GetType().GetProperty(name,Private)!.SetValue(obj,value);
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    private static void VerifyActivityInput(Window window,MainViewModel vm,TestTimeProvider clock,string surface)
    {
        clock.Advance(TimeSpan.FromSeconds(1));
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,Environment.TickCount,Key.LeftShift)
            {RoutedEvent=Keyboard.PreviewKeyDownEvent});
        Check((long)Field(vm,"_lastActivityTimestamp")! == clock.GetTimestamp(),surface+" keyboard input resets idle timestamp");
        clock.Advance(TimeSpan.FromSeconds(1));
        window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
            {RoutedEvent=Mouse.PreviewMouseDownEvent});
        Check((long)Field(vm,"_lastActivityTimestamp")! == clock.GetTimestamp(),surface+" mouse click resets idle timestamp");
        clock.Advance(TimeSpan.FromSeconds(1));
        window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120)
            {RoutedEvent=Mouse.PreviewMouseWheelEvent});
        Check((long)Field(vm,"_lastActivityTimestamp")! == clock.GetTimestamp(),surface+" mouse wheel resets idle timestamp");
    }
    private static void Save(Window window,string path)
    {
        window.UpdateLayout();
        var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        image.Render(window); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create(path); encoder.Save(stream);
    }

    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        string fixtureRoot=Path.Combine(output,"isolated-security-fixtures"); Directory.CreateDirectory(fixtureRoot);
        VerifyService(fixtureRoot);
        var clock=new TestTimeProvider();
        var store=new TestStorage(); var service=new AppLockService(store);
        Check(service.Enable(Password,out _,PasswordHint),"isolated service enables lock with an optional user hint");
        var window=new MainWindow(demo:true,allowAvatarNetwork:false,enableShellIntegration:false,persistTaskbarPosition:false,appLockService:service,appLockTimeProvider:clock)
            {ShowActivated=false};
        var vm=(MainViewModel)window.DataContext;
        try
        {
            window.Show(); await Idle();
            var overlay=(Grid)window.FindName("LockOverlay"); var content=(Grid)window.FindName("ProtectedContent");
            var passwordBox=(PasswordBox)window.FindName("UnlockPassword");
            Check(vm.IsLocked&&vm.AppLockEnabled,"enabled startup is locked");
            Check(vm.CurrentPasswordHint==PasswordHint&&vm.HasPasswordHint&&!vm.ShowPasswordHint,"saved hint is available but stays hidden on the initial lock screen");
            Check(overlay.IsVisible&&overlay.Background is SolidColorBrush {Color.A:255},"lock overlay is visible and opaque");
            Check(!content.IsEnabled&&!content.IsVisible,"account and info area is hidden and disabled");
            Check(!((TextBlock)window.FindName("CurrentSessionText")).IsVisible,"current account text is hidden while locked");
            Check(passwordBox.IsVisible&&passwordBox.MaxLength==4096,"unlock uses a bounded PasswordBox");
            Check(!window.CanMaximize&&window.NativeChromeVerification.NativeMaximizeBoxRemoved,"lock screen preserves custom no-maximize chrome");
            Check(!(Field(vm,"_appLockTimer") is DispatcherTimer {IsEnabled:true}),"locked timer does not wake needlessly");
            Save(window,Path.Combine(output,"01-startup-locked.png"));
            var active=vm.AllAccounts.First(a=>a.IsActive); var accountCount=vm.AllAccounts.Count; var activityCount=vm.Activity.Count;
            var allCommands=typeof(MainViewModel).GetProperties().Where(p=>typeof(System.Windows.Input.ICommand).IsAssignableFrom(p.PropertyType))
                .Select(p=>(Name:p.Name,Command:(System.Windows.Input.ICommand)p.GetValue(vm)!)).ToArray();
            Check(allCommands.All(c=>!c.Command.CanExecute(null)),"all exposed account, management, settings and confirmation commands are disabled while locked");
            await vm.SelectAndSwitchAsync(active.SteamId,SwitchMode.Online);
            Check(!vm.PendingConfirmation&&!vm.IsBusy,"direct VM switch is blocked before confirmation; widget continuation is validated separately");
            foreach(string name in new[]{"SaveFolder","OpenEditAccount","SaveNewAccount","SaveSettings","SaveNote","RequestClearActivity","RequestDeleteFolder"}) Invoke(vm,name);
            await (Task)Invoke(vm,"RequestRestore")!;
            await (Task)Invoke(vm,"ExecuteSwitch",active,false)!;
            Check(vm.AllAccounts.Count==accountCount&&vm.Activity.Count==activityCount&&!vm.PendingConfirmation&&!vm.IsBusy&&!vm.HasManagementDialog,
                "direct private management, restore and switch methods hit the central lock guard");
            var quick=await window.HandleInstanceCommandAsync(new("quick-menu"));
            Check(!quick.Accepted&&quick.Code=="locked"&&window.IsVisible&&!window.QuickAccountsVisible,
                "IPC quick request shows only the unlock screen; switch continuation is validated separately");
            window.ShowQuickAccounts(); Check(!window.QuickAccountsVisible&&window.IsVisible,"quick open while locked redirects to main unlock screen");
            string note=vm.NoteDraft,path=vm.SteamPath; vm.NoteDraft="blocked edit"; vm.SteamPath="blocked path";
            Check(vm.NoteDraft==note&&vm.SteamPath==path,"locked direct note and Steam path writes are rejected");
            var messageChanges=new List<string>();System.ComponentModel.PropertyChangedEventHandler changed=(_,e)=> {if(e.PropertyName is not null)messageChanges.Add(e.PropertyName);};
            vm.PropertyChanged+=changed;
            int failuresBefore=(int)Field(vm,"_failedUnlockAttempts")!;long epochBefore=(long)Field(vm,"_lockEpoch")!;
            Check(!await vm.TryUnlockAsync("")&&vm.AppLockMessage=="请输入应用密码。"&&vm.HasAppLockMessage,"empty unlock shows a clear input prompt");
            Check(!vm.ShowPasswordHint,"empty unlock never reveals the saved password hint");
            Check((int)Field(vm,"_failedUnlockAttempts")! ==failuresBefore&&(long)Field(vm,"_lockEpoch")! ==epochBefore&&!vm.AppLockChecking,
                "empty unlock consumes no failed attempt and never starts asynchronous password checking");
            Check(messageChanges.Contains(nameof(MainViewModel.HasAppLockMessage)),"message changes notify the error-panel visibility binding");
            for(int attempt=0;attempt<5;attempt++)
            {
                Check(!await vm.TryUnlockAsync("not-the-password"),"wrong unlock attempt "+(attempt+1)+" is rejected");
                Check(vm.AppLockMessage=="密码错误，请重新输入。"&&vm.HasAppLockMessage,"wrong unlock attempt "+(attempt+1)+" shows an obvious Chinese password error");
                Check(vm.ShowPasswordHint&&vm.CurrentPasswordHint==PasswordHint,"wrong unlock attempt "+(attempt+1)+" shows the user's saved hint");
            }
            Check(!await vm.TryUnlockAsync(Password)&&vm.IsLocked,"five failures enforce a short cooldown even for a correct password");
            Check(vm.AppLockMessage=="尝试过多，请稍后再试。"&&vm.HasAppLockMessage,"cooldown keeps its distinct prompt instead of a misleading password error");
            Check(vm.ShowPasswordHint,"cooldown after incorrect passwords keeps the saved hint visible");
            clock.Advance(TimeSpan.FromSeconds(31));
            Check(await vm.TryUnlockAsync(Password),"correct password unlocks after cooldown"); await Idle();
            Check(!vm.HasAppLockMessage&&vm.AppLockMessage.Length==0,"correct unlock clears the error text and error-panel visibility");vm.PropertyChanged-=changed;
            Check(!vm.ShowPasswordHint&&vm.HasPasswordHint,"successful unlock hides the hint without deleting it");
            Check(!vm.IsLocked&&!overlay.IsVisible&&content.IsVisible&&content.IsEnabled,"successful unlock restores protected content");
            Check(!(Field(vm,"_appLockTimer") is DispatcherTimer {IsEnabled:true}),"main window open leaves background-only timer stopped");
            VerifyActivityInput(window,vm,clock,"main window");
            vm.ShowSettingsCommand.Execute(null); await Idle(); Save(window,Path.Combine(output,"02-security-settings.png"));
            var settings=(Border)window.FindName("SecuritySettingsCard"); Check(settings.IsVisible,"security settings card is present");
            var dialog=new AppLockDialog(vm,AppLockSettingsAction.ChangePassword) {Owner=window,ShowActivated=false};
            try
            {
                dialog.Show(); await Idle();
                Check(dialog.FindName("CurrentPasswordBox") is PasswordBox&&dialog.FindName("NewPasswordBox") is PasswordBox&&
                    dialog.FindName("RepeatPasswordBox") is PasswordBox,"password setup/change uses three PasswordBox fields");
                var hintInput=(TextBox)dialog.FindName("PasswordHintBox");
                Check(hintInput.MaxLength==80&&!hintInput.AcceptsReturn&&hintInput.TextWrapping==TextWrapping.NoWrap&&hintInput.Text==PasswordHint,
                    "password change prefills the saved optional hint in a bounded single-line editor");
                Save(dialog,Path.Combine(output,"03-password-dialog.png"));
                VerifyActivityInput(dialog,vm,clock,"password settings dialog");
                await VerifyPasswordReveal(dialog,vm,output);
            }
            finally { dialog.Close(); }
            Check(new[]{"Current","New","Repeat"}.All(prefix=>((TextBox)dialog.FindName(prefix+"PasswordPlain")).Text.Length==0&&((PasswordBox)dialog.FindName(prefix+"PasswordBox")).Password.Length==0),
                "closed password dialog clears masked and plain controls for all three fields");
            Check(((TextBox)dialog.FindName("PasswordHintBox")).Text.Length==0,"closed password dialog also clears its hint editor");
            vm.BeginBackgroundLockCountdown();
            Check(Field(vm,"_appLockTimer") is DispatcherTimer {IsEnabled:true},"background countdown starts its light timer");
            clock.Advance(TimeSpan.FromMinutes(4)); vm.CheckAppLockIdle(); Check(!vm.IsLocked,"less than five background minutes stays unlocked");
            vm.RecordAppActivity(); clock.Advance(TimeSpan.FromMinutes(4)); vm.CheckAppLockIdle(); Check(!vm.IsLocked,"new input resets the idle deadline");
            clock.Advance(TimeSpan.FromMinutes(1)); vm.CheckAppLockIdle(); Check(vm.IsLocked,"five background minutes locks");
            Check(await vm.TryUnlockAsync(Password),"background lock can be unlocked");vm.CancelBackgroundLockCountdown();
            vm.ShowAccountsCommand.Execute(null); await vm.SelectAndSwitchAsync(active.SteamId,SwitchMode.Online);
            var staleConfirmation=(Func<Task>)Field(vm,"_confirmation")!;
            Check(vm.PendingConfirmation,"unlocked switch may request confirmation");
            vm.RequestAppLock();
            Check(!vm.PendingConfirmation&&Field(vm,"_confirmation") is null,"locking clears a pending confirmation and its callback");
            vm.ConfirmCommand.Execute(null); await staleConfirmation();
            Check(!vm.IsBusy&&vm.Activity.Count==activityCount,"confirmation command and stale callback cannot execute after lock");
            Check(await vm.TryUnlockAsync(Password),"manual lock unlocks");
            SetProperty(vm,"IsBusy",true); vm.RequestAppLock("windows-session");
            Check(!vm.IsLocked&&Field(vm,"_lockPending") is true,"busy operation defers lock without interrupting it");
            SetProperty(vm,"IsBusy",false); Check(vm.IsLocked,"busy completion immediately applies deferred lock");
            Check(await vm.TryUnlockAsync(Password),"deferred lock unlocks");
            window.ShowQuickAccounts(); await Idle(); Check(window.QuickAccountsVisible,"unlocked quick chooser may open");
            VerifyActivityInput((Window)Field(window,"_quickAccounts")!,vm,clock,"quick account chooser");
            vm.RequestAppLock(); await Idle(); Check(!window.QuickAccountsVisible,"locking closes an already-open quick chooser");
            Check(await vm.TryUnlockAsync(Password),"quick lock unlocks");
            window.HideToBackground();Check(!vm.IsLocked&&!window.IsVisible&&vm.IsBackgroundLockCountdownActive,"hiding starts the default delayed background lock");
            clock.Advance(TimeSpan.FromMinutes(5));vm.CheckAppLockIdle();Check(vm.IsLocked,"hidden app locks when its selected delay expires");
            window.ShowFromTray(); Check(window.IsVisible&&vm.IsLocked&&!window.QuickAccountsVisible,"tray reopen stays locked");
            var unlocking=vm.TryUnlockAsync(Password); vm.RequestAppLock("windows-session");
            Check(!await unlocking&&vm.IsLocked,"a new session lock request invalidates an in-flight successful unlock");
            Check(await vm.TryUnlockAsync(Password),"fresh unlock works after the invalidated request");
            Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangePassword,"wrong-current",NewPassword,NewPassword),"change password rejects incorrect current password");
            Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangePassword,Password,NewPassword,NewPassword),"password change verifies current password");
            vm.RequestAppLock(); Check(!await vm.TryUnlockAsync(Password)&&await vm.TryUnlockAsync(NewPassword),"old password no longer unlocks after a successful change");
            Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.Disable,"wrong-current","",""),"disable rejects incorrect current password");
            Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.Disable,NewPassword,"",""),"disable verifies current password");
            Check(!vm.AppLockEnabled&&!(Field(vm,"_appLockTimer") is DispatcherTimer {IsEnabled:true}),"disabled lock stops its idle timer");
            window.HideToBackground(); Check(!vm.IsLocked,"default disabled lock permits background mode");
        }
        finally { window.Close(); }
        await VerifyPasswordHintSettings(output);
        await VerifyHintConfigurationFailure();
        await VerifyShortPasswordDialog(output);
        await VerifyEnableRace();
        await VerifyCorruptUi(output);
        await VerifyBackgroundDelay();
        VerifyForgotPasswordModel();
        VerifyResetImportBoundary(output);
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {
            Passed=true,CheckCount=Checks.Count,Checks,SyntheticDemoOnly=true,RealSteamStarted=false,
            RealSecurityConfigurationRead=false,UserAccountsChanged=false,PasswordLogged=false,
            Kdf="PBKDF2-HMAC-SHA256",Iterations=AppLockService.PasswordIterations,PasswordMinimumLength=AppLockService.MinimumPasswordLength,
            Screenshots=new[]{"01-startup-locked.png","02-security-settings.png","03-password-dialog.png","04-damaged-config-recovery.png","05-forgot-password-warning.png","06-password-error.png","07-change-password-hint.png","08-short-workarea-hint.png"}
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"PASS {Checks.Count} application-lock service/UI checks; synthetic demo only, no real security configuration or Steam changes");
    }

    private static async Task VerifyPasswordHintSettings(string output)
    {
        var storage=new TestStorage();var service=new AppLockService(storage);Check(service.Enable(Password,out _,PasswordHint),"hint-management fixture uses only synthetic security storage");
        using var vm=new MainViewModel(true,false,service,new TestTimeProvider());
        int requests=0;AppLockSettingsAction? requestedAction=null;
        vm.AppLockSettingsRequested+=(_,e)=> {requests++;requestedAction=e.Action;};
        Check(!vm.ChangePasswordHintCommand.CanExecute(null),"hint-editing command is unavailable while locked");
        Invoke(vm,"RequestAppLockSettings",AppLockSettingsAction.ChangeHint);
        Check(requests==0,"direct hint-settings requests cannot bypass the locked center guard");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"ignored","different",ChangedHint)&&service.PasswordHint==PasswordHint,
            "direct hint changes are rejected while locked before touching security storage");
        Check(await vm.TryUnlockAsync(Password),"hint-management fixture unlocks with its existing password");
        Check(vm.ChangePasswordHintCommand.CanExecute(null),"unlocked enabled lock exposes the hint-editing command");
        vm.ChangePasswordHintCommand.Execute(null);
        Check(requests==1&&requestedAction==AppLockSettingsAction.ChangeHint&&!vm.ShowPasswordHint,"hint-editing command requests its dedicated dialog without revealing a saved hint as an error");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,"wrong-current","ignored","different",ChangedHint)&&vm.ShowPasswordHint&&vm.CurrentPasswordHint==PasswordHint,
            "changing a hint requires the current password and displays the original hint after a wrong password");
        foreach(var action in new[]{AppLockSettingsAction.ChangePassword,AppLockSettingsAction.ChangeHint,AppLockSettingsAction.Disable})
            Check(!await vm.ConfigureAppLockAsync(action,"",NewPassword,NewPassword,ChangedHint)&&vm.AppLockMessage=="请输入当前密码。"&&!vm.ShowPasswordHint&&!vm.AppLockChecking&&vm.CurrentPasswordHint==PasswordHint,
                "empty current password shows an input prompt without revealing or changing the saved hint for "+action);
        var changes=new List<string>();System.ComponentModel.PropertyChangedEventHandler changed=(_,e)=> {if(e.PropertyName is not null)changes.Add(e.PropertyName);};vm.PropertyChanged+=changed;
        Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"ignored","different",ChangedHint)&&vm.CurrentPasswordHint==ChangedHint&&!vm.ShowPasswordHint,
            "verified hint-only editing ignores hidden new/repeat fields and saves the new hint");
        Check(new[]{nameof(MainViewModel.CurrentPasswordHint),nameof(MainViewModel.HasPasswordHint),nameof(MainViewModel.ShowPasswordHint)}.All(changes.Contains),
            "hint saves notify both the saved text and all hint-visibility bindings");vm.PropertyChanged-=changed;
        Check(new AppLockService(storage).PasswordHint==ChangedHint,"a saved user hint survives an isolated service restart");
        var dialog=new AppLockDialog(vm,AppLockSettingsAction.ChangeHint) {ShowActivated=false};
        try
        {
            dialog.Show();await Idle();
            var input=(TextBox)dialog.FindName("PasswordHintBox");
            Check(((TextBlock)dialog.FindName("DialogTitle")).Text=="修改密码提示"&&((StackPanel)dialog.FindName("CurrentPasswordSection")).IsVisible&&
                ((StackPanel)dialog.FindName("NewPasswordSection")).Visibility==Visibility.Collapsed&&input.Text==ChangedHint,"hint-only dialog shows the current password and saved hint with new/repeat password fields hidden");
            input.Text="尚未保存的新线索";((PasswordBox)dialog.FindName("CurrentPasswordBox")).Password="wrong-current";
            Invoke(dialog,"SubmitClick",dialog.FindName("SubmitButton"),new RoutedEventArgs());
            var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(5);while(vm.AppLockChecking&&DateTime.UtcNow<deadline)await Task.Delay(20);await Idle();
            Check(vm.ShowPasswordHint&&((Border)dialog.FindName("SavedPasswordHintPanel")).IsVisible&&((TextBlock)dialog.FindName("SavedPasswordHintText")).Text==ChangedHint&&input.Text=="尚未保存的新线索",
                "wrong current password shows the saved original hint while preserving the editable unsaved draft");
            Save(dialog,Path.Combine(output,"07-change-password-hint.png"));
        }
        finally {dialog.Close();}
        Check(((TextBox)dialog.FindName("PasswordHintBox")).Text.Length==0&&((PasswordBox)dialog.FindName("CurrentPasswordBox")).Password.Length==0,
            "closing the hint-only dialog clears its draft and current password");
        vm.RequestAppLock();Check(!vm.ShowPasswordHint&&vm.IsLocked,"relocking hides a hint revealed by a previous failed settings verification");
        Check(!await vm.TryUnlockAsync("wrong-current")&&vm.ShowPasswordHint,"wrong unlock reveals the updated saved hint");
        Check(!await vm.TryUnlockAsync("")&&!vm.ShowPasswordHint,"empty input after a wrong password hides the hint again");
        Check(await vm.TryUnlockAsync(Password)&&!vm.ShowPasswordHint,"hint editing does not change the unlock password");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","",new string('x',81))&&!vm.ShowPasswordHint&&vm.AppLockMessage.Contains("80",StringComparison.Ordinal),
            "overlength hints show their validation message without displaying a wrong-password hint");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","","first\nsecond")&&!vm.ShowPasswordHint,
            "hint-only editing rejects multiline hints through the VM service route");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","","my password is "+Password)&&!vm.ShowPasswordHint,
            "hint-only editing refuses a hint containing the full current password");
        storage.FailWrites=true;
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","","保存失败的提示")&&vm.CurrentPasswordHint==ChangedHint&&!vm.ShowPasswordHint,
            "failed hint persistence keeps the original hint and does not imply a wrong password");storage.FailWrites=false;
        Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","","")&&!vm.HasPasswordHint&&!vm.ShowPasswordHint,
            "verified empty hint clears the saved user hint");
        vm.RequestAppLock();Check(!await vm.TryUnlockAsync("wrong-current")&&!vm.ShowPasswordHint&&!vm.HasPasswordHint,"an unset hint never becomes visible after a wrong unlock");
        Check(await vm.TryUnlockAsync(Password)&&await vm.ConfigureAppLockAsync(AppLockSettingsAction.Disable,Password,"",""),"hint-management fixture disables only after current-password verification");
        Check(!vm.ChangePasswordHintCommand.CanExecute(null),"hint-only command is unavailable when app lock is disabled");
        Invoke(vm,"RequestAppLockSettings",AppLockSettingsAction.ChangeHint);Check(requests==1,"direct hint dialog request is also refused after disabling the lock");
        var enableDialog=new AppLockDialog(vm,AppLockSettingsAction.Enable) {ShowActivated=false};
        try {enableDialog.Show();await Idle();Check(((TextBox)enableDialog.FindName("PasswordHintBox")).Text.Length==0,"first password setup starts with an empty optional hint");}
        finally {enableDialog.Close();}
        Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.Enable,"",Password,Password,PasswordHint)&&vm.CurrentPasswordHint==PasswordHint&&!vm.ShowPasswordHint,
            "first enable accepts the optional hint without displaying it as an error");
    }

    private static async Task VerifyHintConfigurationFailure()
    {
        foreach(bool denyRead in new[]{false,true})
        {
            var storage=new MissingAfterWriteStorage {DenyReadsAfterFailure=denyRead};var service=new AppLockService(storage);
            Check(service.Enable(Password,out _,PasswordHint),"configuration-failure fixture starts with an isolated valid lock, read denial="+denyRead);
            using var vm=new MainViewModel(true,false,service);
            Check(await vm.TryUnlockAsync(Password),"configuration-failure fixture is initially unlocked, read denial="+denyRead);
            var account=vm.AllAccounts.First(a=>a.HasCachedLogin);vm.SelectedAccount=account;
            Check(vm.OnlineCommand.CanExecute(null),"account switch is available before the simulated security-file failure, read denial="+denyRead);
            await vm.SelectAndSwitchAsync(account.SteamId,SwitchMode.Online);
            Check(vm.PendingConfirmation,"fixture holds a normal switch confirmation before security failure, read denial="+denyRead);
            int accountCount=vm.AllAccounts.Count,activityCount=vm.Activity.Count;storage.FailAfterWrite=true;
            Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,Password,"","",ChangedHint),"hint write failure is returned to its caller, read denial="+denyRead);
            Check(vm.AppLockConfigurationError&&vm.IsLocked&&!vm.AppLockChecking&&!service.IsAuthenticated&&!vm.ShowPasswordHint,
                "missing or unreadable security configuration immediately locks the VM and clears hint/authentication state, read denial="+denyRead);
            Check(!vm.PendingConfirmation&&Field(vm,"_confirmation") is null,"security-configuration failure cancels the old switch confirmation immediately, read denial="+denyRead);
            var commands=typeof(MainViewModel).GetProperties().Where(p=>typeof(ICommand).IsAssignableFrom(p.PropertyType)).Select(p=>(ICommand)p.GetValue(vm)!);
            Check(commands.All(command=>!command.CanExecute(null)),"all exposed commands are disabled after security-configuration failure, read denial="+denyRead);
            string draft=vm.NoteDraft;vm.NoteDraft="blocked after security failure";await vm.SelectAndSwitchAsync(account.SteamId,SwitchMode.Offline);
            Invoke(vm,"OpenEditAccount");Invoke(vm,"SaveNewAccount");Invoke(vm,"SaveFolder");vm.ConfirmCommand.Execute(null);
            Check(!vm.EnsureUnlocked()&&vm.NoteDraft==draft&&vm.AllAccounts.Count==accountCount&&vm.Activity.Count==activityCount&&!vm.PendingConfirmation&&!vm.IsBusy&&!vm.HasManagementDialog,
                "direct note/management/switch/confirmation paths stay blocked after the configuration failure, read denial="+denyRead);
        }
    }

    private static async Task VerifyShortPasswordDialog(string output)
    {
        var service=new AppLockService(new TestStorage());Check(service.Enable(Password,out _,new string('线',80)),"short-workarea dialog fixture uses the longest allowed synthetic hint");
        using var vm=new MainViewModel(true,false,service);Check(await vm.TryUnlockAsync(Password),"short-workarea fixture starts unlocked");
        var dialog=new AppLockDialog(vm,AppLockSettingsAction.ChangePassword) {ShowActivated=false};
        try
        {
            dialog.Show();await Idle();Check(double.IsFinite(dialog.MaxHeight)&&dialog.MaxHeight>0,"password dialog limits its height to the monitor work area");
            dialog.MaxHeight=460;
            Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangePassword,"wrong-current",NewPassword,NewPassword)&&vm.ShowPasswordHint,
                "short-workarea fixture displays the full eighty-character saved hint after a wrong password");await Idle();
            var scroll=(ScrollViewer)dialog.FindName("DialogContentScroll");var submit=(Button)dialog.FindName("SubmitButton");
            var buttonOrigin=submit.TransformToAncestor(dialog).Transform(new Point());
            Check(dialog.ActualHeight<=461&&scroll.ScrollableHeight>0,"short work area caps the window and scrolls overflowing password/hint content");
            Check(submit.IsVisible&&buttonOrigin.Y>=0&&buttonOrigin.Y+submit.ActualHeight<=dialog.ActualHeight,
                "save button stays completely inside the short password dialog independently of scrolling");
            scroll.ScrollToEnd();await Idle();
            var hint=(Border)dialog.FindName("SavedPasswordHintPanel");var hintOrigin=hint.TransformToAncestor(scroll).Transform(new Point());
            Check(hint.IsVisible&&hintOrigin.Y>=-1&&hintOrigin.Y+hint.ActualHeight<=scroll.ActualHeight+1,
                "scrolling reaches the entire saved long hint on a short work area");
            Save(dialog,Path.Combine(output,"08-short-workarea-hint.png"));
            var current=(PasswordBox)dialog.FindName("CurrentPasswordBox");current.BringIntoView();await Idle();
            var currentOrigin=current.TransformToAncestor(scroll).Transform(new Point());
            Check(currentOrigin.Y>=-1&&currentOrigin.Y+current.ActualHeight<=scroll.ActualHeight+1,
                "scrolling also keeps the current-password field reachable without moving the save button");
        }
        finally {dialog.Close();}
    }

    private static void VerifyService(string fixtureRoot)
    {
        var storage=new TestStorage(); var protector=new InspectProtector(); var service=new AppLockService(storage,protector);
        Check(!service.Enabled&&!service.ConfigurationError,"missing isolated configuration defaults to disabled");
        Check(!service.Enable("short",out _),"password shorter than eight is rejected");
        Check(service.Enable(Password,out _),"service enables a valid password");
        string originalSalt=JsonDocument.Parse(protector.LastPlaintext!).RootElement.GetProperty("Salt").GetString()!;
        Check(!Encoding.UTF8.GetString(protector.LastPlaintext!).Contains(Password,StringComparison.Ordinal),"configuration contains no plaintext password");
        Check(JsonDocument.Parse(protector.LastPlaintext!).RootElement.GetProperty("Iterations").GetInt32()==600_000,"PBKDF2 uses 600000 SHA256 iterations");
        Check(service.Verify(Password,out _)&&!service.Verify("incorrect",out _),"service validates correct and rejects incorrect passwords");
        var restarted=new AppLockService(storage,protector);
        Check(restarted.Enabled&&restarted.Verify(Password,out _),"enabled verifier survives an isolated restart");
        Check(!restarted.ChangePassword("incorrect",NewPassword,out _)&&!restarted.Disable("incorrect",out _),"change and disable both require current password");
        Check(restarted.ChangePassword(Password,NewPassword,out _),"service changes password after verification");
        string newSalt=JsonDocument.Parse(protector.LastPlaintext!).RootElement.GetProperty("Salt").GetString()!;
        Check(originalSalt!=newSalt,"password change generates a fresh random salt");
        Check(!restarted.Verify(Password,out _)&&restarted.Verify(NewPassword,out _),"changed verifier invalidates old password");
        storage.FailWrites=true;
        Check(!restarted.Disable(NewPassword,out _)&&restarted.Enabled&&restarted.Verify(NewPassword,out _),"atomic-save failure preserves enabled state and old verifier");
        storage.FailWrites=false;
        Check(restarted.Disable(NewPassword,out _)&&!new AppLockService(storage,protector).Enabled,"verified disable persists an explicit disabled state");
        storage.Payload="GOL1broken"u8.ToArray(); var corrupt=new AppLockService(storage);
        Check(corrupt.Enabled&&corrupt.ConfigurationError&&!corrupt.Verify(Password,out _),"damaged configuration fails closed");
        Check(!corrupt.Enable(Password,out _)&&!corrupt.Disable(Password,out _),"damaged lock cannot be silently enabled over or disabled");
        var unreadable=new AppLockService(new ThrowingReadStorage());
        Check(unreadable.Enabled&&unreadable.ConfigurationError,"unreadable lock configuration fails closed");
        string path=Path.Combine(fixtureRoot,"synthetic.golock");
        File.Delete(path); // This fixture is created only by this harness, so reruns remain isolated and deterministic.
        var realStorage=new AppLockFileStorage(path); var native=new AppLockService(realStorage);
        Check(native.Enable(Password,out _),"isolated file is atomically protected with Windows DPAPI");
        Check(new AppLockService(realStorage).Verify(Password,out _),"Windows DPAPI isolated roundtrip works");
        Check(File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.UTF8.GetBytes(Password))<0,"protected file contains no plaintext password bytes");
        using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var lockedFile=new AppLockService(realStorage);
            Check(lockedFile.Enabled&&lockedFile.ConfigurationError,"sharing-denied file is not treated as missing");
        }
        File.WriteAllBytes(path,new byte[64*1024+1]); var oversize=new AppLockService(realStorage);
        Check(oversize.Enabled&&oversize.ConfigurationError,"oversized configuration is rejected before reading its body");
        var nonexistent=new AppLockService(new AppLockFileStorage(Path.Combine(fixtureRoot,"not-present","security.golock")));
        Check(!nonexistent.Enabled&&!nonexistent.ConfigurationError,"only genuinely missing files are unconfigured");
    }
    private static async Task VerifyEnableRace()
    {
        var store=new BlockingStorage(); var service=new AppLockService(store);
        using var vm=new MainViewModel(true,false,service);
        var enabling=vm.ConfigureAppLockAsync(AppLockSettingsAction.Enable,"",Password,Password);
        Check(await Task.Run(()=>store.Started.Wait(TimeSpan.FromSeconds(5))),"enable save can be isolated in flight");
        Check(vm.AppLockChecking&&!vm.AppLockEnabled,"first enable is checking before the atomic save completes");
        vm.RequestAppLock("windows-session"); store.Continue.Set();
        Check(await enabling&&vm.AppLockEnabled&&vm.IsLocked,"a session/background lock during first enable is preserved and applied after success");
        var failedStore=new BlockingStorage {FailWrites=true};
        using var failedVm=new MainViewModel(true,false,new AppLockService(failedStore));
        var failing=failedVm.ConfigureAppLockAsync(AppLockSettingsAction.Enable,"",Password,Password);
        Check(await Task.Run(()=>failedStore.Started.Wait(TimeSpan.FromSeconds(5))),"failed first enable save can be held in flight");
        failedVm.RequestAppLock("background"); failedStore.Continue.Set();
        Check(!await failing&&!failedVm.AppLockEnabled&&!failedVm.IsLocked&&Field(failedVm,"_lockPending") is false,
            "failed first enable clears pending lock without inventing an enabled configuration");
        Check(!(Field(failedVm,"_appLockTimer") is DispatcherTimer {IsEnabled:true}),"failed first enable leaves idle timer stopped");
        var reopenedStore=new BlockingStorage();using var reopenedVm=new MainViewModel(true,false,new AppLockService(reopenedStore));
        reopenedVm.AppLockDelayMinutes=0;
        var reopenedEnable=reopenedVm.ConfigureAppLockAsync(AppLockSettingsAction.Enable,"",Password,Password);
        Check(await Task.Run(()=>reopenedStore.Started.Wait(TimeSpan.FromSeconds(5))),"first-enable immediate-background fixture is checking in flight");
        reopenedVm.BeginBackgroundLockCountdown();reopenedVm.CancelBackgroundLockCountdown();reopenedStore.Continue.Set();
        Check(await reopenedEnable&&reopenedVm.AppLockEnabled&&!reopenedVm.IsLocked,
            "reopening cancels only a pending immediate-background request during first enable");
        var sessionStore=new BlockingStorage();using var sessionVm=new MainViewModel(true,false,new AppLockService(sessionStore));
        sessionVm.AppLockDelayMinutes=0;
        var sessionEnable=sessionVm.ConfigureAppLockAsync(AppLockSettingsAction.Enable,"",Password,Password);
        Check(await Task.Run(()=>sessionStore.Started.Wait(TimeSpan.FromSeconds(5))),"first-enable session-priority fixture is checking in flight");
        sessionVm.BeginBackgroundLockCountdown();sessionVm.RequestAppLock("windows-session");sessionVm.CancelBackgroundLockCountdown();sessionStore.Continue.Set();
        Check(await sessionEnable&&sessionVm.IsLocked,"reopening cannot cancel a Windows session request queued during first enable");
    }
    private static async Task VerifyCorruptUi(string output)
    {
        var storage=new TestStorage {Payload="GOL1broken"u8.ToArray()};
        var window=new MainWindow(true,false,false,false,new AppLockService(storage)) {ShowActivated=false};
        try
        {
            window.Show(); await Idle();
            var vm=(MainViewModel)window.DataContext;
            Check(vm.IsLocked&&vm.AppLockConfigurationError&&((Grid)window.FindName("LockOverlay")).IsVisible,
                "damaged configuration starts with an opaque locked recovery screen");
            Check(!((PasswordBox)window.FindName("UnlockPassword")).IsVisible,
                "damaged configuration does not suggest a password can silently clear it");
            Check(vm.AppLockMessage.Contains("重置",StringComparison.Ordinal)&&vm.AppLockMessage.Contains("清空",StringComparison.Ordinal),
                "damaged configuration explains destructive reset rather than promising password bypass");
            Check(!await vm.TryUnlockAsync(Password)&&vm.IsLocked,"damaged configuration cannot be unlocked through UI verification");
            Check(!vm.ShowPasswordHint&&!vm.HasPasswordHint,"damaged configuration does not reveal a hint or confuse recovery with a wrong password");
            Save(window,Path.Combine(output,"04-damaged-config-recovery.png"));
            var forgot=new ForgotPasswordDialog(vm){Owner=window,ShowActivated=false};
            try
            {
                forgot.Show();await Idle();Save(forgot,Path.Combine(output,"05-forgot-password-warning.png"));
                Check(forgot.FlowForVerification.Phase==ForgotPasswordPhase.Confirmation,"forgot dialog opens before any destructive countdown");
                forgot.FlowForVerification.ConfirmationText=ForgotPasswordFlow.RequiredConfirmation;
                Check(forgot.FlowForVerification.StartCooling(),"warning dialog begins cooling only after exact phrase");
                forgot.CancelHold();Check(forgot.FlowForVerification.IsCancelled,"cancel closes warning and invalidates its entire consent flow");
            }
            finally{if(forgot.IsVisible)forgot.Close();}
        }
        finally { window.Close(); }
    }
    private static async Task VerifyBackgroundDelay()
    {
        var clock=new TestTimeProvider();var service=new AppLockService(new TestStorage());Check(service.Enable(Password,out _),"delay fixture is isolated and enabled");
        using var vm=new MainViewModel(true,false,service,clock);
        Check(await vm.TryUnlockAsync(Password)&&vm.AppLockDelayMinutes==5,"default background delay is five minutes");
        Check(vm.AppLockDelayOptions.Select(o=>o.Minutes).SequenceEqual(new[]{0,1,3,5,10}),"delay options are immediate, 1, 3, 5 and 10 minutes");
        vm.AppLockDelayMinutes=2;Check(vm.AppLockDelayMinutes==5,"unsupported delay value cannot be saved");
        foreach(int minutes in new[]{0,1,3,5,10})
        {
            vm.CancelBackgroundLockCountdown();vm.AppLockDelayMinutes=minutes;
            clock.Advance(TimeSpan.FromMinutes(30));vm.CheckAppLockIdle();Check(!vm.IsLocked,"foreground remains unlocked with delay "+minutes);
            vm.BeginBackgroundLockCountdown();
            if(minutes==0)Check(vm.IsLocked,"immediate background delay locks on hide");
            else
            {
                clock.Advance(TimeSpan.FromMinutes(minutes)-TimeSpan.FromMilliseconds(1));vm.CheckAppLockIdle();Check(!vm.IsLocked,"delay "+minutes+" does not lock before deadline");
                clock.Advance(TimeSpan.FromMilliseconds(1));vm.CheckAppLockIdle();Check(vm.IsLocked,"delay "+minutes+" locks exactly at deadline");
            }
            Check(await vm.TryUnlockAsync(Password),"delay "+minutes+" lock can be unlocked");vm.CancelBackgroundLockCountdown();
        }
        vm.AppLockDelayMinutes=1;vm.BeginBackgroundLockCountdown();clock.Advance(TimeSpan.FromSeconds(59));vm.RecordAppActivity();
        clock.Advance(TimeSpan.FromSeconds(59));vm.CheckAppLockIdle();Check(!vm.IsLocked,"widget/quick activity resets the background deadline");
        vm.CancelBackgroundLockCountdown();clock.Advance(TimeSpan.FromMinutes(30));vm.CheckAppLockIdle();Check(!vm.IsLocked&&!vm.IsBackgroundLockCountdownActive,"reopening main window cancels pending delayed locking");
        vm.RequestAppLock("windows-session");Check(vm.IsLocked,"Windows session lock is immediate regardless of selected delay");
    }
    private static void VerifyForgotPasswordModel()
    {
        foreach(string text in new[]{"", "我已确认", " 我已经确认", "我已经确认 ", "我已经确认\n"})
        {var bad=new ForgotPasswordFlow(new TestTimeProvider()){ConfirmationText=text};Check(!bad.StartCooling(),"non-exact confirmation phrase is rejected: length "+text.Length);}
        var clock=new TestTimeProvider();var flow=new ForgotPasswordFlow(clock){ConfirmationText=ForgotPasswordFlow.RequiredConfirmation};
        Check(flow.StartCooling()&&flow.CoolingSecondsRemaining==15,"exact phrase starts a full fifteen-second cooling interval");
        clock.Advance(TimeSpan.FromMilliseconds(14999));flow.Tick();Check(!flow.BeginHold()&&flow.CompleteHold() is null,"14999ms cannot skip cooling or grant reset authorization");
        clock.Advance(TimeSpan.FromMilliseconds(1));flow.Tick();Check(flow.Phase==ForgotPasswordPhase.ReadyToHold,"fifteen seconds only makes the hold button ready");
        clock.Advance(TimeSpan.FromHours(1));flow.Tick();Check(flow.CompleteHold() is null&&flow.Phase==ForgotPasswordPhase.ReadyToHold,"an expired countdown never automatically authorizes deletion");
        Check(flow.BeginHold(),"ready flow begins a deliberate continuous hold");clock.Advance(TimeSpan.FromMilliseconds(2999));
        Check(flow.CompleteHold() is null,"2999ms hold cannot grant reset authorization");clock.Advance(TimeSpan.FromMilliseconds(1));var consent=flow.CompleteHold();
        Check(consent is not null&&flow.CompleteHold() is null,"three-second hold produces one authorization only");
        Check((bool)Invoke(consent!,"TryConsume")!&&!(bool)Invoke(consent!,"TryConsume")!,"reset authorization is single use");
        foreach(string reason in new[]{"release","focus loss","Escape","cancel","pointer leaves button"})
        {
            var localClock=new TestTimeProvider();var cancelled=new ForgotPasswordFlow(localClock){ConfirmationText=ForgotPasswordFlow.RequiredConfirmation};
            cancelled.StartCooling();localClock.Advance(TimeSpan.FromSeconds(15));cancelled.Tick();cancelled.BeginHold();localClock.Advance(TimeSpan.FromSeconds(2));cancelled.Cancel();
            localClock.Advance(TimeSpan.FromSeconds(10));Check(cancelled.IsCancelled&&cancelled.CompleteHold() is null&&!cancelled.BeginHold(),reason+" cancels the whole reset flow");
        }
        var revokedClock=new TestTimeProvider();var revoked=new ForgotPasswordFlow(revokedClock){ConfirmationText=ForgotPasswordFlow.RequiredConfirmation};
        revoked.StartCooling();revokedClock.Advance(TimeSpan.FromSeconds(15));revoked.Tick();revoked.BeginHold();revokedClock.Advance(TimeSpan.FromSeconds(3));var revokedConsent=revoked.CompleteHold()!;
        revoked.Cancel();Check(!(bool)Invoke(revokedConsent,"TryConsume")!,"cancelling before the backend begins revokes an already-issued authorization");
    }
    private static void VerifyResetImportBoundary(string output)
    {
        string root=Path.Combine(output,"synthetic-public-preview-boundary");
        var context=GameorderDataContext.CreateTest(root);
        Check(context.IsResetTest&&context.GetPublicAccountPreview().Count==3,"public-preview boundary uses only authored isolated account identities");
        string marker=Path.Combine(root,"manual-import-required.marker");
        File.WriteAllText(marker,"MANUAL IMPORT REQUIRED AFTER DESTRUCTIVE RESET");
        using(var suppressed=new MainViewModel(true,false,dataContext:context))
            Check(suppressed.IsLocked&&suppressed.AllAccounts.Count==0,"reset marker prevents locked cold-start identity previews from reappearing");
        context.MarkManualImportCompleted();
        Check(!context.SuppressAutomaticImport&&!File.Exists(marker),"explicit completed import clears only its own suppression marker");
        using(var restored=new MainViewModel(true,false,dataContext:context))
            Check(restored.IsLocked&&restored.AllAccounts.Count==3&&restored.AllAccounts.All(a=>!a.HasNote),
                "after explicit import only public identities return before unlock, with no private notes");
    }
    private static async Task VerifyPasswordReveal(AppLockDialog dialog,MainViewModel vm,string output)
    {
        foreach(string prefix in new[]{"Current","New","Repeat"})
        {
            var masked=(PasswordBox)dialog.FindName(prefix+"PasswordBox");var plain=(TextBox)dialog.FindName(prefix+"PasswordPlain");var eye=(ToggleButton)dialog.FindName(prefix+"PasswordReveal");
            Check(plain.MaxLength==4096&&plain.Text.Length==0&&plain.Visibility==Visibility.Collapsed&&eye.IsChecked!=true,prefix+" password starts masked with an empty bounded plain editor");
            masked.Password="synthetic-visible-password";eye.IsChecked=true;await Idle();
            Check(dialog.HasRevealedPassword&&plain.IsVisible&&plain.Text==masked.Password&&!masked.IsVisible,prefix+" eye reveals the existing password only in its paired editor");
            plain.Text="edited-synthetic-password";
            Check(masked.Password==plain.Text,prefix+" visible edits synchronize immediately back to PasswordBox");
            eye.IsChecked=false;await Idle();
            Check(plain.Text.Length==0&&!plain.IsVisible&&!plain.CanUndo&&masked.IsVisible&&masked.Password=="edited-synthetic-password",prefix+" hiding clears plaintext and undo history while preserving the edited masked password");
            eye.IsChecked=true;await Idle();((Button)dialog.FindName("SubmitButton")).Focus();await Idle();
            Check(plain.Text.Length==0&&eye.IsChecked!=true,prefix+" losing focus hides and clears the plain editor");
        }
        var current=(PasswordBox)dialog.FindName("CurrentPasswordBox");var next=(PasswordBox)dialog.FindName("NewPasswordBox");var repeat=(PasswordBox)dialog.FindName("RepeatPasswordBox");
        current.Password="incorrect-current-password";next.Password=NewPassword;repeat.Password=NewPassword;
        ((ToggleButton)dialog.FindName("CurrentPasswordReveal")).IsChecked=true;await Idle();
        Invoke(dialog,"SubmitClick",dialog.FindName("SubmitButton"),new RoutedEventArgs());
        Check(!dialog.HasRevealedPassword&&new[]{"Current","New","Repeat"}.All(prefix=>((TextBox)dialog.FindName(prefix+"PasswordPlain")).Text.Length==0),
            "submitting hides and clears all plain editors before asynchronous password verification");
        var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(5);
        while(vm.AppLockChecking&&DateTime.UtcNow<deadline)await Task.Delay(20);
        await Idle();
        Check(!vm.AppLockChecking&&current.Password.Length==0&&next.Password.Length==0&&repeat.Password.Length==0,
            "failed password submit also clears every PasswordBox after verification");
        Check(vm.HasAppLockMessage&&vm.AppLockMessage=="密码错误，请重新输入。"&&((Border)dialog.FindName("AppLockErrorPanel")).IsVisible&&
            ((TextBlock)dialog.FindName("ErrorMessage")).FontSize==14,"wrong current password displays the visible fourteen-point inline error panel");
        Check(vm.ShowPasswordHint&&((Border)dialog.FindName("SavedPasswordHintPanel")).IsVisible&&((TextBlock)dialog.FindName("SavedPasswordHintText")).Text==PasswordHint,
            "wrong current password shows the user's original saved hint inside the settings dialog");
        Save(dialog,Path.Combine(output,"06-password-error.png"));
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangePassword,Password,NewPassword,"different-confirmation")&&vm.HasAppLockMessage&&
            vm.AppLockMessage=="两次密码不一致。","confirmation mismatch keeps its specific visible error");
        Check(!vm.ShowPasswordHint,"confirmation mismatch is not presented as a wrong current password");
        Check(!await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangePassword,Password,"short","short")&&vm.HasAppLockMessage&&
            vm.AppLockMessage.Contains("8",StringComparison.Ordinal),"minimum password length keeps its specific visible error without weakening the rule");
        Check(!vm.ShowPasswordHint,"minimum-length validation does not show a wrong-password hint");
        current.Password="synthetic-visible-password";((ToggleButton)dialog.FindName("CurrentPasswordReveal")).IsChecked=true;await Idle();
        vm.RequestAppLock();await Idle();
        Check(!dialog.HasRevealedPassword&&current.Password.Length==0&&((TextBox)dialog.FindName("CurrentPasswordPlain")).Text.Length==0,
            "locking while a password is revealed hides and clears both controls");
        Check(await vm.TryUnlockAsync(Password),"reveal lifecycle fixture unlocks without changing its configured password");
        current.Password="synthetic-visible-password";((ToggleButton)dialog.FindName("CurrentPasswordReveal")).IsChecked=true;await Idle();
    }
    private sealed class TestStorage : IAppLockStorage
    {
        public byte[]? Payload;
        public bool FailWrites;
        public byte[]? Read()=>Payload?.ToArray();
        public void Write(byte[] bytes) { if(FailWrites) throw new IOException("Synthetic save failure"); Payload=bytes.ToArray(); }
    }
    private sealed class ThrowingReadStorage : IAppLockStorage
    {
        public byte[]? Read()=>throw new UnauthorizedAccessException("Synthetic read denial");
        public void Write(byte[] bytes)=>throw new IOException();
    }
    private sealed class MissingAfterWriteStorage : IAppLockStorage
    {
        private byte[]? _payload;
        private bool _failed;
        public bool FailAfterWrite,DenyReadsAfterFailure;
        public byte[]? Read() {if(_failed&&DenyReadsAfterFailure)throw new UnauthorizedAccessException("Synthetic security-file read denial");return _payload?.ToArray();}
        public void Write(byte[] bytes)
        {
            if(FailAfterWrite) {_payload=null;_failed=true;throw new IOException("Synthetic security-file loss during write");}
            _payload=bytes.ToArray();
        }
    }
    private sealed class BlockingStorage : IAppLockStorage
    {
        public readonly ManualResetEventSlim Started=new(),Continue=new();
        public bool FailWrites;
        private byte[]? _payload;
        public byte[]? Read()=>_payload?.ToArray();
        public void Write(byte[] bytes) { Started.Set(); if(!Continue.Wait(TimeSpan.FromSeconds(10))||FailWrites) throw new IOException("Synthetic blocking save failure"); _payload=bytes.ToArray(); }
    }
    private sealed class InspectProtector : IAppLockProtector
    {
        public byte[]? LastPlaintext;
        public byte[] Protect(byte[] bytes) { LastPlaintext=bytes.ToArray(); return bytes.Select(b=>(byte)(b^0x5A)).ToArray(); }
        public byte[] Unprotect(byte[] bytes)=>bytes.Select(b=>(byte)(b^0x5A)).ToArray();
    }
    private sealed class TestTimeProvider : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc=new(2026,10,7,0,0,0,TimeSpan.Zero);
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>_ticks;
        public override DateTimeOffset GetUtcNow()=>_utc;
        public void Advance(TimeSpan time) { _ticks+=time.Ticks; _utc+=time; }
    }
}
