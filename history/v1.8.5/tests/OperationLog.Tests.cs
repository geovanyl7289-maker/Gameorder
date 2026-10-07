using System.Text;
using System.Text.Json;
using Gameorder;

internal static class OperationLogTests
{
    private const string Folder = "ed3e8516-49d3-4673-b89a-ef0ef6b424d6";
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    public static IEnumerable<(string Name, Func<Task> Run)> Tests =>
    [
        Case("Legacy operation JSON keeps real records and infers switching category", () =>
        {
            const string json = """
                {"Activity":[{"Title":"已请求在线启动","Description":"Alpha · 实际登录结果请查看 Steam 客户端","Kind":"success","Time":"2023-11-14T22:13:20+00:00"}]}
                """;
            var state = JsonSerializer.Deserialize<LocalState>(json)!;
            Assert(state.Activity.Count == 1);
            var entry = state.Activity.Single();
            Assert(entry.Category == "" && entry.EffectiveCategory == "login" && entry.CategoryLabel == "账号切换");
            Assert(entry.Title == "已请求在线启动" && entry.Time == Epoch);
        }),
        Case("Existing online offline failed and restored switches retain category inference", () =>
        {
            foreach (var title in new[] { "已请求在线启动", "已请求离线启动", "在线启动 · 演示", "离线启动 · 演示", "切换未完成", "恢复切换配置", "在线登录", "离线登录", "账号切换" })
                Assert(OperationLogService.CategoryOf(Entry(title)) == "login");
        }),
        Case("Unknown legacy record remains other without fabricated operations", () =>
        {
            var entry = Entry("资料已保存", description: "原有活动记录");
            var original = JsonSerializer.Serialize(entry);
            Assert(OperationLogService.CategoryOf(entry) == "other");
            Assert(JsonSerializer.Serialize(entry) == original && entry.Category == "");
        }),
        Case("Explicit operation category wins over title inference", () =>
        {
            var entry = Entry("已请求在线启动", " Settings ");
            Assert(OperationLogService.CategoryOf(entry) == "settings" && entry.Category == " Settings ");
            Assert(entry.CategoryLabel == "软件设置");
        }),
        Case("Future unknown categories are preserved and treated as other", () =>
        {
            var entry = Entry("账号切换", "future-category");
            Assert(OperationLogService.CategoryOf(entry) == "other" && entry.Category == "future-category");
        }),
        Case("Operation filter separates login account group and settings", () =>
        {
            var state = State();
            Assert(state.Activity.Count(e => OperationLogService.Matches(e, "login")) == 2);
            Assert(state.Activity.Count(e => OperationLogService.Matches(e, "account")) == 1);
            Assert(state.Activity.Count(e => OperationLogService.Matches(e, "group")) == 1);
            Assert(state.Activity.Count(e => OperationLogService.Matches(e, "settings")) == 1);
            Assert(state.Activity.Count(e => OperationLogService.Matches(e, "other")) == 1);
            Assert(state.Activity.All(e => OperationLogService.Matches(e, " ALL ")));
        }),
        Case("Operation search matches multiple words across title description and category", () =>
        {
            var entry = Entry("备注已更新", "account", "Alpha · 主账号");
            Assert(OperationLogService.Matches(entry, "account", " alpha 备注 "));
            Assert(OperationLogService.Matches(entry, "all", "ALPHA 主账号 账号管理"));
            Assert(!OperationLogService.Matches(entry, "login", "alpha"));
            Assert(!OperationLogService.Matches(entry, "account", "alpha Beta"));
        }),
        Case("Whitespace queries match and unsupported filters never widen clear scope", () =>
        {
            var entry = Entry("备注已更新", "account", "Alpha");
            Assert(OperationLogService.Matches(entry, null, "\t \r\n"));
            Assert(!OperationLogService.Matches(entry, "acount", "Alpha"));
        }),
        Case("Clearing category removes legacy and explicit matching records only", () =>
        {
            var source = State();
            var candidate = OperationLogService.CreateClearCandidate(source, "login");
            Assert(source.Activity.Count == 6 && candidate.Activity.Count == 4);
            Assert(candidate.Activity.All(e => e.EffectiveCategory != "login"));
            Assert(candidate.Activity.Select(e => e.Title).SequenceEqual(new[] { "备注已更新", "分组已创建", "任务栏设置已保存", "已有旧记录" }));
        }),
        Case("Clearing current category and search preserves other matching-category records", () =>
        {
            var source = State();
            source.Activity.Add(Entry("备注已更新", "account", "Beta"));
            var candidate = OperationLogService.CreateClearCandidate(source, "account", "alpha 备注");
            Assert(source.Activity.Count == 7 && candidate.Activity.Count == 6);
            Assert(candidate.Activity.Any(e => e.Category == "account" && e.Description == "Beta"));
            Assert(!candidate.Activity.Any(e => e.Category == "account" && e.Description.Contains("Alpha", StringComparison.Ordinal)));
        }),
        Case("Clearing all records changes only operation history", () =>
        {
            var source = State();
            var candidate = OperationLogService.CreateClearCandidate(source);
            Assert(candidate.Activity.Count == 0 && source.Activity.Count == 6);
            Assert(candidate.SteamPath == source.SteamPath && !candidate.TaskbarWidgetEnabled && candidate.TaskbarWidgetDock == "left");
            Assert(candidate.Accounts.Single().Note == "保留账号备注" && candidate.Accounts.Single().IsFavorite && candidate.Accounts.Single().IsManual);
            Assert(candidate.Folders.Single().Id == Folder && candidate.Folders.Single().Name == "主账号");
            Assert(candidate.Accounts.Single().FolderId == Folder && candidate.HiddenAccounts.SequenceEqual(source.HiddenAccounts));
        }),
        Case("Unsupported clear filter cannot delete history", () =>
        {
            var source = State();
            var candidate = OperationLogService.CreateClearCandidate(source, "unknown-filter");
            Assert(candidate.Activity.Count == source.Activity.Count);
            Assert(candidate.Activity.Select(e => e.Title).SequenceEqual(source.Activity.Select(e => e.Title)));
        }),
        Case("Cancelled clear candidate is deep and cannot mutate source data", () =>
        {
            var source = State();
            var candidate = OperationLogService.CreateClearCandidate(source, "group");
            candidate.Accounts[0].Note = "candidate-only";
            candidate.Folders[0].Name = "candidate-only";
            candidate.HiddenAccounts.Add("candidate-only");
            candidate.Activity[0].Description = "candidate-only";
            candidate.Activity[0].Category = "settings";
            Assert(source.Accounts[0].Note == "保留账号备注" && source.Folders[0].Name == "主账号" && source.HiddenAccounts.Count == 1);
            Assert(source.Activity[0].Description == "Alpha" && source.Activity[0].Category == "" && source.Activity.Count == 6);
        }),
        Case("History candidate preserves raw categories timestamps kinds and ordering", () =>
        {
            var source = State();
            source.Activity[5].Category = "future-category";
            var retained = source.Activity.Where(e => e.EffectiveCategory != "group").ToArray();
            var candidate = OperationLogService.CreateClearCandidate(source, "group");
            Assert(candidate.Activity.Count == retained.Length);
            for (var i = 0; i < retained.Length; i++)
            {
                var copy = candidate.Activity[i];
                var original = retained[i];
                Assert(!ReferenceEquals(copy, original) && copy.Title == original.Title && copy.Description == original.Description
                    && copy.Time == original.Time && copy.Kind == original.Kind && copy.Category == original.Category);
            }
        }),
        Case("History clear does not normalize an unrelated raw dock preference", () =>
        {
            var source = State();
            source.TaskbarWidgetDock = "future-dock";
            Assert(OperationLogService.CreateClearCandidate(source, "group").TaskbarWidgetDock == "future-dock");
            Assert(source.TaskbarWidgetDock == "future-dock");
        }),
        Case("Computed operation category labels are not persisted as extra fields", () =>
        {
            var json = JsonSerializer.Serialize(Entry("备注已更新", "account"));
            Assert(json.Contains("\"Category\":\"account\"", StringComparison.Ordinal));
            Assert(!json.Contains("CategoryLabel", StringComparison.Ordinal) && !json.Contains("EffectiveCategory", StringComparison.Ordinal));
        }),
        Case("Atomic local store roundtrip preserves new operation categories", () =>
        {
            using var f = new StoreFixture();
            var source = State();
            var store = new LocalStore(false, f.Path);
            store.Save(source);
            var reloaded = store.Load();
            Assert(store.LoadError is null && reloaded.Activity.Count == 6);
            Assert(reloaded.Activity[1].Category == "account" && reloaded.Activity[2].Category == "group" && reloaded.Activity[3].Category == "settings");
            var candidate = OperationLogService.CreateClearCandidate(reloaded, "group");
            store.Save(candidate);
            Assert(store.Load().Activity.Count == 5 && reloaded.Activity.Count == 6);
        })
    ];
    private static ActivityEntry Entry(string title, string category = "", string description = "", int seconds = 0) =>
        new() { Title = title, Description = description, Category = category, Time = Epoch.AddSeconds(seconds), Kind = "success" };
    private static LocalState State() => new()
    {
        SteamPath = @"D:\Steam",
        TaskbarWidgetEnabled = false,
        TaskbarWidgetDock = "left",
        Accounts = [new() { AccountName = "alpha", DisplayName = "主账号", Note = "保留账号备注", IsFavorite = true, IsManual = true, FolderId = Folder }],
        Folders = [new() { Id = Folder, Name = "主账号" }],
        HiddenAccounts = ["hidden"],
        Activity =
        [
            Entry("已请求在线启动", description: "Alpha", seconds: 6),
            Entry("备注已更新", "account", "Alpha", 5),
            Entry("分组已创建", "group", "主账号", 4),
            Entry("任务栏设置已保存", "settings", "已关闭任务栏小组件", 3),
            Entry("已请求离线启动", "login", "Beta", 2),
            Entry("已有旧记录", description: "保留原始记录", seconds: 1)
        ]
    };
    private static (string, Func<Task>) Case(string name, Action run) => (name, () => { run(); return Task.CompletedTask; });
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Operation log assertion failed"); }
    private sealed class StoreFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Gameorder-operation-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_root, "accounts.json");
        public StoreFixture() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(_root);
            var temporary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(target).StartsWith("Gameorder-operation-tests-", StringComparison.Ordinal))
                throw new IOException("Unsafe operation test cleanup target.");
            Directory.Delete(target, true);
        }
    }
}