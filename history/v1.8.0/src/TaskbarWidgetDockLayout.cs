using System.Windows;

namespace Gameorder;

public enum TaskbarDockPosition { Tray, Left }
public sealed class TaskbarDockPositionCommittedEventArgs(TaskbarDockPosition position) : EventArgs
{
    public TaskbarDockPosition Position { get; }=position;
}
public sealed record TaskbarDockSlot(TaskbarDockPosition Position,Rect BoundsPixels)
{
    public Point CenterPixels=>new(BoundsPixels.Left+BoundsPixels.Width/2,BoundsPixels.Top+BoundsPixels.Height/2);
}
public sealed record TaskbarDockSlots(TaskbarDockSlot? Tray,TaskbarDockSlot? Left)
{
    public TaskbarDockSlot? Get(TaskbarDockPosition position)=>position==TaskbarDockPosition.Tray?Tray:Left;
    public IEnumerable<TaskbarDockSlot> Available=>new[]{Tray,Left}.OfType<TaskbarDockSlot>();
}

/// <summary>Two fixed edge anchors within verified free taskbar intervals.</summary>
public static class TaskbarWidgetDockLayout
{
    public static TaskbarDockSlots FindSlots(Rect taskbar,IReadOnlyCollection<Rect> occupied,Rect trayBounds,double scale,
        double desiredWidthDip=360,double minimumWidthDip=80,double desiredHeightDip=48)
    {
        if(taskbar.IsEmpty||taskbar.Width<=taskbar.Height||!double.IsFinite(scale)||scale<=0
            ||!double.IsFinite(desiredWidthDip)||!double.IsFinite(minimumWidthDip)||!double.IsFinite(desiredHeightDip)
            ||desiredWidthDip<minimumWidthDip||minimumWidthDip<=0||desiredHeightDip<=0) return new(null,null);
        double left=taskbar.Left+4*scale,right=taskbar.Right-4*scale,clearance=8*scale;
        var segments=occupied.Append(trayBounds).Select(rect=>Rect.Intersect(taskbar,rect)).Where(rect=>!rect.IsEmpty&&rect.Width>0&&rect.Height>0)
            .Select(rect=>(Start:Math.Max(left,rect.Left-clearance),End:Math.Min(right,rect.Right+clearance)))
            .Where(segment=>segment.End>segment.Start).OrderBy(segment=>segment.Start);
        var gaps=new List<(double Start,double End)>(); double current=left;
        foreach(var segment in segments)
        {
            if(segment.Start>current) gaps.Add((current,segment.Start));
            current=Math.Max(current,segment.End);
        }
        if(current<right) gaps.Add((current,right));
        double height=Math.Min(desiredHeightDip*scale,taskbar.Height-2*scale);
        if(height<24*scale) return new(null,null);
        TaskbarDockSlot? Make((double Start,double End) gap,TaskbarDockPosition position)
        {
            double width=Math.Min(desiredWidthDip*scale,gap.End-gap.Start);
            if(width<minimumWidthDip*scale) return null;
            return new(position,new Rect(position==TaskbarDockPosition.Left?gap.Start:gap.End-width,
                taskbar.Top+(taskbar.Height-height)/2,width,height));
        }
        var safeGaps=gaps.Where(gap=>gap.End-gap.Start>=minimumWidthDip*scale).ToArray();
        TaskbarDockSlot? leftSlot=safeGaps.Length>0?Make(safeGaps[0],TaskbarDockPosition.Left):null;
        TaskbarDockSlot? traySlot=null;
        if(!trayBounds.IsEmpty&&taskbar.IntersectsWith(trayBounds))
        {
            double trayEdge=trayBounds.Left-clearance;
            var trayGaps=safeGaps.Select(gap=>(gap.Start,End:Math.Min(gap.End,trayEdge)))
                .Where(gap=>gap.End-gap.Start>=minimumWidthDip*scale).OrderByDescending(gap=>gap.End).ToArray();
            if(trayGaps.Length>0) traySlot=Make(trayGaps[0],TaskbarDockPosition.Tray);
        }
        // Only one widget is displayed, so overlapping frames are safe. If both edge anchors live
        // in the same narrow gap, adapt their common size to leave distinguishable snap centers.
        if(traySlot is not null&&leftSlot is not null&&Math.Abs(traySlot.CenterPixels.X-leftSlot.CenterPixels.X)<160*scale)
        {
            var sharedGap=safeGaps.FirstOrDefault(gap=>gap.Start<=leftSlot.BoundsPixels.Left&&gap.End>=traySlot.BoundsPixels.Right);
            if(sharedGap.End>sharedGap.Start)
            {
                double width=Math.Min(desiredWidthDip*scale,Math.Max(minimumWidthDip*scale,sharedGap.End-sharedGap.Start-160*scale));
                leftSlot=new(TaskbarDockPosition.Left,new Rect(sharedGap.Start,leftSlot.BoundsPixels.Top,width,height));
                traySlot=new(TaskbarDockPosition.Tray,new Rect(sharedGap.End-width,traySlot.BoundsPixels.Top,width,height));
            }
        }
        // Exactly identical frames do not offer a second physical destination.
        if(traySlot is not null&&leftSlot is not null&&traySlot.BoundsPixels==leftSlot.BoundsPixels) leftSlot=null;
        return new(traySlot,leftSlot);
    }
}
