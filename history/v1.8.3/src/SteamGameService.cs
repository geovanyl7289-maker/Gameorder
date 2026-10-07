using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gameorder;

public sealed record SteamGameSnapshot(string SteamId, uint AppId, string GameName, bool IsRunning, bool IsKnown, string Hint = "")
{
    public static SteamGameSnapshot None(string steamId = "", bool isKnown = false) =>
        new(steamId, 0, "", false, isKnown, isKnown ? "当前账号未运行游戏" : "游戏状态尚未确认");
}
public sealed record SteamGameEvidence(uint AppId, bool IsKnown, string Hint);

/// <summary>Read-only observations, tied to the current Steam process. Fixtures replace all operating-system access.</summary>
public interface ISteamGamePlatform
{
    SteamProcessEvidence ReadProcess(string steamPath);
    SteamGameEvidence ReadGame(string steamPath, DateTimeOffset clientStartedAt);
    string? ReadGameName(string steamPath, uint appId);
}

public sealed class SteamGameService(ISteamGamePlatform? platform = null)
{
    private const ulong IndividualSteamIdBase = 76561197960265728;
    private readonly ISteamGamePlatform _platform = platform ?? new WindowsSteamGamePlatform();

    public SteamGameSnapshot Read(string steamPath, SteamSessionSnapshot session)
    {
        if (!session.ClientRunning || session.ActiveSteamId is null)
            return SteamGameSnapshot.None(isKnown: !session.ClientRunning && session.State == SteamSessionState.Stopped);
        // Connection state may be unknown or offline, but the local ActiveUser must still be confirmed.
        if (!ulong.TryParse(session.ActiveSteamId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= IndividualSteamIdBase || id > IndividualSteamIdBase + uint.MaxValue)
            return SteamGameSnapshot.None();
        try
        {
            var before = _platform.ReadProcess(steamPath);
            if (!Matches(before, id)) return SteamGameSnapshot.None();
            var game = _platform.ReadGame(steamPath, before.StartedAt!.Value);
            var name = !game.IsKnown || game.AppId == 0 ? "" : _platform.ReadGameName(steamPath, game.AppId);
            // Recheck both the game and identity after title I/O; an account/game may change while reading a manifest.
            var afterGame = _platform.ReadGame(steamPath, before.StartedAt.Value);
            var after = _platform.ReadProcess(steamPath);
            if (before != after || !Matches(after, id) || game.AppId != afterGame.AppId || game.IsKnown != afterGame.IsKnown)
                return SteamGameSnapshot.None();
            if (!game.IsKnown || game.AppId == 0)
                return SteamGameSnapshot.None(session.ActiveSteamId, game.IsKnown) with { Hint = game.Hint };
            var label = CleanName(name) ?? (game.AppId >= 0x80000000 ? "Steam 正在游戏" : $"Steam 游戏 · {game.AppId}");
            return new(session.ActiveSteamId, game.AppId, label, true, true, game.Hint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or FormatException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return SteamGameSnapshot.None() with { Hint = "暂时无法确认 Steam 游戏状态" }; }
    }
    private static bool Matches(SteamProcessEvidence process, ulong expected) => process.ClientRunning
        && process.ActiveProcessMatches && process.StartedAt.HasValue && process.ConfirmedAccountId != 0
        && IndividualSteamIdBase + process.ConfirmedAccountId == expected;
    internal static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, 160)];
    }
}

public sealed class WindowsSteamGamePlatform : ISteamGamePlatform
{
    private readonly ISteamSessionPlatform _sessions;
    private readonly Func<DateTimeOffset> _now;
    private readonly SteamGameFiles _files;
    private readonly SteamGameTrackingCache _tracking;
    public WindowsSteamGamePlatform(ISteamSessionPlatform? sessions = null, Func<DateTimeOffset>? utcNow = null)
    {
        _sessions = sessions ?? new WindowsSteamSessionPlatform();
        _now = utcNow ?? (() => DateTimeOffset.UtcNow);
        _files = new SteamGameFiles(_now);
        _tracking = new SteamGameTrackingCache(_now);
    }
    public SteamProcessEvidence ReadProcess(string steamPath)
    {
        var process = _sessions.ReadProcess(steamPath);
        _tracking.ObserveIdentity(process);
        return process;
    }
    public string? ReadGameName(string steamPath, uint appId) => _files.ReadGameName(steamPath, appId);
    public SteamGameEvidence ReadGame(string steamPath, DateTimeOffset clientStartedAt)
    {
        if (!Path.IsPathFullyQualified(steamPath)) return new(0, false, "Steam 路径尚未确认");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath));
        var registry = ReadRegistry();
        // A single larger bootstrap lets a newly opened Gameorder recognize a game that was already running.
        // Subsequent reads remain at 256 KiB and retain only currently verified Steam-tracked processes.
        var log = SteamGameFiles.ReadTail(Path.Combine(root, "logs", "gameprocess_log.txt"), _tracking.PrepareRead(root, clientStartedAt));
        var tracked = _tracking.Collect(log, clientStartedAt, p => IsAlive(p, clientStartedAt));
        return SteamGameLog.Resolve(registry, log is not null, tracked, _ => true);
    }
    private static SteamGameRegistryEvidence ReadRegistry()
    {
        using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", false);
        if (steam is null) return new(null, [], false);
        var selected = steam.GetValue("RunningAppID") is int id ? unchecked((uint)id) : (uint?)null;
        var running = new List<uint>();
        using var apps = steam.OpenSubKey("Apps", false);
        if (apps is not null)
        {
            // Installed entries are never running evidence: only the explicit Running value is read.
            foreach (var key in apps.GetSubKeyNames().Take(2048))
            {
                if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0) continue;
                using var app = apps.OpenSubKey(key, false);
                if (app?.GetValue("Running") is int value && value == 1) running.Add(appId);
            }
        }
        return new(selected, running, true);
    }
    private static bool IsAlive(SteamTrackedGameProcess tracked, DateTimeOffset clientStartedAt)
    {
        try
        {
            using var process = Process.GetProcessById(tracked.Pid);
            var start = new DateTimeOffset(process.StartTime);
            // Prevent PID reuse from reviving an old Steam tracking line.
            return !process.HasExited && start >= clientStartedAt && start <= tracked.AddedAt.AddSeconds(1)
                && string.Equals(process.MainModule?.FileName, tracked.Executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }
}

/// <summary>Bounded read windows plus an observed account epoch; old launch rows cannot cross an account switch.</summary>
internal sealed class SteamGameTrackingCache(Func<DateTimeOffset> utcNow)
{
    internal const int BootstrapBytes = 8 * 1024 * 1024;
    internal const int TailBytes = 256 * 1024;
    private SteamProcessEvidence? _lastObservation;
    private DateTimeOffset? _confirmedClientStartedAt;
    private uint? _confirmedAccount;
    private DateTimeOffset? _identityFence;
    private string? _root;
    private DateTimeOffset? _startedAt;
    private bool _bootstrapPending = true;
    private IReadOnlyList<SteamTrackedGameProcess> _tracked = [];

    internal void ObserveIdentity(SteamProcessEvidence process)
    {
        if (_lastObservation != process)
        {
            _tracked = [];
            _bootstrapPending = true;
        }
        _lastObservation = process;
        if (!process.ClientRunning || !process.ActiveProcessMatches || !process.StartedAt.HasValue) return;
        if (_confirmedClientStartedAt != process.StartedAt)
        {
            _confirmedClientStartedAt = process.StartedAt;
            _confirmedAccount = process.ConfirmedAccountId;
            _identityFence = null;
        }
        else if (_confirmedAccount != process.ConfirmedAccountId)
        {
            // Keep the last confirmed identity through an unreadable observation. This also fences logout -> login.
            _confirmedAccount = process.ConfirmedAccountId;
            _identityFence = utcNow();
        }
    }
    internal int PrepareRead(string root, DateTimeOffset startedAt)
    {
        if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) || _startedAt != startedAt)
        {
            _root = root;
            _startedAt = startedAt;
            _tracked = [];
            _bootstrapPending = true;
        }
        return _bootstrapPending ? BootstrapBytes : TailBytes;
    }
    internal IReadOnlyList<SteamTrackedGameProcess> Collect(string? log, DateTimeOffset clientStartedAt, Func<SteamTrackedGameProcess, bool> isAlive)
    {
        // Retry a failed bootstrap after the log becomes readable; successful reads consume the larger window.
        _bootstrapPending = log is null;
        var earliest = _identityFence.HasValue && _identityFence > clientStartedAt ? _identityFence.Value : clientStartedAt;
        _tracked = SteamGameLog.FindTrackedProcesses(log, earliest, utcNow(), log is null ? [] : _tracked)
            .Where(isAlive).Take(256).ToArray();
        return _tracked;
    }
}
internal sealed record SteamGameRegistryEvidence(uint? RunningAppId, IReadOnlyList<uint> RunningAppIds, bool Available);
internal sealed record SteamTrackedGameProcess(uint AppId, int Pid, string Executable, DateTimeOffset AddedAt);
internal static class SteamGameLog
{
    private static readonly Regex Prefix = new(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*(?<body>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Add = new(@"^AppID (?<app>\d+) adding PID (?<pid>\d+) as a tracked process (?<command>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex RemovePid = new(@"^AppID (?<app>\d+) no longer tracking PID (?<pid>\d+)\b", RegexOptions.CultureInvariant);
    private static readonly Regex RemoveApp = new(@"^Remove (?<app>\d+) from running list\b", RegexOptions.CultureInvariant);
    private static readonly Regex Executable = new("^[\\s\"]*(?<exe>.+?\\.exe)(?:\"|\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<SteamTrackedGameProcess> FindTrackedProcesses(string? log, DateTimeOffset clientStartedAt,
        DateTimeOffset now, IReadOnlyList<SteamTrackedGameProcess>? previous = null)
    {
        var processes = (previous ?? []).Where(p => p.AddedAt >= clientStartedAt && p.AddedAt <= now.AddSeconds(5))
            .ToDictionary(p => (App: p.AppId, p.Pid));
        if (log is null) return [];
        using var reader = new StringReader(log);
        while (reader.ReadLine() is { } line)
        {
            var prefix = Prefix.Match(line);
            if (!prefix.Success || !DateTimeOffset.TryParseExact(prefix.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
                || timestamp < clientStartedAt || timestamp > now.AddSeconds(5)) continue;
            var body = prefix.Groups["body"].Value;
            var add = Add.Match(body);
            if (add.Success && uint.TryParse(add.Groups["app"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId != 0
                && int.TryParse(add.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
            {
                var executable = Executable.Match(add.Groups["command"].Value);
                if (!executable.Success || !Path.IsPathFullyQualified(executable.Groups["exe"].Value)) continue;
                try { processes[(appId, pid)] = new(appId, pid, Path.GetFullPath(executable.Groups["exe"].Value), timestamp); }
                catch (ArgumentException) { }
                continue;
            }
            var remove = RemovePid.Match(body);
            if (remove.Success && uint.TryParse(remove.Groups["app"].Value, out appId) && int.TryParse(remove.Groups["pid"].Value, out pid))
                processes.Remove((appId, pid));
            var removeApp = RemoveApp.Match(body);
            if (removeApp.Success && uint.TryParse(removeApp.Groups["app"].Value, out appId))
                foreach (var key in processes.Keys.Where(p => p.App == appId).ToArray()) processes.Remove(key);
        }
        return processes.Values.OrderByDescending(p => p.AddedAt).ToArray();
    }
    internal static SteamGameEvidence Resolve(SteamGameRegistryEvidence registry, bool hasLog,
        IReadOnlyList<SteamTrackedGameProcess> tracked, Func<SteamTrackedGameProcess, bool> isAlive)
    {
        var live = tracked.Where(isAlive).ToArray();
        var selected = registry.RunningAppId.GetValueOrDefault();
        var app = live.FirstOrDefault(p => selected != 0 && p.AppId == selected)
            ?? live.FirstOrDefault(p => registry.RunningAppIds.Contains(p.AppId)) ?? live.FirstOrDefault();
        if (app is not null) return new(app.AppId, true, "Steam 当前客户端跟踪的游戏进程正在运行");
        // A retained registry Running flag alone is insufficient. It can survive a previous game/client session.
        if (selected != 0 || registry.RunningAppIds.Count != 0)
            return new(0, false, "Steam 游戏标志尚无本次客户端的活跃进程证据");
        return new(0, hasLog && registry.Available, hasLog && registry.Available ? "当前账号未运行游戏" : "游戏状态尚未确认");
    }
}

internal sealed class SteamGameFiles(Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<(string Path, uint App), (DateTimeOffset Expires, string? Name)> _names = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, string[] Paths)> _libraries = new(StringComparer.OrdinalIgnoreCase);
    public string? ReadGameName(string steamPath, uint appId)
    {
        if (appId == 0 || !Path.IsPathFullyQualified(steamPath)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath));
        var key = (root, appId);
        if (_names.TryGetValue(key, out var cached) && cached.Expires > _now()) return cached.Name;
        string? name = null;
        foreach (var library in Libraries(root))
        {
            var text = ReadBounded(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"), 1024 * 1024);
            if (text is null) continue;
            try
            {
                var app = ValveKeyValues.Parse(text).Find("AppState");
                if (app is null || app.GetValue("appid") != appId.ToString(CultureInfo.InvariantCulture)) continue;
                name = SteamGameService.CleanName(app.GetValue("name"));
                if (name is not null) break;
            }
            catch (FormatException) { }
        }
        if (_names.Count >= 128) _names.Clear();
        _names[key] = (_now().AddSeconds(name is null ? 30 : 300), name);
        return name;
    }
    private string[] Libraries(string root)
    {
        if (_libraries.TryGetValue(root, out var cached) && cached.Expires > _now()) return cached.Paths;
        var paths = new List<string> { root };
        var text = ReadBounded(Path.Combine(root, "steamapps", "libraryfolders.vdf"), 2 * 1024 * 1024);
        if (text is not null)
        {
            try
            {
                var entries = ValveKeyValues.Parse(text).Find("libraryfolders")?.Children;
                if (entries is not null)
                    foreach (var entry in entries.Take(64))
                    {
                        if (!uint.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                        var path = entry.IsObject ? entry.GetValue("path") : entry.Value;
                        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
                        try { paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))); }
                        catch (ArgumentException) { }
                    }
            }
            catch (FormatException) { }
        }
        var result = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_libraries.Count >= 8) _libraries.Clear();
        _libraries[root] = (_now().AddMinutes(5), result);
        return result;
    }
    internal static string? ReadBounded(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var chars = new char[maximumBytes + 1];
            var count = reader.ReadBlock(chars, 0, chars.Length);
            return count <= maximumBytes ? new string(chars, 0, count) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return null; }
    }
    internal static string? ReadTail(string path, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - maximumBytes);
            stream.Position = offset;
            var bytes = new byte[maximumBytes];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            var text = Encoding.UTF8.GetString(bytes, 0, count);
            if (offset == 0) return text;
            var newline = text.IndexOf('\n');
            return newline < 0 ? "" : text[(newline + 1)..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return null; }
    }
}