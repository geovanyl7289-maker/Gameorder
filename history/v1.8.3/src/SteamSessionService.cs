using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Gameorder;

public enum SwitchMode { Online, Offline }
public enum SteamSessionState { Stopped, AwaitingLogin, Unknown, Online, Offline }
public sealed record SteamSessionSnapshot(bool ClientRunning, string? ActiveSteamId, SteamSessionState State, string Hint)
{
    public bool IsOnline => ActiveSteamId is not null && State == SteamSessionState.Online;
    public bool IsOffline => ActiveSteamId is not null && State == SteamSessionState.Offline;
}
public sealed record SteamLocalUserEvidence(uint AccountId, bool Connected, bool IdentityStable = true);
public sealed record SteamProcessEvidence(bool ClientRunning, bool ActiveProcessMatches, uint ActiveAccountId, DateTimeOffset? StartedAt,
    SteamLocalUserEvidence? LocalUser = null)
{
    public uint ConfirmedAccountId => !ClientRunning || !ActiveProcessMatches || !StartedAt.HasValue ? 0
        : LocalUser is null ? ActiveAccountId
        : LocalUser.IdentityStable && LocalUser.AccountId != 0 && (ActiveAccountId == 0 || ActiveAccountId == LocalUser.AccountId)
            ? LocalUser.AccountId : 0;
}

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
            var accountId = process.ConfirmedAccountId;
            if (accountId == 0)
                return new(true, null, process.LocalUser is null ? SteamSessionState.AwaitingLogin : SteamSessionState.Unknown,
                    process.LocalUser is null ? "请在 Steam 完成登录或验证" : "Steam 当前账号状态正在变化，请稍候确认");
            var steamId = (IndividualSteamIdBase + accountId).ToString(CultureInfo.InvariantCulture);
            // The current global user's Steam ID exists even when offline cached login does not set ActiveUser
            // or emit a fresh connection_log entry. BLoggedOn reports the actual server connection, not a preference.
            if (process.LocalUser is { IdentityStable: true } local)
            {
                if (_platform.ReadProcess(steamPath) != process)
                    return new(true, null, SteamSessionState.AwaitingLogin, "Steam 登录状态正在变化");
                return local.Connected
                    ? new(true, steamId, SteamSessionState.Online, "本机 Steam 当前用户接口确认已联网登录；好友隐身不影响此状态")
                    : new(true, steamId, SteamSessionState.Offline, "本机 Steam 当前用户接口确认已有本地登录且未连接服务器；可能是离线登录或网络断开");
            }
            var log = _platform.ReadConnectionLogTail(steamPath);
            // A logout/switch can happen while the log is being read. Never assign the previous session to a new user.
            if (_platform.ReadProcess(steamPath) != process)
                return new(true, null, SteamSessionState.AwaitingLogin, "Steam 登录状态正在变化");
            var latest = LatestConnectionState(log, process.StartedAt.Value, _now());
            if (latest is null || latest.Value.AccountId != accountId)
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
        // Steam log timestamps have whole-second precision; compare both sides at that precision.
        var earliest = new DateTimeOffset(startedAt.Ticks - startedAt.Ticks % TimeSpan.TicksPerSecond, startedAt.Offset);
        if (log is null) return null;
        using var reader = new StringReader(log);
        while (reader.ReadLine() is { } line)
        {
            var match = LogPrefix.Match(line);
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
                || timestamp < earliest || timestamp > now.AddSeconds(5)
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
    private readonly SteamConnectionLogCache _logs = new();
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
                if (Matches(process, expected))
                {
                    var startedAt = new DateTimeOffset(process.StartTime);
                    var local = SteamLocalUserReader.Read(steamPath);
                    // IPC is global to this Windows session; the expected Steam installation/PID must still match.
                    if (key?.GetValue("pid") is not int samePid || samePid != pid
                        || key.GetValue("ActiveUser") is not int sameUser || unchecked((uint)sameUser) != activeUser
                        || process.HasExited || new DateTimeOffset(process.StartTime) != startedAt)
                        return new(true, false, 0, null);
                    return new(true, true, activeUser, startedAt, local);
                }
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

    public string? ReadConnectionLogTail(string steamPath) => _logs.Read(Path.Combine(steamPath, "logs", "connection_log.txt"));
}

internal sealed class SteamConnectionLogCache
{
    private readonly object _gate = new();
    private string? _path, _text;
    private long _length = -1;
    private DateTime _lastWrite;
    private byte[]? _buffer;
    internal string? Read(string path)
    {
        lock (_gate)
        {
            var full = Path.GetFullPath(path);
            var file = new FileInfo(full);
            if (!file.Exists) { _path = null; _text = null; _length = -1; return null; }
            if (string.Equals(full, _path, StringComparison.OrdinalIgnoreCase) && file.Length == _length && file.LastWriteTimeUtc == _lastWrite)
                return _text;
            const int maximumBytes = 256 * 1024;
            _buffer ??= new byte[maximumBytes];
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(0, stream.Length - maximumBytes);
            stream.Position = offset;
            var read = 0;
            while (read < _buffer.Length)
            {
                var count = stream.Read(_buffer, read, _buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            var text = Encoding.UTF8.GetString(_buffer, 0, read);
            if (offset > 0)
            {
                var newline = text.IndexOf('\n');
                text = newline < 0 ? "" : text[(newline + 1)..];
            }
            // Cache only the metadata from before the read. A concurrent append is reread on the next observation.
            _path = full; _length = file.Length; _lastWrite = file.LastWriteTimeUtc; _text = text;
            return text;
        }
    }
}

internal static class SteamLocalUserReader
{
    private const ulong IndividualSteamIdBase = 76561197960265728;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Binding> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTimeOffset> Failed = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Binding(nint Module, CreateInterface? Factory);

    // Exact interface versions and method order are from Valve's public Steamworks headers:
    // https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/steam/isteamclient.h
    // https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/steam/isteamuser.h
    // No SteamAPI_Init, AppID, local-user creation, login request, callback dispatch or network API is used.
    internal static SteamLocalUserEvidence? Read(string steamPath)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Path.IsPathFullyQualified(steamPath)) return null;
        lock (Gate) // SteamClient pipe/user methods are documented as not thread-safe.
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(steamPath));
            if (!Libraries.TryGetValue(root, out var binding))
            {
                if (Failed.TryGetValue(root, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow) return null;
                if (Libraries.Count >= 4) return null;
                foreach (var name in new[] { "steam.exe", "steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll" })
                    if (!SteamLibraryTrust.IsTrustedValveFile(Path.Combine(root, name)))
                    { Failed[root] = DateTimeOffset.UtcNow.AddMinutes(1); return null; }
                // Full path + only the DLL's directory/default safe directories; never the working directory.
                var module = LoadLibraryExW(Path.Combine(root, "steamclient64.dll"), 0, 0x100 | 0x1000);
                if (module == 0) { Failed[root] = DateTimeOffset.UtcNow.AddMinutes(1); return null; }
                var exported = GetProcAddress(module, "CreateInterface");
                binding = new(module, exported == 0 ? null : Marshal.GetDelegateForFunctionPointer<CreateInterface>(exported));
                // Keep verified modules loaded until process exit; no IPC user or pipe is retained.
                Libraries[root] = binding;
            }
            var factory = binding.Factory;
            if (factory is null) return null;
            var client = factory("SteamClient020", 0);
            if (client == 0) return null;
            var create = Method<CreatePipe>(client, 0);
            var releasePipe = Method<ReleasePipe>(client, 1);
            var connect = Method<ConnectUser>(client, 2);
            var releaseUser = Method<ReleaseUser>(client, 4);
            var getUser = Method<GetUser>(client, 5);
            var pipe = create(client);
            if (pipe == 0) return null;
            var user = 0;
            try
            {
                user = connect(client, pipe); // Existing global user only; zero means no logged-in local user.
                if (user == 0) return null;
                var instance = getUser(client, user, pipe, "SteamUser023");
                if (instance == 0) return null;
                var getId = Method<GetSteamId>(instance, 2);
                var connected = Method<LoggedOn>(instance, 1);
                ulong before = 0, after = 0;
                _ = getId(instance, ref before);
                var loggedOn = connected(instance);
                _ = getId(instance, ref after);
                if (before != after) return new(0, false, false);
                if (before <= IndividualSteamIdBase || before > IndividualSteamIdBase + uint.MaxValue) return null;
                return new((uint)(before - IndividualSteamIdBase), loggedOn);
            }
            finally
            {
                if (user != 0) releaseUser(client, pipe, user);
                _ = releasePipe(client, pipe);
            }
        }
    }
    private static T Method<T>(nint instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateInterface([MarshalAs(UnmanagedType.LPStr)] string version, nint returnCode);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int CreatePipe(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool ReleasePipe(nint self, int pipe);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate int ConnectUser(nint self, int pipe);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void ReleaseUser(nint self, int pipe, int user);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate nint GetUser(nint self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
    // MSVC x64 returns CSteamID by an 8-byte hidden result buffer after the this pointer (verified by read-only probe).
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate nint GetSteamId(nint self, ref ulong result);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool LoggedOn(nint self);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern nint GetProcAddress(nint module, string name);
}

internal static class SteamLibraryTrust
{
    internal static bool IsTrustedValveFile(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var file = new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = pointer, StateAction = 1, ProviderFlags = 0x1000 | 0x10 };
        try
        {
            // Verify file integrity/trust using only cached certificates; verification cannot retrieve from the network.
            if (WinVerifyTrust(-1, ref action, ref data) != 0) return false;
            var provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == 0) return false;
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == 0) return false;
            var signerInfo = Marshal.PtrToStructure<TrustSignerPrefix>(signer);
            if (signerInfo.CertificateCount == 0 || signerInfo.Certificates == 0) return false;
            var certificate = Marshal.ReadIntPtr(signerInfo.Certificates, IntPtr.Size); // CRYPT_PROVIDER_CERT.pCert
            if (certificate == 0) return false;
            var subject = new StringBuilder(256);
            _ = CertGetNameStringW(certificate, 4, 0, 0, subject, (uint)subject.Capacity);
            return subject.ToString() is "Valve Corp." or "Valve Corporation";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Security.SecurityException) { return false; }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(-1, ref action, ref data);
            Marshal.DestroyStructure<TrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFileInfo
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File; public uint StateAction; public nint StateData, UrlReference; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustSignerPrefix
    { public uint Size, VerifyTimeLow, VerifyTimeHigh, CertificateCount; public nint Certificates; }
    [DllImport("wintrust.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);
    [DllImport("wintrust.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("crypt32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CertGetNameStringW(nint certificate, uint nameType, uint flags, nint typeParameter, StringBuilder name, uint size);
}