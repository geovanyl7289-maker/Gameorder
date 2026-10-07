using System.IO;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string output=Path.GetFullPath(args.FirstOrDefault()??Path.Combine(AppContext.BaseDirectory,"verification"));
        Directory.CreateDirectory(output);File.Delete(Path.Combine(output,"result.json"));
        File.Delete(Path.Combine(output,"failure.txt"));
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
        {
            try { await SecurityUiVerification.RunAsync(output); app.Shutdown(0); }
            catch(Exception ex) { Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString()); File.WriteAllText(Path.Combine(output,"result.json"),System.Text.Json.JsonSerializer.Serialize(new {Passed=false,Error=ex.Message}));Console.WriteLine(ex); app.Shutdown(2); }
        }));
        return app.Run();
    }
}
