using System.Reflection;

namespace Gameorder;

/// <summary>Bundled attribution and release notes; viewing About never contacts a server.</summary>
public static class AboutInfo
{
    public const string AuthorName = "XIAOYING223";
    public static string Version { get; } = ReadVersion();
    public static IReadOnlyList<ReleaseNote> ReleaseNotes { get; } = Array.AsReadOnly(new[]
    {
        new ReleaseNote(Version, "作者与关于", Array.AsReadOnly(new[]
        {
            "新增关于页面，展示作者头像与署名。",
            "加入更新日志，集中查看版本变化。"
        }), true),
        new ReleaseNote("1.7.0", "任务栏自定义", Array.AsReadOnly(new[]
        {
            "设置中可开启或隐藏任务栏小组件。",
            "支持左侧与托盘侧两个停靠位置。",
            "拖动靠近停靠区域时显示虚线预览，松手吸附并保存位置。"
        }), false)
    });

    private static string ReadVersion()
    {
        var assembly = typeof(AboutInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "dev";
    }
}

public sealed record ReleaseNote(string Version, string Title, IReadOnlyList<string> Changes, bool IsLatest);