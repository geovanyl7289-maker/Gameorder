using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;

namespace Gameorder;
internal static class ShellIdentity
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    public static void SetApplicationIdentity(bool verification)
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(verification?"Gameorder.ShellVerification":"Gameorder"));
        JumpList.SetJumpList(Application.Current,new JumpList {ShowFrequentCategory=false,ShowRecentCategory=false});
    }
}
