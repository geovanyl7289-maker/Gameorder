using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Gameorder;

public sealed record TrayAccountEntry(string SteamId,string AccountName,string DisplayName,string PresenceLabel,
    bool IsActive,bool IsOnline,bool IsOffline,bool CanOnline,bool CanOffline,bool HasAvatar,string FolderName="未分组",string Note="");
public sealed record TrayMenuVerification(int AccountCount,int OnlineEntryCount,int OfflineEntryCount,int EnabledOfflineCount,int AvatarImageCount);

/// <summary>Shell shortcuts only request the view model's existing switch confirmation.</summary>
public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Drawing.Font _menuFont;
    private readonly Drawing.Font _captionFont;
    private readonly Drawing.Font _headingFont;
    private readonly GameorderMenuRenderer _renderer;
    private readonly List<Drawing.Image> _menuImages = [];
    private readonly HashSet<SteamAccount> _observedAccounts = [];
    private readonly INotifyCollectionChanged? _accountChanges;
    private bool _disposed;
    public bool IsVisible => !_disposed && _notifyIcon.Visible;

    public TrayService(MainViewModel viewModel,MainWindow window)
    {
        _viewModel = viewModel;
        _window = window;
        _icon = LoadApplicationIcon();
        _menuFont = new Drawing.Font("Microsoft YaHei UI",9f);
        _captionFont = new Drawing.Font("Microsoft YaHei UI",8.2f);
        _headingFont = new Drawing.Font("Microsoft YaHei UI",9.5f,Drawing.FontStyle.Bold);
        _renderer = new GameorderMenuRenderer(_captionFont,_headingFont);
        _menu = new Forms.ContextMenuStrip
        {
            Font = _menuFont,
            ImageScalingSize = new Drawing.Size(28,28),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            ShowItemToolTips = true,
            Padding = new Forms.Padding(6,6,6,6),
            Renderer = _renderer
        };
        _ = new RoundedMenuChrome(_menu,244,6);
        _menu.Opening += (_,_) => RebuildMenu();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            ContextMenuStrip = _menu,
            Text = "Gameorder",
            Visible = true
        };
        _notifyIcon.MouseClick += (_,e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ShowFromTray();
        };
        _notifyIcon.BalloonTipClicked += (_,_) => _window.ShowFromTray();
        try
        {
        _viewModel.PropertyChanged += OnViewModelChanged;
        _accountChanges = _viewModel.AllAccounts as INotifyCollectionChanged;
        if (_accountChanges is not null) _accountChanges.CollectionChanged += OnAccountsChanged;
        ObserveAccounts();
        UpdateShellState();
        }
        catch
        {
            _viewModel.PropertyChanged-=OnViewModelChanged;
            if(_accountChanges is not null) _accountChanges.CollectionChanged-=OnAccountsChanged;
            foreach(var account in _observedAccounts) account.PropertyChanged-=OnAccountChanged;
            _notifyIcon.Visible=false; _notifyIcon.Dispose(); _menu.Dispose();
            _icon.Dispose(); _menuFont.Dispose(); _captionFont.Dispose(); _headingFont.Dispose(); _window.TaskbarItemInfo=null;
            throw;
        }
    }

    // This model is shared with read-only verification; it creates no native shell objects.
    public static IReadOnlyList<TrayAccountEntry> BuildMenuSnapshot(MainViewModel viewModel)
    {
        bool pathReady = viewModel.IsDemo || File.Exists(Path.Combine(viewModel.SteamPath,"steam.exe"));
        bool canRequest = !viewModel.IsBusy && !viewModel.PendingConfirmation && !viewModel.IsAddAccountOpen && !viewModel.HasManagementDialog && pathReady;
        return viewModel.AllAccounts.OrderByDescending(a=>a.IsActive).ThenByDescending(a=>a.IsFavorite)
            .Select(a=>new TrayAccountEntry(a.SteamId,a.AccountName,a.DisplayName,a.PresenceLabel,a.IsActive,a.IsOnline,a.IsOffline,
                canRequest,canRequest && a.HasCachedLogin,a.HasAvatar,a.FolderName,a.NotePreview)).ToArray();
    }

    public static Task RequestSwitchForVerificationAsync(MainViewModel viewModel,string steamId,SwitchMode mode)
        => viewModel.SelectAndSwitchAsync(steamId,mode);

    public int RefreshNativeMenuForVerification()
    {
        return PrepareMenuForVerification().AccountCount;
    }

    public TrayMenuVerification PrepareMenuForVerification()
    {
        RebuildMenu();
        static IEnumerable<Forms.ToolStripMenuItem> Descendants(Forms.ToolStripItemCollection items)
        {
            foreach(var item in items.OfType<Forms.ToolStripMenuItem>())
            {
                yield return item;
                foreach(var child in Descendants(item.DropDownItems)) yield return child;
            }
        }
        var accounts=Descendants(_menu.Items).Where(item=>item.DropDownItems.Count==2&&item.DropDownItems[0].Text=="在线登录"&&item.DropDownItems[1].Text=="离线登录").ToArray();
        var actions=accounts.SelectMany(item=>item.DropDownItems.Cast<Forms.ToolStripItem>()).ToArray();
        return new(accounts.Length,actions.Count(item=>item.Text=="在线登录"),actions.Count(item=>item.Text=="离线登录"),
            actions.Count(item=>item.Text=="离线登录" && item.Enabled),accounts.Count(item=>item.Image is not null));
    }

    private void RebuildMenu()
    {
        if (_disposed) return;
        var previousItems = _menu.Items.Cast<Forms.ToolStripItem>().ToArray();
        _menu.Items.Clear();
        foreach (var item in previousItems) item.Dispose();
        foreach (var image in _menuImages) image.Dispose();
        _menuImages.Clear();
        int imageSize=(int)Math.Round(28*_menu.DeviceDpi/96d);
        _menu.ImageScalingSize=new Drawing.Size(imageSize,imageSize);
        _menu.MinimumSize=new Drawing.Size((int)Math.Round(244*_menu.DeviceDpi/96d),0);
        ObserveAccounts();
        var brand=_icon.ToBitmap(); _menuImages.Add(brand);
        _menu.Items.Add(new HeadingMenuItem("Gameorder",_viewModel.HasActiveAccount ? _viewModel.ActiveAccountName+" · "+_viewModel.SessionSummary : _viewModel.SessionSummary,brand) {Enabled=false});
        _menu.Items.Add(new Forms.ToolStripSeparator());
        var quick = CreateAction("账号头像栏","avatars");
        quick.Click += (_,_) => _window.ShowQuickAccounts();
        _menu.Items.Add(quick);
        var entries = BuildMenuSnapshot(_viewModel);
        foreach (var group in entries.GroupBy(entry=>entry.FolderName))
        {
            var folderMenu=CreateAction(group.Key,"folder");
            folderMenu.ToolTipText=$"{group.Count()} 个账号";
            StyleDropDown(folderMenu,true);
            _menu.Items.Add(folderMenu);
            foreach (var entry in group)
            {
            var account = _viewModel.AllAccounts.First(a=>a.AccountName==entry.AccountName);
            var avatar = CreateMenuAvatar(account,imageSize);
            _menuImages.Add(avatar);
            var item = new AccountMenuItem(entry.DisplayName,entry.Note,entry.PresenceLabel,entry.IsOnline,entry.IsActive,avatar)
            {
                ToolTipText=(string.IsNullOrEmpty(account.Note) ? "" : account.Note+"\n")+account.PresenceHint,
                ForeColor=entry.IsOnline ? Drawing.Color.FromArgb(38,143,105) : Drawing.Color.FromArgb(43,52,70)
            };
            StyleDropDown(item);
            var online = CreateAction("在线登录","online"); online.Enabled=entry.CanOnline;
            var offline = CreateAction("离线登录","offline"); offline.Enabled=entry.CanOffline;
            offline.ToolTipText=entry.CanOffline ? "使用本机已记住的登录" : "需要本机有效的已记住登录";
            // Capture account identity, not selection, so a later selection cannot redirect a shortcut.
            online.Click += async (_,_) => await RequestAccountSwitchAsync(account,SwitchMode.Online);
            offline.Click += async (_,_) => await RequestAccountSwitchAsync(account,SwitchMode.Offline);
            item.DropDownItems.Add(online);
            item.DropDownItems.Add(offline);
            folderMenu.DropDownItems.Add(item);
            }
        }
        if (entries.Count==0) _menu.Items.Add(new Forms.ToolStripMenuItem("还没有账号") { Enabled=false });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        var open = CreateAction("打开主界面","open");
        open.Click += (_,_) => _window.ShowFromTray();
        var refresh = CreateAction("刷新账号","refresh"); refresh.Enabled=_viewModel.RefreshCommand.CanExecute(null);
        refresh.Click += (_,_) => { if(_viewModel.RefreshCommand.CanExecute(null)) _viewModel.RefreshCommand.Execute(null); };
        var exit = CreateAction("退出","exit"); exit.Enabled=!_viewModel.IsBusy;
        exit.Click += (_,_) => _window.RequestExit();
        _menu.Items.Add(open); _menu.Items.Add(refresh); _menu.Items.Add(exit);
    }

    private Forms.ToolStripMenuItem CreateAction(string text,string glyph)
    {
        var image=CreateActionIcon(glyph); _menuImages.Add(image);
        float scale=_menu.DeviceDpi/96f;
        return new Forms.ToolStripMenuItem(text,image) { AutoSize=false,Width=(int)Math.Round(230*scale),Height=(int)Math.Round(32*scale),Padding=new Forms.Padding(3,0,3,0) };
    }

    private void StyleDropDown(Forms.ToolStripMenuItem item,bool accountRows=false)
    {
        item.DropDown.Font=_menuFont;
        item.DropDown.Renderer=_renderer;
        item.DropDown.Padding=new Forms.Padding(5,5,5,5);
        item.DropDown.MinimumSize=new Drawing.Size((int)Math.Round((accountRows?282:182)*_menu.DeviceDpi/96d),0);
        if(item.DropDown is Forms.ToolStripDropDownMenu dropdown)
        { dropdown.ImageScalingSize=_menu.ImageScalingSize; dropdown.ShowCheckMargin=false; }
        _ = new RoundedMenuChrome(item.DropDown,accountRows?282:182,5);
    }

    // Render the actual menu component without sending input to Windows or selecting an action.
    public void SaveMenuPreviewForVerification(string output)
    {
        RebuildMenu();
        static void Prepare(Forms.ToolStrip strip)
        {
            strip.PerformLayout(); strip.CreateControl();
            _ = strip.Handle; // Force the same HWND region that the visible menu will use, without showing it.
            // Apply the menu's final DPI/row metrics before measuring, then resolve its final HWND bounds.
            strip.PerformLayout();
            strip.Size=strip.GetPreferredSize(Drawing.Size.Empty);
            strip.PerformLayout();
        }
        void Save(Forms.ToolStrip strip,string name)
        {
            Prepare(strip);
            using var bitmap=new Drawing.Bitmap(Math.Max(1,strip.Width),Math.Max(1,strip.Height));
            strip.DrawToBitmap(bitmap,new Drawing.Rectangle(Drawing.Point.Empty,bitmap.Size));
            // WM_PRINT may paint its rectangular target; export precisely the real window's clipped region.
            if(strip.Region is not null)
            {
                using var graphics=Drawing.Graphics.FromImage(bitmap);
                graphics.SetClip(strip.Region,Drawing2D.CombineMode.Exclude);
                graphics.CompositingMode=Drawing2D.CompositingMode.SourceCopy;
                using var transparent=new Drawing.SolidBrush(Drawing.Color.Transparent);
                graphics.FillRectangle(transparent,new Drawing.Rectangle(Drawing.Point.Empty,bitmap.Size));
            }
            bitmap.Save(Path.Combine(output,name),Drawing.Imaging.ImageFormat.Png);
        }
        Directory.CreateDirectory(output);
        Save(_menu,"tray-menu.png");
        var folder=_menu.Items.OfType<Forms.ToolStripMenuItem>().FirstOrDefault(item=>item.DropDownItems.Count>0);
        if(folder is not null) Save(folder.DropDown,"tray-accounts.png");
        var login=folder?.DropDownItems.OfType<Forms.ToolStripMenuItem>().FirstOrDefault(item=>item.DropDownItems.Count>0);
        if(login is not null) Save(login.DropDown,"tray-login.png");
        var shapes=new List<MenuShapeVerification>();
        void Capture(Forms.ToolStripDropDown strip,string level)
        {
            Prepare(strip); shapes.Add(CaptureMenuShape(strip,level));
            foreach(var item in strip.Items.OfType<Forms.ToolStripMenuItem>().Where(item=>item.HasDropDownItems))
                Capture(item.DropDown,level=="main"?"accounts":"login");
        }
        Capture(_menu,"main");
        File.WriteAllText(Path.Combine(output,"tray-layout.json"),System.Text.Json.JsonSerializer.Serialize(new {
            MenuWidth=_menu.Width,ItemWidths=_menu.Items.Cast<Forms.ToolStripItem>().Select(item=>item.Width),
            AccountMenuWidth=folder?.DropDown.Width,AccountItemWidths=folder?.DropDownItems.Cast<Forms.ToolStripItem>().Select(item=>item.Width),
            LoginMenuWidth=login?.DropDown.Width,Menus=shapes,
            CaptionFontSizePoints=_captionFont.SizeInPoints,HeadingFontSizePoints=_headingFont.SizeInPoints,
            AllMenusRounded=shapes.All(shape=>shape.CornersExcluded&&shape.CenterIncluded&&shape.NativeRegionApplied&&shape.NativeCornersExcluded&&shape.NativeCenterIncluded)
        }));
    }

    private async Task RequestAccountSwitchAsync(SteamAccount account,SwitchMode mode)
    {
        if (_disposed) return;
        try
        {
            await _window.RequestWidgetSwitchAsync(account,mode);
        }
        catch (Exception ex) { _viewModel.ShowStatus("快捷切换未完成",ex.Message,"error"); }
    }

    public void ShowQuickMenu()
    {
        if (_disposed) return;
        if (_menu.Visible) _menu.Close();
        _window.ShowQuickAccounts();
    }

    private void OnViewModelChanged(object? sender,PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.AccountsSummary) or nameof(MainViewModel.AvatarSummary)) ObserveAccounts();
        UpdateShellState();
    }
    private void OnAccountsChanged(object? sender,NotifyCollectionChangedEventArgs e) { ObserveAccounts(); UpdateShellState(); }
    private void OnAccountChanged(object? sender,PropertyChangedEventArgs e) => UpdateShellState();

    private void ObserveAccounts()
    {
        var latest = _viewModel.AllAccounts.ToHashSet();
        foreach (var account in _observedAccounts.Except(latest).ToArray())
        { account.PropertyChanged-=OnAccountChanged; _observedAccounts.Remove(account); }
        foreach (var account in latest.Except(_observedAccounts))
        { account.PropertyChanged+=OnAccountChanged; _observedAccounts.Add(account); }
    }

    private void UpdateShellState()
    {
        if (_disposed) return;
        string text="Gameorder · "+_viewModel.SessionSummary;
        _notifyIcon.Text=text.Length>63 ? text[..(char.IsHighSurrogate(text[62]) ? 62 : 63)] : text;
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            var resource=Application.GetResourceStream(new Uri("pack://application:,,,/assets/Gameorder.ico"));
            if(resource is not null)
            { using(resource.Stream) using(var icon=new Drawing.Icon(resource.Stream,32,32)) return (Drawing.Icon)icon.Clone(); }
        }
        catch (Exception) { }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static Drawing.Bitmap CreateMenuAvatar(SteamAccount account,int size)
    {
        var result=new Drawing.Bitmap(size,size);
        using var graphics=Drawing.Graphics.FromImage(result);
        graphics.ScaleTransform(size/28f,size/28f);
        graphics.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias;
        graphics.InterpolationMode=Drawing2D.InterpolationMode.HighQualityBicubic;
        using var circle=new Drawing2D.GraphicsPath();
        circle.AddEllipse(1,1,26,26);
        graphics.SetClip(circle);
        using var background=new Drawing.SolidBrush(Drawing.Color.FromArgb(237,241,248));
        graphics.FillEllipse(background,1,1,26,26);
        bool painted=false;
        if (!string.IsNullOrWhiteSpace(account.AvatarPath))
        {
            try
            {
                using var file=new FileStream(account.AvatarPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var image=Drawing.Image.FromStream(file);
                int side=Math.Min(image.Width,image.Height);
                graphics.DrawImage(image,new Drawing.Rectangle(1,1,26,26),new Drawing.Rectangle((image.Width-side)/2,(image.Height-side)/2,side,side),Drawing.GraphicsUnit.Pixel);
                painted=true;
            }
            catch (Exception) { }
        }
        if (!painted)
        {
            using var foreground=new Drawing.SolidBrush(Drawing.Color.FromArgb(149,165,189));
            graphics.FillEllipse(foreground,10,6,8,8);
            graphics.FillEllipse(foreground,6,16,16,15);
        }
        graphics.ResetClip();
        if (account.IsActive)
        {
            using var outline=new Drawing.Pen(Drawing.Color.White,2);
            using var status=new Drawing.SolidBrush(account.IsOnline ? Drawing.Color.FromArgb(46,166,119) : Drawing.Color.FromArgb(149,162,184));
            graphics.FillEllipse(status,19,19,8,8); graphics.DrawEllipse(outline,19,19,8,8);
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true;
        _viewModel.PropertyChanged-=OnViewModelChanged;
        if (_accountChanges is not null) _accountChanges.CollectionChanged-=OnAccountsChanged;
        foreach(var account in _observedAccounts) account.PropertyChanged-=OnAccountChanged;
        _observedAccounts.Clear();
        _notifyIcon.Visible=false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach(var image in _menuImages) image.Dispose();
        _menuImages.Clear();
        _icon.Dispose(); _menuFont.Dispose(); _captionFont.Dispose(); _headingFont.Dispose();
        _window.TaskbarItemInfo=null;
    }

    private static Drawing.Bitmap CreateActionIcon(string glyph)
    {
        var bitmap=new Drawing.Bitmap(32,32);
        using var g=Drawing.Graphics.FromImage(bitmap); g.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias;
        using var pen=new Drawing.Pen(Drawing.Color.FromArgb(105,123,150),1.65f) {StartCap=Drawing2D.LineCap.Round,EndCap=Drawing2D.LineCap.Round,LineJoin=Drawing2D.LineJoin.Round};
        switch(glyph)
        {
            case "avatars":
                g.DrawEllipse(pen,7,6,6,6); g.DrawEllipse(pen,19,6,6,6);
                g.DrawArc(pen,4,14,12,12,180,180); g.DrawArc(pen,16,14,12,12,180,180); break;
            case "folder":
                g.DrawLines(pen,[new(6,10),new(6,8),new(13,8),new(16,11),new(26,11),new(26,24),new(6,24),new(6,10)]); break;
            case "open":
                g.DrawRectangle(pen,6,7,20,18); g.DrawLine(pen,6,12,26,12); g.DrawLine(pen,12,12,12,25); break;
            case "refresh":
                g.DrawArc(pen,7,7,18,18,40,280); g.DrawLines(pen,[new(19,7),new(25,7),new(25,13)]); break;
            case "exit":
                g.DrawArc(pen,7,7,18,18,310,280); g.DrawLine(pen,16,5,16,15); break;
            case "online":
                pen.Color=Drawing.Color.FromArgb(43,152,109); g.DrawEllipse(pen,7,7,18,18); g.DrawLines(pen,[new(12,16),new(15,19),new(21,12)]); break;
            case "offline":
                g.DrawEllipse(pen,7,7,18,18); g.DrawLine(pen,9,23,23,9); break;
        }
        return bitmap;
    }

    private sealed class HeadingMenuItem(string title,string caption,Drawing.Image image) : Forms.ToolStripMenuItem(title,image)
    {
        public string Caption { get; }=caption;
        public override Drawing.Size GetPreferredSize(Drawing.Size constrainingSize)=>new((int)Math.Round(230*(Owner?.DeviceDpi??96)/96d),(int)Math.Round(48*(Owner?.DeviceDpi??96)/96d));
    }
    private sealed class AccountMenuItem(string title,string note,string status,bool online,bool active,Drawing.Image image) : Forms.ToolStripMenuItem(title,image)
    {
        public string Caption { get; }=note;
        public string Status { get; }=status;
        public bool Online { get; }=online;
        public bool Active { get; }=active;
        public override Drawing.Size GetPreferredSize(Drawing.Size constrainingSize)=>new((int)Math.Round(268*(Owner?.DeviceDpi??96)/96d),(int)Math.Round(44*(Owner?.DeviceDpi??96)/96d));
    }
    private sealed record MenuItemBoundsVerification(string Text,int X,int Y,int Width,int Height,int Right,int Bottom,
        bool Available,bool InsideClientBounds,bool InsideNativeRegion,bool IsDisplayed,bool IsFullyDisplayed,
        int VisibleX,int VisibleY,int VisibleWidth,int VisibleHeight);
    private sealed record MenuShapeVerification(string Level,int Width,int Height,double WidthDip,double HeightDip,int Dpi,int RadiusPixels,
        bool HasRegion,bool CornersExcluded,bool CenterIncluded,bool NativeRegionApplied,bool NativeCornersExcluded,bool NativeCenterIncluded,
        int[] ItemHeights,int[] ItemWidths,double[] ItemHeightsDip,double[] ItemWidthsDip,double[] ActionHeightsDip,
        double[] HeadingHeightsDip,double[] AccountHeightsDip,float FontSizePoints,double FontHeightDip,
        MenuItemBoundsVerification[] ItemBounds,bool AllItemsInsideClientBounds,bool AllItemsInsideNativeRegion,
        int ContentLeft,int ContentTop,int ContentRight,int ContentBottom,int PaddingTop,int PaddingBottom,
        bool RequiresScrolling,int AvailableItemCount,int VisibleItemCount,int RequestedContentHeight,
        int DisplayLeft,int DisplayTop,int DisplayRight,int DisplayBottom);

    private static MenuShapeVerification CaptureMenuShape(Forms.ToolStrip strip,string level)
    {
        var corners=new[]{new Drawing.Point(1,1),new Drawing.Point(strip.Width-2,1),new Drawing.Point(1,strip.Height-2),new Drawing.Point(strip.Width-2,strip.Height-2)};
        var center=new Drawing.Point(strip.Width/2,strip.Height/2);
        bool nativeApplied=false,nativeCorners=false,nativeCenter=false;
        var itemBounds=new List<MenuItemBoundsVerification>();
        IntPtr region=CreateRectRgn(0,0,0,0);
        try
        {
            nativeApplied=region!=IntPtr.Zero&&GetWindowRgn(strip.Handle,region)>1;
            if(nativeApplied)
            {
                nativeCorners=corners.All(point=>!PtInRegion(region,point.X,point.Y));
                nativeCenter=PtInRegion(region,center.X,center.Y);
            }
            foreach(var item in strip.Items.Cast<Forms.ToolStripItem>())
            {
                var bounds=item.Bounds;
                bool inside=bounds.Width>0&&bounds.Height>0&&bounds.Left>=1&&bounds.Top>=1&&bounds.Right<=strip.Width-1&&bounds.Bottom<=strip.Height-1;
                bool nativeInside=inside&&nativeApplied&&new[]{new Drawing.Point(bounds.Left,bounds.Top),new Drawing.Point(bounds.Right-1,bounds.Top),
                    new Drawing.Point(bounds.Left,bounds.Bottom-1),new Drawing.Point(bounds.Right-1,bounds.Bottom-1)}.All(point=>PtInRegion(region,point.X,point.Y));
                var visibleBounds=Drawing.Rectangle.Intersect(strip.DisplayRectangle,bounds);
                bool displayed=item.Available&&visibleBounds.Width>0&&visibleBounds.Height>0;
                itemBounds.Add(new(item.Text??string.Empty,bounds.X,bounds.Y,bounds.Width,bounds.Height,bounds.Right,bounds.Bottom,item.Available,inside,nativeInside,
                    displayed,displayed&&strip.DisplayRectangle.Contains(bounds),visibleBounds.X,visibleBounds.Y,visibleBounds.Width,visibleBounds.Height));
            }
        }
        finally { if(region!=IntPtr.Zero) DeleteObject(region); }
        double scale=strip.DeviceDpi/96d;
        int requestedHeight=strip.Items.Cast<Forms.ToolStripItem>().Where(item=>item.Available).Sum(item=>item.Height+item.Margin.Vertical)+strip.Padding.Vertical;
        return new(level,strip.Width,strip.Height,strip.Width/scale,strip.Height/scale,strip.DeviceDpi,(int)Math.Round(10*scale),
            strip.Region is not null,strip.Region is not null&&corners.All(point=>!strip.Region.IsVisible(point)),strip.Region?.IsVisible(center)??false,
            nativeApplied,nativeCorners,nativeCenter,strip.Items.Cast<Forms.ToolStripItem>().Select(item=>item.Height).ToArray(),strip.Items.Cast<Forms.ToolStripItem>().Select(item=>item.Width).ToArray(),
            strip.Items.Cast<Forms.ToolStripItem>().Select(item=>item.Height/scale).ToArray(),strip.Items.Cast<Forms.ToolStripItem>().Select(item=>item.Width/scale).ToArray(),
            strip.Items.OfType<Forms.ToolStripMenuItem>().Where(item=>item is not HeadingMenuItem and not AccountMenuItem).Select(item=>item.Height/scale).ToArray(),
            strip.Items.OfType<HeadingMenuItem>().Select(item=>item.Height/scale).ToArray(),strip.Items.OfType<AccountMenuItem>().Select(item=>item.Height/scale).ToArray(),
            strip.Font.SizeInPoints,strip.Font.GetHeight(strip.DeviceDpi)/scale,itemBounds.ToArray(),
            itemBounds.Where(item=>item.Available).All(item=>item.InsideClientBounds),itemBounds.Where(item=>item.Available).All(item=>item.InsideNativeRegion),
            itemBounds.Where(item=>item.Available).Select(item=>item.X).DefaultIfEmpty().Min(),itemBounds.Where(item=>item.Available).Select(item=>item.Y).DefaultIfEmpty().Min(),
            itemBounds.Where(item=>item.Available).Select(item=>item.Right).DefaultIfEmpty().Max(),itemBounds.Where(item=>item.Available).Select(item=>item.Bottom).DefaultIfEmpty().Max(),
            strip.Padding.Top,strip.Padding.Bottom,requestedHeight>strip.Height,itemBounds.Count(item=>item.Available),itemBounds.Count(item=>item.IsDisplayed),requestedHeight,
            strip.DisplayRectangle.Left,strip.DisplayRectangle.Top,strip.DisplayRectangle.Right,strip.DisplayRectangle.Bottom);
    }

    // A renderer changes pixels; Control.Region also changes the native window outline and hit testing.
    // WinForms owns each assigned Region and disposes it on replacement and control disposal.
    private sealed class RoundedMenuChrome : IDisposable
    {
        private readonly Forms.ToolStripDropDown _strip;
        private readonly int _widthDip,_paddingDip;
        private Drawing.Size _roundedSize;
        private int _roundedDpi;
        private bool _updating,_disposed;
        public RoundedMenuChrome(Forms.ToolStripDropDown strip,int widthDip,int paddingDip)
        {
            _strip=strip; _widthDip=widthDip; _paddingDip=paddingDip;
            strip.Layout+=OnLayout;
            strip.SizeChanged+=OnGeometryChanged;
            strip.DpiChangedAfterParent+=OnGeometryChanged;
            strip.HandleCreated+=OnGeometryChanged;
            strip.Opened+=OnGeometryChanged;
            strip.Disposed+=OnDisposed;
            ApplyMetricsAndShape();
        }
        private void OnLayout(object? sender,Forms.LayoutEventArgs e)=>ApplyMetricsAndShape();
        private void OnGeometryChanged(object? sender,EventArgs e)=>ApplyMetricsAndShape();
        private void ApplyMetricsAndShape()
        {
            if(_disposed||_updating||_strip.IsDisposed) return;
            _updating=true;
            try
            {
                int D(int value)=>(int)Math.Round(value*_strip.DeviceDpi/96d);
                int width=D(_widthDip),padding=D(_paddingDip);
                _strip.MaximumSize=new Drawing.Size(width,0);
                _strip.Padding=new Forms.Padding(padding);
                _strip.ImageScalingSize=new Drawing.Size(D(28),D(28));
                foreach(var item in _strip.Items.OfType<Forms.ToolStripMenuItem>())
                {
                    item.AutoSize=false;
                    item.Size=new Drawing.Size(D(_widthDip-14),D(item is HeadingMenuItem?48:item is AccountMenuItem?44:32));
                    item.Padding=new Forms.Padding(D(3),0,D(3),0);
                    // WinForms menu items subtract the owner's left Padding in SetBounds;
                    // an explicit row margin preserves a real inset for the rounded outline.
                    item.Margin=new Forms.Padding(padding,0,0,0);
                }
                // ToolStripDropDownMenu's preferred-size cache can retain its default 2px top/bottom
                // padding after Layout changes ours. A real minimum height protects every menu row.
                int contentHeight=_strip.Items.Cast<Forms.ToolStripItem>().Where(item=>item.Available).Sum(item=>item.Height+item.Margin.Vertical);
                int height=Math.Min(contentHeight+padding*2,Math.Max(D(32),Forms.Screen.FromControl(_strip).WorkingArea.Height-D(8)));
                _strip.MinimumSize=new Drawing.Size(width,height);
                if(_strip.Width<=1||_strip.Height<=1) return;
                if(_roundedSize==_strip.Size&&_roundedDpi==_strip.DeviceDpi&&_strip.Region is not null) return;
                using var path=CreateRoundedPath(new Drawing.RectangleF(0,0,_strip.Width,_strip.Height),D(10));
                _strip.Region=new Drawing.Region(path);
                _roundedSize=_strip.Size; _roundedDpi=_strip.DeviceDpi;
            }
            finally { _updating=false; }
        }
        private void OnDisposed(object? sender,EventArgs e)=>Dispose();
        public void Dispose()
        {
            if(_disposed) return; _disposed=true;
            _strip.Layout-=OnLayout; _strip.SizeChanged-=OnGeometryChanged; _strip.DpiChangedAfterParent-=OnGeometryChanged;
            _strip.HandleCreated-=OnGeometryChanged; _strip.Opened-=OnGeometryChanged; _strip.Disposed-=OnDisposed;
        }
    }
    private static Drawing2D.GraphicsPath CreateRoundedPath(Drawing.RectangleF rectangle,float radius)
    {
        float diameter=Math.Max(1,Math.Min(radius*2,Math.Min(rectangle.Width,rectangle.Height)));
        var path=new Drawing2D.GraphicsPath();
        path.AddArc(rectangle.X,rectangle.Y,diameter,diameter,180,90);
        path.AddArc(rectangle.Right-diameter,rectangle.Y,diameter,diameter,270,90);
        path.AddArc(rectangle.Right-diameter,rectangle.Bottom-diameter,diameter,diameter,0,90);
        path.AddArc(rectangle.X,rectangle.Bottom-diameter,diameter,diameter,90,90);
        path.CloseFigure(); return path;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window,IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region,int x,int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    private sealed class GameorderMenuRenderer(Drawing.Font captionFont,Drawing.Font headingFont) : Forms.ToolStripProfessionalRenderer(new GameorderMenuColors())
    {
        private static Drawing.Color Ink=>Drawing.Color.FromArgb(43,55,76);
        private static Drawing.Color Muted=>Drawing.Color.FromArgb(121,136,159);
        private const Forms.TextFormatFlags Flags=Forms.TextFormatFlags.EndEllipsis|Forms.TextFormatFlags.NoPrefix|Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.VerticalCenter
            |Forms.TextFormatFlags.PreserveGraphicsClipping|Forms.TextFormatFlags.PreserveGraphicsTranslateTransform;
        protected override void OnRenderItemImage(Forms.ToolStripItemImageRenderEventArgs e)
        {
            if(e.Item is HeadingMenuItem&&e.Image is not null) e.Graphics.DrawImage(e.Image,e.ImageRectangle);
            else base.OnRenderItemImage(e);
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            int D(int size)=>(int)Math.Round(size*(e.ToolStrip?.DeviceDpi??96)/96d);
            if(e.Item is HeadingMenuItem header)
            {
                var r=e.TextRectangle;
                int width=Math.Max(D(40),e.Item.Width-r.X-D(16));
                Forms.TextRenderer.DrawText(e.Graphics,header.Text,headingFont,new Drawing.Rectangle(r.X,D(4),width,D(22)),Ink,Flags);
                Forms.TextRenderer.DrawText(e.Graphics,header.Caption,captionFont,new Drawing.Rectangle(r.X,D(25),width,D(19)),Muted,Flags);
                return;
            }
            if(e.Item is AccountMenuItem account)
            {
                var r=e.TextRectangle; int badgeWidth=account.Active ? D(56) : 0;
                int width=Math.Max(D(24),e.Item.Width-r.X-D(24));
                Forms.TextRenderer.DrawText(e.Graphics,account.Text,e.TextFont,new Drawing.Rectangle(r.X,D(2),Math.Max(D(24),width-badgeWidth),D(21)),Ink,Flags);
                Forms.TextRenderer.DrawText(e.Graphics,account.Caption,captionFont,new Drawing.Rectangle(r.X,D(23),width,D(18)),Muted,Flags);
                if(account.Active)
                {
                    var badge=new Drawing.Rectangle(e.Item.Width-D(73),D(4),D(48),D(18));
                    using var background=new Drawing.SolidBrush(account.Online ? Drawing.Color.FromArgb(232,246,239) : Drawing.Color.FromArgb(235,240,248));
                    using var path=Rounded(badge,D(8)); e.Graphics.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias; e.Graphics.FillPath(background,path);
                    Forms.TextRenderer.DrawText(e.Graphics,account.Status,captionFont,badge,account.Online ? Drawing.Color.FromArgb(38,137,99) : Muted,Flags|Forms.TextFormatFlags.HorizontalCenter);
                }
                return;
            }
            e.TextColor=e.Item.Enabled ? Ink : Drawing.Color.FromArgb(157,167,183);
            e.TextFormat|=Forms.TextFormatFlags.NoPrefix;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if(e.Item is HeadingMenuItem || !e.Item.Selected || !e.Item.Enabled) return;
            int D(int value)=>(int)Math.Round(value*(e.ToolStrip?.DeviceDpi??96)/96d);
            using var brush=new Drawing.SolidBrush(Drawing.Color.FromArgb(232,239,251));
            using var path=Rounded(new Drawing.Rectangle(D(3),D(2),e.Item.Width-D(6),e.Item.Height-D(4)),D(8));
            e.Graphics.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias; e.Graphics.FillPath(brush,path);
        }
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            if(e.ToolStrip is not Forms.ToolStripDropDown) { base.OnRenderToolStripBackground(e); return; }
            float scale=e.ToolStrip.DeviceDpi/96f;
            using var path=CreateRoundedPath(new Drawing.RectangleF(0,0,e.ToolStrip.Width,e.ToolStrip.Height),10*scale);
            using var brush=new Drawing.SolidBrush(Drawing.Color.FromArgb(249,251,254));
            e.Graphics.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias; e.Graphics.FillPath(brush,path);
        }
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            if(e.ToolStrip is not Forms.ToolStripDropDown) { base.OnRenderToolStripBorder(e); return; }
            float scale=e.ToolStrip.DeviceDpi/96f;
            using var path=CreateRoundedPath(new Drawing.RectangleF(.5f,.5f,e.ToolStrip.Width-1,e.ToolStrip.Height-1),10*scale);
            using var pen=new Drawing.Pen(Drawing.Color.FromArgb(221,228,239));
            e.Graphics.SmoothingMode=Drawing2D.SmoothingMode.AntiAlias; e.Graphics.DrawPath(pen,path);
        }
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            if(e.Item is null) { base.OnRenderArrow(e); return; }
            int size=(int)Math.Round(7*(e.Item.Owner?.DeviceDpi??96)/96d);
            e.ArrowRectangle=new Drawing.Rectangle(e.Item.Width-size*3,(e.Item.Height-size)/2,size,size);
            e.ArrowColor=Muted;
            base.OnRenderArrow(e);
        }
        private static Drawing2D.GraphicsPath Rounded(Drawing.Rectangle r,int radius)
        {
            int d=radius*2; var path=new Drawing2D.GraphicsPath();
            path.AddArc(r.X,r.Y,d,d,180,90); path.AddArc(r.Right-d,r.Y,d,d,270,90);
            path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); path.AddArc(r.X,r.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
        }
    }

    private sealed class GameorderMenuColors : Forms.ProfessionalColorTable
    {
        private static Drawing.Color Background => Drawing.Color.FromArgb(249,251,254);
        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuItemSelected => Drawing.Color.FromArgb(230,237,249);
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(212,223,243);
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(221,228,239);
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(225,232,241);
        public override Drawing.Color SeparatorLight => Background;
    }
}
