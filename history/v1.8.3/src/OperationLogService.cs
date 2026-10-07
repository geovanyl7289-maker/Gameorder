namespace Gameorder;

/// <summary>Pure filtering and clear candidates. Saving and publishing a candidate belongs to the view model.</summary>
public static class OperationLogService
{
    public const string All = "all";
    public const string Login = "login";
    public const string Account = "account";
    public const string Group = "group";
    public const string Settings = "settings";
    public const string Other = "other";

    public static string CategoryOf(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var explicitCategory = entry.Category?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(explicitCategory))
            return IsCategory(explicitCategory) ? explicitCategory : Other;
        // Before categories existed, only these real switching/login events were recorded.
        // Keep unknown historical entries unchanged rather than inventing account/settings operations.
        var title = entry.Title?.Trim() ?? "";
        if (title.StartsWith("已请求在线启动", StringComparison.Ordinal)
            || title.StartsWith("已请求离线启动", StringComparison.Ordinal)
            || title.StartsWith("在线启动", StringComparison.Ordinal)
            || title.StartsWith("离线启动", StringComparison.Ordinal)
            || title is "切换未完成" or "恢复切换配置" or "在线登录" or "离线登录" or "账号切换")
            return Login;
        return Other;
    }
    public static string CategoryLabel(ActivityEntry entry) => CategoryLabel(CategoryOf(entry));
    public static string CategoryLabel(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        All => "全部操作",
        Login => "账号切换",
        Account => "账号管理",
        Group => "分组管理",
        Settings => "软件设置",
        _ => "其他操作"
    };
    public static bool Matches(ActivityEntry entry, string? category = All, string? search = "")
    {
        ArgumentNullException.ThrowIfNull(entry);
        var selected = string.IsNullOrWhiteSpace(category) ? All : category.Trim().ToLowerInvariant();
        // An unsupported filter never expands to all, especially when this predicate is used for clearing.
        if (selected != All && (!IsCategory(selected) || CategoryOf(entry) != selected)) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        var parts = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var title = entry.Title ?? "";
        var description = entry.Description ?? "";
        var label = CategoryLabel(entry);
        return parts.All(part => title.Contains(part, StringComparison.OrdinalIgnoreCase)
            || description.Contains(part, StringComparison.OrdinalIgnoreCase)
            || label.Contains(part, StringComparison.OrdinalIgnoreCase));
    }
    public static LocalState CreateClearCandidate(LocalState source, string? category = All, string? search = "")
    {
        ArgumentNullException.ThrowIfNull(source);
        var candidate = LocalAccountLibrary.Copy(source);
        // A history-only operation must not normalize an unrelated preference in its reviewable candidate.
        candidate.TaskbarWidgetDock = source.TaskbarWidgetDock;
        candidate.Activity = source.Activity.Where(entry => !Matches(entry, category, search))
            .Select(Clone).ToList();
        return candidate;
    }
    private static bool IsCategory(string category) => category is Login or Account or Group or Settings or Other;
    private static ActivityEntry Clone(ActivityEntry entry) => new()
    {
        Title = entry.Title,
        Description = entry.Description,
        Time = entry.Time,
        Kind = entry.Kind,
        Category = entry.Category
    };
}