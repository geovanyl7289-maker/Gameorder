using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Gameorder;

// Read-only proof for the dedicated test build. Destructive-flow tests use separate disposable fixtures.
internal static class ResetTestProof
{
#if RESET_TEST_BUILD
    private const bool ForcedTestBuild=true;
#else
    private const bool ForcedTestBuild=false;
#endif
    public static async Task RunAsync(MainWindow window,GameorderDataContext context,string output)
    {
        var vm=(MainViewModel)window.DataContext;
        if(!vm.IsResetTest||!vm.IsDemo||window.ShellIntegrationEnabled||window.TrayIconVisible||window.TaskbarWidgetForVerification is not null)
            throw new InvalidOperationException("测试模式隔离检查失败。");
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Save(window,Path.Combine(output,"test-startup.png"));
        if(window.ResetTestWidgetPreviewForVerification is not { } preview) throw new InvalidOperationException("缺少测试小组件预览。");
        Save(preview,Path.Combine(output,"test-widget.png"));
        var report=new {Passed=true,Version=AboutInfo.Version,ResetTest=true,ForcedTestBuild,Demo=true,ShellIntegration=false,
            NativeTaskbarHost=false,TrayIcon=false,AccountsPath=context.Store.StoragePath,
            LockEnabled=vm.AppLockEnabled,Locked=vm.IsLocked,PublicPreviewAccounts=vm.AllAccounts.Count,
            ShortTestPasswordLength=GameorderDataContext.TestPassword.Length,
            PasswordEyePresent=window.FindName("UnlockPasswordReveal") is System.Windows.Controls.Primitives.ToggleButton,
            VisiblePasswordCopyEmpty=window.FindName("UnlockPasswordPlain") is System.Windows.Controls.TextBox {Text.Length:0},
            RealSteamReadOrWrite=false,AvatarNetwork=false,DestructiveResetExecuted=false};
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));
    }
    private static void Save(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path);encoder.Save(stream);
    }
}
