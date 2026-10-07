using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Gameorder;

public sealed class AvatarImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string,(long Stamp,BitmapImage Image)> Cache=new(StringComparer.OrdinalIgnoreCase);
    // Drops strong cache references; visible views keep their own frozen bitmaps and remain intact.
    public static void TrimMemoryCache()=>Cache.Clear();
    public static int CachedImageCountForVerification=>Cache.Count;
    public static BitmapImage? Load(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var file=new FileInfo(path);
            if(!file.Exists || file.Length is <=0 or >2097152) return null;
            long stamp=file.LastWriteTimeUtc.Ticks;
            if(Cache.TryGetValue(path,out var cached)&&cached.Stamp==stamp) return cached.Image;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var image=Decode(stream);
            if(Cache.Count>256) Cache.Clear();
            Cache[path]=(stamp,image); return image;
        }
        catch(Exception) { return null; }
    }
    public static bool CanDecode(byte[] bytes)
    {
        if(bytes.Length is <=0 or >2097152) return false;
        try { using var stream=new MemoryStream(bytes,false); _=Decode(stream); return true; }
        catch(Exception) { return false; }
    }
    private static BitmapImage Decode(Stream stream)
    {
        var image=new BitmapImage();
        image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.DecodePixelWidth=160;
        image.StreamSource=stream; image.EndInit(); image.Freeze(); return image;
    }
    public object? Convert(object value,Type targetType,object parameter,CultureInfo culture)=>Load(value as string);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
