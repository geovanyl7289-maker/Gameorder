using System.IO;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output=Path.GetFullPath(args.FirstOrDefault()??Path.Combine(AppContext.BaseDirectory,"verification"));
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(async()=>
        {
            try {await ResetUiVerification.RunAsync(output);app.Shutdown(0);}
            catch(Exception ex) {Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());app.Shutdown(2);}
        }));
        return app.Run();
    }
}
