using System.Reflection;

namespace Gameorder;

/// <summary>Bundled attribution and release notes; viewing About never contacts a server.</summary>
public static class AboutInfo
{
    public const string AuthorName = "XIAOYING223";
    public const string DevelopmentHistoryLabel = "开发历程 · 0.1–1.8";
    public const string DevelopmentHistoryNotice = "";
    public static string Version { get; } = ReadVersion();
    public static IReadOnlyList<ReleaseNote> ReleaseNotes { get; } = Array.AsReadOnly(new[]
    {
        new ReleaseNote(Version, "离线状态与后台节能", Array.AsReadOnly(new[]
        {
            "修正离线登录状态显示，账号头像、快捷头像栏与任务栏入口同步标注。",
            "主界面与快捷面板都隐藏时减少后台活动，打开界面后恢复响应。",
            "状态读取移至后台，避免重复刷新界面与托盘。",
            "保留游戏期间收起组件的行为，不改变显示开关与停靠位置。"
        }), true),
        new ReleaseNote("1.8.2", "状态体验与操作记录", Array.AsReadOnly(new[]
        {
            "游戏运行时托盘显示绿色优化状态，右键菜单保留打开主界面入口。",
            "启动时自动确认 Steam 当前会话，没有已确认在线账号时显示离线提示，保留账号头像和任务栏组件。",
            "普通状态提示自动消失，加载与确认操作保持显示。",
            "切换记录扩展为操作记录，支持类型筛选、关键词搜索和确认后清除当前结果。"
        }), false),
        new ReleaseNote("1.8.1", "游戏感知与后台节能", Array.AsReadOnly(new[]
        {
            "根据本机 Steam 当前账号的运行状态，显示正在运行的游戏。",
            "游戏运行时减少后台刷新、降低 Gameorder 进程优先级，并暂时隐藏任务栏小组件。",
            "游戏结束后恢复小组件与正常刷新，保留原有开关和停靠位置。",
            "加入从 0.1 到 1.8 的功能开发历程。"
        }), false),
        new ReleaseNote("1.8.0", "作者与关于", Array.AsReadOnly(new[]
        {
            "新增关于页面，展示作者头像与署名。",
            "加入更新日志，集中查看版本变化。"
        }), false),
        new ReleaseNote("1.7.0", "任务栏自定义", Array.AsReadOnly(new[]
        {
            "设置中可开启或隐藏任务栏小组件。",
            "支持左侧与托盘侧两个停靠位置。",
            "拖动靠近停靠区域时显示虚线预览，松手吸附并保存位置。"
        }), false)
    });

    public static IReadOnlyList<ReleaseNote> DevelopmentStages { get; } = Array.AsReadOnly(new[]
    {
        Stage("0.1", "Steam 账号导入", "识别 Steam 安装位置，导入本机已保存的账号。"),
        Stage("0.2", "账号切换", "选择目标账号后确认切换，由 Steam 完成登录。"),
        Stage("0.3", "在线登录", "提供在线启动入口，密码与 Steam Guard 在官方窗口中完成。"),
        Stage("0.4", "离线登录", "为已有有效本机登录缓存的账号提供离线启动入口。"),
        Stage("0.5", "头像网格", "用独立头像卡片展示账号，便于快速辨认。"),
        Stage("0.6", "头像加载", "优先读取本机头像，缺少时查询公开头像并缓存。"),
        Stage("0.7", "当前会话状态", "区分当前账号的在线、离线、待登录与待确认状态。"),
        Stage("0.8", "账号备注", "在头像卡片与快捷入口显示备注，悬停查看长备注。"),
        Stage("0.9", "文件夹分组", "在软件内创建、重命名和删除分组，整理账号。"),
        Stage("1.0", "账号管理", "添加账号、编辑显示名称，并从账号库移出账号。"),
        Stage("1.1", "任务栏小组件", "在任务栏可用区域显示常驻账号入口。"),
        Stage("1.2", "快捷头像栏", "展开头像栏，搜索与筛选账号并发起切换。"),
        Stage("1.3", "自定义标题栏", "提供自绘最小化与关闭按钮，移除最大化按钮。"),
        Stage("1.4", "圆角托盘菜单", "使用紧凑圆角菜单，保留账号备注与常用操作。"),
        Stage("1.5", "组件显示开关", "在设置中开关任务栏组件，保存显示偏好。"),
        Stage("1.6", "拖动与吸附", "拖到左侧或托盘侧，显示同尺寸虚线预览并保存落点。"),
        Stage("1.7", "作者与关于", "展示 XIAOYING223 的头像、署名与更新记录。"),
        Stage("1.8", "游戏感知", "显示当前账号的运行游戏，游戏期间降低后台活动并暂时收起组件。")
    });

    private static ReleaseNote Stage(string version, string title, string change)
        => new(version, title, Array.AsReadOnly(new[] { change }), false);

    private static string ReadVersion()
    {
        var assembly = typeof(AboutInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "dev";
    }
}

public sealed record ReleaseNote(string Version, string Title, IReadOnlyList<string> Changes, bool IsLatest);