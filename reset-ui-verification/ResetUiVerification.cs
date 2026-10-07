using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gameorder;

internal static class ResetUiVerification
{
    private static readonly List<string> Checks=[];
    private static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);Checks.Add(name);}
    private static async Task Idle() {await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);await Task.Delay(80);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    private static string Digest(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"result.json"),"{\"Passed\":false}");
        if(File.Exists(Path.Combine(output,"failure.txt")))File.Delete(Path.Combine(output,"failure.txt"));
        var fixture=Path.Combine(output,"isolated-reset-fixture-"+Guid.NewGuid().ToString("N"));
        var context=GameorderDataContext.CreateTest(fixture);
        var clock=new ManualTime();
        var window=new MainWindow(demo:true,allowAvatarNetwork:false,enableShellIntegration:false,persistTaskbarPosition:false,dataContext:context,appLockTimeProvider:clock) {ShowActivated=false};
        var vm=(MainViewModel)window.DataContext;
        try
        {
            window.Show();await Idle();
            Check(vm.IsResetTest&&vm.IsDemo&&!window.ShellIntegrationEnabled&&!window.TrayIconVisible&&window.TaskbarWidgetForVerification is null,"test mode never creates native taskbar or tray integration");
            Check(context.Store.StoragePath.StartsWith(fixture+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"persistent test accounts stay inside the isolated fixture");
            Check(vm.IsLocked&&vm.AppLockEnabled,"test fixture starts locked with its own app password");
            Check(GameorderDataContext.TestPassword=="12345678","test password uses the short published eight digits");
            var password=(PasswordBox)window.FindName("UnlockPassword");
            var plain=(TextBox)window.FindName("UnlockPasswordPlain");
            var eye=(ToggleButton)window.FindName("UnlockPasswordReveal");
            Check(!window.HasRevealedUnlockPassword&&plain.Visibility==Visibility.Collapsed&&plain.Text.Length==0,"unlock input starts masked without a visible text copy");
            password.Password=GameorderDataContext.TestPassword;eye.IsChecked=true;
            Check(window.HasRevealedUnlockPassword&&plain.Text==GameorderDataContext.TestPassword&&password.Visibility==Visibility.Collapsed,"unlock eye reveals only the supplied synthetic password");
            plain.Text="87654321";
            Check(password.Password=="87654321"&&!plain.IsUndoEnabled,"visible edits sync to masked input without retaining undo history");
            eye.IsChecked=false;
            Check(!window.HasRevealedUnlockPassword&&plain.Text.Length==0&&!plain.CanUndo&&password.Password=="87654321","hiding clears visible copy and retains actual entered password");
            eye.IsChecked=true;window.HideToBackground();window.ShowFromTray();await Idle();
            Check(!window.HasRevealedUnlockPassword&&plain.Text.Length==0,"background hide automatically masks an exposed unlock input");
            password.Clear();
            Check(vm.AllAccounts.Count>=3&&vm.AllAccounts.All(a=>!a.HasNote),"locked cold start exposes only synthetic public previews");
            var preview=window.ResetTestWidgetPreviewForVerification!;
            Check(preview is not null&&preview.IsVisible&&preview.WidgetForVerification.IsVisible,"locked widget preview remains visible");
            var widget=preview!.WidgetForVerification.ViewModel!;
            Check(widget.IsLocked&&widget.HasAccount&&!widget.Note.Contains("备注",StringComparison.Ordinal),"widget lock label replaces private notes");
            Save(window,Path.Combine(output,"01-test-locked.png"));Save(preview,Path.Combine(output,"02-widget-locked.png"));
            Check(!await vm.TryUnlockAsync("deliberately-wrong-password"),"wrong test password cannot unlock");
            password.Password=GameorderDataContext.TestPassword;eye.IsChecked=true;
            typeof(MainWindow).GetMethod("UnlockClick",Private)!.Invoke(window,[window,new RoutedEventArgs()]);
            Check(!window.HasRevealedUnlockPassword&&plain.Text.Length==0&&password.Password.Length==0,"unlock submission immediately hides and clears both inputs");
            for(int attempt=0;attempt<80&&(vm.IsLocked||vm.AppLockChecking);attempt++)await Task.Delay(100);
            Check(!vm.IsLocked,"short test password unlocks through the actual UI handler");
            await Idle();
            Check(vm.AllAccounts.Any(a=>a.HasNote)&&context.Store.IsPasswordProtected,"unlock loads notes from password encrypted data");
            var beforeHint=Digest(context.Store.StoragePath);
            Check(await vm.ConfigureAppLockAsync(AppLockSettingsAction.ChangeHint,GameorderDataContext.TestPassword,"","","仅本人能理解的开发验证线索"),"an authenticated user can add an optional password hint");
            Check(Digest(context.Store.StoragePath)==beforeHint&&vm.CurrentPasswordHint=="仅本人能理解的开发验证线索","hint-only update preserves all encrypted account data");
            var target=vm.AllAccounts.Where(a=>a.HasCachedLogin).Skip(1).First();var targetName=target.AccountName;var targetId=target.SteamId;
            vm.RequestAppLock();
            Check(!vm.ShowPasswordHint,"a new lock does not expose the saved hint before an incorrect password");
            Check(!await vm.TryUnlockAsync("wrong-hint-preview"),"an incorrect password leaves the app locked with its saved hint");
            await Idle();
            var hintBanner=(Border)window.FindName("PasswordHintBanner");
            var hintText=(TextBlock)window.FindName("PasswordHintText");
            Check(vm.ShowPasswordHint&&hintBanner.IsVisible&&hintText.Text==vm.CurrentPasswordHint,"the actual main lock screen displays the stored user hint after an incorrect password");
            window.Width=1000;window.Height=680;await Idle();
            var hintBounds=hintBanner.TransformToAncestor(window).TransformBounds(new Rect(new Point(0,0),hintBanner.RenderSize));
            Check(hintBounds.Bottom<=window.ActualHeight&&hintBounds.Top>=0,"the password hint fits in the smallest supported main window");
            Save(window,Path.Combine(output,"06-user-password-hint.png"));
            Check(await vm.TryUnlockAsync(GameorderDataContext.TestPassword)&&!vm.ShowPasswordHint,"a successful unlock hides the saved hint again");
            window.Width=1280;window.Height=820;await Idle();
            target=vm.AllAccounts.Single(a=>a.SteamId==targetId&&a.AccountName==targetName);
            vm.RequestAppLock();await window.RequestWidgetSwitchAsync(target,SwitchMode.Online);
            window.HideToBackground();window.ShowFromTray();
            Check(await vm.TryUnlockAsync(GameorderDataContext.TestPassword),"cancelled unlock can later unlock normally");await Idle();
            Check(!vm.PendingConfirmation,"closing the unlock window discards the old widget switch request");
            target=vm.AllAccounts.Single(a=>a.SteamId==targetId&&a.AccountName==targetName);
            vm.RequestAppLock();
            Check(vm.IsLocked&&preview.IsVisible&&widget.IsLocked,"locking does not hide the widget");
            await window.RequestWidgetSwitchAsync(target,SwitchMode.Offline);
            Check(!vm.PendingConfirmation&&!vm.IsBusy,"locked widget switch waits for password without executing Steam");
            Check(await vm.TryUnlockAsync(GameorderDataContext.TestPassword),"unlock accepts a pending widget request");await Idle();
            Check(vm.PendingConfirmation&&vm.SelectedAccount?.AccountName==targetName&&vm.SelectedAccount?.SteamId==targetId,"unlock resumes the exact account and offline mode into normal confirmation");
            Check(vm.ConfirmationTitle.Contains("离线",StringComparison.Ordinal),"resumed confirmation retains offline mode");
            vm.CancelConfirmCommand.Execute(null);window.CloseQuickAccounts();
            vm.AppLockDelayMinutes=1;window.HideToBackground();
            Check(vm.IsBackgroundLockCountdownActive&&!vm.IsLocked,"hiding starts delay without immediate lock");
            clock.Advance(TimeSpan.FromSeconds(59));vm.CheckAppLockIdle();Check(!vm.IsLocked,"background delay does not lock early");
            widget.RecordActivity();clock.Advance(TimeSpan.FromSeconds(59));vm.CheckAppLockIdle();Check(!vm.IsLocked,"widget activity restarts the background delay");
            clock.Advance(TimeSpan.FromSeconds(1));vm.CheckAppLockIdle();Check(vm.IsLocked,"delay locks at the chosen deadline");
            window.ShowFromTray();Check(await vm.TryUnlockAsync(GameorderDataContext.TestPassword),"delayed lock can unlock");
            window.HideToBackground();clock.Advance(TimeSpan.FromSeconds(10));window.ShowFromTray();
            clock.Advance(TimeSpan.FromMinutes(2));vm.CheckAppLockIdle();Check(!vm.IsLocked&&!vm.IsBackgroundLockCountdownActive,"reopening the main window cancels background countdown");
            vm.RequestAppLock("windows-session");Check(vm.IsLocked,"Windows session lock is immediate");await Idle();
            var dialog=new ForgotPasswordDialog(vm) {Owner=window};dialog.Show();await Idle();
            var input=(TextBox)dialog.FindName("ConfirmationBox");var next=(Button)dialog.FindName("ContinueButton");
            Check(!next.IsEnabled,"reset dialog starts with confirmation disabled");input.Text="我已经确认 ";await Idle();Check(!next.IsEnabled,"reset phrase must match exactly");
            input.Text=ForgotPasswordFlow.RequiredConfirmation;await Idle();Check(next.IsEnabled,"exact phrase enables only the cooling step");
            Save(dialog,Path.Combine(output,"03-reset-warning.png"));dialog.CancelHold();await Idle();
            var before=Digest(context.Store.StoragePath);var cancelled=vm.CreateForgotPasswordFlow();cancelled.ConfirmationText=ForgotPasswordFlow.RequiredConfirmation;
            Check(cancelled.StartCooling(),"confirmation begins monotonic cooling period");clock.Advance(TimeSpan.FromSeconds(15));cancelled.Tick();
            Check(cancelled.CanHold&&Digest(context.Store.StoragePath)==before,"cooling expiry alone never clears data");
            Check(cancelled.BeginHold(),"ready flow can begin a deliberate hold");clock.Advance(TimeSpan.FromMilliseconds(2999));
            Check(cancelled.CompleteHold() is null,"partial hold cannot authorize reset");cancelled.Cancel();clock.Advance(TimeSpan.FromSeconds(1));
            Check(cancelled.CompleteHold() is null&&Digest(context.Store.StoragePath)==before,"cancelled hold retains all encrypted data");
            var full=vm.CreateForgotPasswordFlow();full.ConfirmationText=ForgotPasswordFlow.RequiredConfirmation;full.StartCooling();clock.Advance(TimeSpan.FromSeconds(15));full.Tick();full.BeginHold();clock.Advance(TimeSpan.FromSeconds(3));
            var consent=full.CompleteHold();Check(consent is not null,"complete cooling and continuous hold produce one consent");
            Check(await vm.ResetLocalDataAsync(consent!),"authorized reset actually clears the isolated data");await Idle();
            Check(!vm.AppLockEnabled&&!vm.IsLocked&&vm.AllAccounts.Count==0&&vm.Activity.Count==0,"reset leaves an unlocked empty app without old accounts or history");
            Check(vm.CurrentPasswordHint.Length==0&&!vm.ShowPasswordHint&&!vm.HasPasswordHint,"destructive reset clears the user hint and its visible state");
            var empty=context.Store.Load();Check(empty.Accounts.Count==0&&empty.Folders.Count==0&&empty.Activity.Count==0&&!empty.AutoImportSteamAccounts,"empty persisted state explicitly disables automatic Steam import");
            Check(!await vm.ResetLocalDataAsync(consent!),"reset consent cannot be replayed");
            Save(window,Path.Combine(output,"04-reset-empty.png"));
            var reopened=GameorderDataContext.CreateTest(fixture);using(var restart=new MainViewModel(true,false,dataContext:reopened))
                Check(restart.AllAccounts.Count==0&&!restart.AppLockEnabled,"restarting an emptied test app does not regenerate the fixture");
            await (Task)typeof(MainViewModel).GetMethod("RestoreResetTestDataAsync",Private)!.Invoke(vm,null)!;await Idle();
            Check(vm.IsLocked&&vm.AppLockEnabled&&vm.AllAccounts.Count>=3,"rebuild recreates only virtual accounts and the test lock");
            Check(await vm.TryUnlockAsync(GameorderDataContext.TestPassword),"rebuilt fixture uses the published test password");
            Check(vm.AllAccounts.Any(a=>a.HasNote)&&vm.Activity.Count>0,"rebuild restores synthetic notes and operation records");
            vm.ShowSettingsCommand.Execute(null);await Idle();Save(window,Path.Combine(output,"05-delay-settings.png"));
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,CheckCount=Checks.Count,Checks,RealSteamReadOrWrite=false,RealAccountDataCopied=false,NativeShellModified=false,FixturePath=fixture},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally {typeof(MainWindow).GetField("_exitRequested",Private)!.SetValue(window,true);window.Close();}
    }
    private static void Save(Window window,string path)
    {
        window.UpdateLayout();var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>_ticks;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,7,0,0,0,TimeSpan.Zero)+TimeSpan.FromTicks(_ticks);
        public void Advance(TimeSpan duration)=>_ticks+=duration.Ticks;
    }
}
