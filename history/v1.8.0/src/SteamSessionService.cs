using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gameorder;

public enum SwitchMode { Online, Offline }
public enum SteamSessionState { Stopped, AwaitingLogin, Unknown, Online, Offline }
public sealed record SteamSessionSnapshot(bool ClientRunning, string? ActiveSteamId, SteamSessionState State, string Hint)
{
    public bool IsOnline => ActiveSteamId is not null && State == SteamSessionState.Online;
    public bool IsOffline => ActiveSteamId is not null && State == SteamSessionState.Offline;
}
public sealed record SteamProcessEvidence(bool ClientRunning, bool ActiveProcessMatches, uint ActiveAccountId, DateTimeOffset? StartedAt);

/// <summary>Only local, read-only process, registry and log observations. Tests inject fixtures.</summary>
public interface ISteamSessionPlatform
{
    SteamProcessEvidence ReadProcess(string steamPath);
    string? ReadConnectionLogTail(string steamPath);
}

public sealed class SteamSessionService(ISteamSessionPlatform? platform = null, Func<DateTimeOffset>? utcNow = null)
{
    private const ulong IndividualSteamIdBase = 76561197960265728;
    private readonly ISteamSessionPlatform _platform = platform ?? new WindowsSteamSessionPlatform();
    private readonly Func<DateTimeOffset> _now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private static readonly Regex LogPrefix = new(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\s*\[(?<state>[^,\]]+),[^\]]*\]\s*\[U:1:(?<account>\d+)\]", RegexOptions.CultureInvariant);

    public SteamSessionSnapshot Read(string steamPath)
    {
        try
        {
            var process = _platform.ReadProcess(steamPath);
            if (!process.ClientRunning) return new(false, null, SteamSessionState.Stopped, "Steam 未启动");
            if (!process.ActiveProcessMatches || !process.StartedAt.HasValue)
                return new(true, null, SteamSessionState.Unknown, "Steam 正在运行，当前账号尚未确认");
            if (process.ActiveAccountId == 0)
                return new(true, null, SteamSessionState.AwaitingLogin, "请在 Steam 完成登录或验证");
            var steamId = (IndividualSteamIdBase + process.ActiveAccountId).ToString(CultureInfo.InvariantCulture);
            var log = _platform.ReadConnectionLogTail(steamPath);
            // A logout/switch can happen while the log is being read. Never assign the previous session to a new user.
            if (_platform.ReadProcess(steamPath) != process)
                return new(true, null, SteamSessionState.AwaitingLogin, "Steam 登录状态正在变化");
            var latest = LatestConnectionState(log, process.StartedAt.Value, _now());
            if (latest is null || latest.Value.AccountId != process.ActiveAccountId)
                return new(true, steamId, SteamSessionState.Unknown, "当前账号已识别；尚无本次客户端的连接状态");
            return latest.Value.State switch
            {
                SteamSessionState.Online => new(true, steamId, SteamSessionState.Online, "本机 Steam 日志显示已连接并登录；好友隐身不影响此状态"),
                SteamSessionState.Offline => new(true, steamId, SteamSessionState.Offline, "本机 Steam 日志显示离线或连接断开；可能是离线模式或网络中断"),
                SteamSessionState.AwaitingLogin => new(true, steamId, SteamSessionState.AwaitingLogin, "Steam 正在连接或等待登录验证"),
                _ => new(true, steamId, SteamSessionState.Unknown, "已识别当前账号，连接状态待确认")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return new(false, null, SteamSessionState.Unknown, "无法读取 Steam 当前会话，请刷新或查看 Steam 客户端"); }
    }

    private static (uint AccountId, SteamSessionState State)? LatestConnectionState(string? log, DateTimeOffset startedAt, DateTimeOffset now)
    {
        (uint AccountId, SteamSessionState State)? latest = null;
        if (log is null) return null;
        using var reader = new StringReader(log);
        while (reader.ReadLine() is { } line)
        {
            var match = LogPrefix.Match(line);
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
                || timestamp < startedAt || timestamp > now.AddSeconds(5)
                || !uint.TryParse(match.Groups["account"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)) continue;
            var state = match.Groups["state"].Value.Trim();
            SteamSessionState? observed = state.ToLowerInvariant() switch
            {
                "logged on" => SteamSessionState.Online,
                "offline" or "no connection" or "disconnected" => SteamSessionState.Offline,
                "connecting" or "connected" or "logging on" or "logged off" or "logging off" => SteamSessionState.AwaitingLogin,
                _ => null
            };
            // The prefix can still say Logged On on the line that tears down its connection.
            if (line.Contains("Disconnected(", StringComparison.OrdinalIgnoreCase)) observed = SteamSessionState.Offline;
            var body = line[match.Length..];
            if (Regex.IsMatch(body, @"\b(?:LogOff|Logout|Logged Off|Logging Off)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                observed = SteamSessionState.AwaitingLogin;
            if (observed.HasValue) latest = (accountId, observed.Value);
        }
        return latest;
    }
}

public sealed class WindowsSteamSessionPlatform : ISteamSessionPlatform
{
    public SteamProcessEvidence ReadProcess(string steamPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath) || !Path.IsPathFullyQualified(steamPath)) return new(false, false, 0, null);
        var expected = Path.GetFullPath(Path.Combine(steamPath, "steam.exe"));
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess", false);
        var pid = key?.GetValue("pid") is int value ? value : 0;
        var activeUser = key?.GetValue("ActiveUser") is int user ? unchecked((uint)user) : 0;
        if (pid > 0)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (Matches(process, expected)) return new(true, true, activeUser, new DateTimeOffset(process.StartTime));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var running = false;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try { running |= Matches(process, expected); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return new(running, false, 0, null);
    }
    private static bool Matches(Process process, string expected) => !process.HasExited && process.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase)
        && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase);

    public string? ReadConnectionLogTail(string steamPath)
    {
        var path = Path.Combine(steamPath, "logs", "connection_log.txt");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int maximumBytes = 256 * 1024;
        var offset = Math.Max(0, stream.Length - maximumBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        var bytes = new byte[maximumBytes];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        var text = Encoding.UTF8.GetString(bytes, 0, read);
        if (offset > 0)
        {
            var newline = text.IndexOf('\n'); // The first line may have been cut in the middle.
            return newline < 0 ? "" : text[(newline + 1)..];
        }
        return text;
    }
}
