using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Gameorder;

/// <summary>A temporary, noninteractive drop preview. The actual widget remains a taskbar child.</summary>
internal sealed class TaskbarDockHint : IDisposable
{
    private readonly Window _window;
    private HwndSource? _source;
    private bool _disposed;
    public IntPtr Handle=>_source?.Handle??IntPtr.Zero;
    public bool IsVisible=>!_disposed&&_window.IsVisible&&Handle!=IntPtr.Zero;
    public Rect BoundsPixels { get; private set; }=Rect.Empty;
    public bool InputTransparent=>Handle!=IntPtr.Zero&&((long)GetWindowLongPtr(Handle,-20)&0x00000020)!=0;
    public bool NoActivate=>Handle!=IntPtr.Zero&&((long)GetWindowLongPtr(Handle,-20)&0x08000000)!=0;
    public bool NoTaskbarButton=>!_window.ShowInTaskbar&&Handle!=IntPtr.Zero&&((long)GetWindowLongPtr(Handle,-20)&0x00040000)==0;
    public TaskbarDockHint()
    {
        _window=new Window
        {
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,
            Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,Focusable=false,
            Topmost=true,IsHitTestVisible=false,Content=new Rectangle
            {
                RadiusX=11,RadiusY=11,Margin=new Thickness(2),StrokeThickness=1.5,
                Stroke=new SolidColorBrush(Color.FromArgb(210,153,148,255)),StrokeDashArray=new DoubleCollection([5,3]),
                Fill=new SolidColorBrush(Color.FromArgb(38,140,132,245)),IsHitTestVisible=false
            }
        };
        _window.SourceInitialized+=OnSourceInitialized;
    }
    private void OnSourceInitialized(object? sender,EventArgs e)
    {
        _source=HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        if(_source is null) return;
        long style=(long)GetWindowLongPtr(_source.Handle,-20);
        SetWindowLongPtr(_source.Handle,-20,new IntPtr((style|0x00000020|0x08000000|0x00000080)&~0x00040000L));
        _source.AddHook(Hook);
    }
    private static IntPtr Hook(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x84) { handled=true; return new IntPtr(-1); } // HTTRANSPARENT
        if(message==0x21) { handled=true; return new IntPtr(3); } // MA_NOACTIVATE
        return IntPtr.Zero;
    }
    public void ShowAt(Rect boundsPixels,double scale)
    {
        if(_disposed||boundsPixels.IsEmpty||boundsPixels.Width<=0||boundsPixels.Height<=0) return;
        _window.Width=boundsPixels.Width/scale; _window.Height=boundsPixels.Height/scale;
        _window.Left=boundsPixels.Left/scale; _window.Top=boundsPixels.Top/scale;
        _ = new WindowInteropHelper(_window).EnsureHandle();
        if(!_window.IsVisible) _window.Show();
        SetWindowPos(Handle,new IntPtr(-1),(int)Math.Round(boundsPixels.X),(int)Math.Round(boundsPixels.Y),
            (int)Math.Round(boundsPixels.Width),(int)Math.Round(boundsPixels.Height),0x0010|0x0040|0x0200);
        BoundsPixels=GetWindowRect(Handle,out var rect)?new Rect(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top):boundsPixels;
    }
    public void Hide() { if(!_disposed&&_window.IsVisible) _window.Hide(); BoundsPixels=Rect.Empty; }
    public bool SavePreview(string output)
    {
        if(!IsVisible||BoundsPixels.IsEmpty) return false;
        _window.UpdateLayout();
        var transform=_source?.CompositionTarget.TransformToDevice??Matrix.Identity;
        var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Round(BoundsPixels.Width)),Math.Max(1,(int)Math.Round(BoundsPixels.Height)),
            96*transform.M11,96*transform.M22,PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(output); encoder.Save(file); return true;
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        _window.SourceInitialized-=OnSourceInitialized;
        if(_source is not null&&!_source.IsDisposed) _source.RemoveHook(Hook);
        _window.Close(); _source=null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rectangle);
}
