using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Gameorder;

public sealed record SwitchResult(string Message);
public sealed record SteamRegistryState(string? AutoLoginUser, int? RememberPassword);

/// <summary>Injected by the isolated tests so no test accesses the user's Steam process or registry.</summary>
public interface ISteamPlatform
{
    string? GetSteamPath();
    bool IsSteamRunning { get; }
    SteamRegistryState ReadLoginSettings();
    void WriteLoginSettings(SteamRegistryState state);
    Task RequestShutdownAsync(string steamExe, CancellationToken cancellationToken);
    void Launch(string steamExe);
}

public sealed class SteamService
{
    private readonly ISteamPlatform _platform;
    private readonly string _backupRoot;
    private readonly TimeSpan _shutdownTimeout;
    private readonly TimeSpan _shutdownPollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Regex AccountNamePattern = new("^[A-Za-z0-9_]{2,64}$", RegexOptions.CultureInvariant);
    private const int MaximumConfigBytes = 16 * 1024 * 1024;

    public SteamService(ISteamPlatform? platform = null, string? backupRoot = null, TimeSpan? shutdownTimeout = null, TimeSpan? shutdownPollInterval = null)
    {
        _platform = platform ?? new WindowsSteamPlatform();
        _backupRoot = backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gameorder", "backups");
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(30);
        _shutdownPollInterval = shutdownPollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    public bool IsSteamRunning => _platform.IsSteamRunning;
    public string DetectSteamPath()
    {
        var candidates = new[] { _platform.GetSteamPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"), @"C:\Program Files (x86)\Steam" };
        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).FirstOrDefault(p => File.Exists(Path.Combine(p!, "steam.exe"))) ?? "";
    }

    public IReadOnlyList<SteamAccount> ReadAccounts(string steamPath)
    {
        var install = ValidateSteamPath(steamPath);
        var file = VdfSnapshot.Read(LoginFile(install));
        if (!file.Exists) return [];
        var users = ReadUsers(file.Text!);
        var registry = _platform.ReadLoginSettings();
        var publicUsers = users.Children.Where(e => e.IsObject && IsSteamId(e.Key)).ToList();
        var selected = publicUsers.FirstOrDefault(e => !string.IsNullOrWhiteSpace(registry.AutoLoginUser) && string.Equals(registry.AutoLoginUser, e.GetValue("AccountName"), StringComparison.OrdinalIgnoreCase))
            ?? publicUsers.FirstOrDefault(e => e.GetValue("AutoLogin") == "1")
            ?? publicUsers.FirstOrDefault(e => e.GetValue("AutoLogin") is null && e.GetValue("MostRecent") == "1");
        return publicUsers.Select(e => new SteamAccount
        {
            SteamId = e.Key,
            AccountName = e.GetValue("AccountName") ?? "",
            DisplayName = e.GetValue("PersonaName") ?? e.GetValue("AccountName") ?? "Steam 用户",
            HasCachedLogin = IsCached(e),
            LastLoginAt = ParseTimestamp(e.GetValue("Timestamp")),
            IsCurrentSelection = ReferenceEquals(e, selected)
        }).Where(a => !string.IsNullOrWhiteSpace(a.AccountName)).OrderByDescending(a => a.IsCurrentSelection).ThenByDescending(a => a.LastLoginAt).ToList();
    }

    public async Task<SwitchResult> SwitchAsync(string steamPath, SteamAccount account, bool offline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!AccountNamePattern.IsMatch(account.AccountName)) throw new ArgumentException("Steam 登录名须为 2–64 位英文字母、数字或下划线。");
        var install = ValidateSteamPath(steamPath);
        if (offline && string.IsNullOrWhiteSpace(account.SteamId)) throw new InvalidOperationException("手动添加的账号需先在 Steam 完成一次在线登录，才能使用本机离线登录。");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var operationLock = AcquireInstallLock(install);
            // Validate before asking Steam to exit, then re-read after exit to use its final saved state.
            ValidateTarget(VdfSnapshot.Read(LoginFile(install)), account, offline);
            var exe = Path.Combine(install, "steam.exe");
            if (_platform.IsSteamRunning)
            {
                await _platform.RequestShutdownAsync(exe, cancellationToken);
                var timer = Stopwatch.StartNew();
                while (_platform.IsSteamRunning)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timer.Elapsed >= _shutdownTimeout) throw new InvalidOperationException("Steam 未在 30 秒内正常退出。请结束游戏并从 Steam 菜单退出后重试；账号配置尚未修改。");
                    await Task.Delay(_shutdownPollInterval, cancellationToken);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_platform.IsSteamRunning) throw new InvalidOperationException("Steam 仍在运行，无法安全更改账号配置。");
            var path = LoginFile(install);
            var before = VdfSnapshot.Read(path);
            var (document, users, target) = ValidateTarget(before, account, offline);
            byte[]? newBytes = null;
            var fields = new List<OwnedField>();
            if (document is not null && users is not null)
            {
                var changes = new List<KeyValuesChange>();
                foreach (var user in users.Children.Where(e => e.IsObject && IsSteamId(e.Key)))
                {
                    var selected = ReferenceEquals(user, target);
                    changes.Add(new(user, "AutoLogin", selected ? "1" : "0"));
                    changes.Add(new(user, "MostRecent", selected ? "1" : "0"));
                    if (!selected) continue;
                    changes.Add(new(user, "WantsOfflineMode", offline ? "1" : "0"));
                    changes.Add(new(user, "SkipOfflineModeWarning", offline ? "1" : "0"));
                }
                fields = changes.Select(change => new OwnedField { SteamId = change.Entry.Key, Key = change.Key, BeforeValue = change.Entry.GetValue(change.Key), AfterValue = change.Value! }).ToList();
                newBytes = before.Encode(document.WithValues(changes));
            }
            var registryBefore = _platform.ReadLoginSettings();
            var registryAfter = new SteamRegistryState(account.AccountName, target is not null && IsCached(target) ? 1 : registryBefore.RememberPassword);
            var backup = new BackupRecord
            {
                SteamPath = install,
                CreatedAt = DateTimeOffset.UtcNow,
                OriginalFileExists = before.Exists,
                OriginalFile = before.Bytes,
                OriginalRegistry = registryBefore,
                ChangedFileExists = newBytes is not null || before.Exists,
                ChangedHash = Hash(newBytes ?? before.Bytes),
                ChangedRegistry = registryAfter,
                OwnedFields = fields
            };
            var backupFile = SaveBackup(install, backup);
            var fileCommitted = false;
            var registryCommitted = false;
            try
            {
                if (_platform.IsSteamRunning) throw new InvalidOperationException("Steam 已重新运行，账号配置尚未修改。");
                if (newBytes is not null)
                {
                    ReplaceChecked(path, before.Exists, Hash(before.Bytes), newBytes);
                    fileCommitted = true;
                }
                else AssertFileMatches(path, before.Exists, Hash(before.Bytes));
                if (_platform.ReadLoginSettings() != registryBefore) throw new IOException("Steam 登录设置已被其他程序更改，请刷新后重试。");
                // Mark before the operation so a partially failed registry write is also restored.
                registryCommitted = true;
                _platform.WriteLoginSettings(registryAfter);
                cancellationToken.ThrowIfCancellationRequested();
                AssertFileMatches(path, backup.ChangedFileExists, backup.ChangedHash);
                if (_platform.IsSteamRunning) throw new InvalidOperationException("Steam 在切换期间重新运行，已中止启动。");
                _platform.Launch(exe);
            }
            catch (Exception error)
            {
                try
                {
                    if (_platform.IsSteamRunning) throw new IOException("Steam 已重新运行。请先退出 Steam，再从加密备份恢复登录设置");
                    if (registryCommitted && !IsOwnedRegistryState(_platform.ReadLoginSettings(), registryBefore, registryAfter)) throw new IOException("Steam 登录设置被其他程序再次更改，未覆盖最新设置；加密备份已保留");
                    if (fileCommitted) RestoreFileChecked(path, backup.ChangedFileExists, backup.ChangedHash, backup.OriginalFileExists, backup.OriginalFile);
                    if (registryCommitted) _platform.WriteLoginSettings(registryBefore);
                    File.Delete(backupFile);
                }
                catch (Exception restoreError)
                {
                    throw new IOException($"切换未完成：{error.Message} 自动恢复也未完成：{restoreError.Message}。加密备份已保留，请退出 Steam 后恢复。", new AggregateException(error, restoreError));
                }
                throw new InvalidOperationException($"切换未完成，原登录设置已恢复：{error.Message}", error);
            }
            PruneBackups(install);
            return new SwitchResult(offline ? "已选择离线模式并启动 Steam。能否离线进入取决于本机已记住的会话及游戏授权。" : "已选择账号并启动 Steam。Steam 可能要求密码或 Steam Guard 验证。");
        }
        finally { _gate.Release(); }
    }

    public bool CanRestoreBackup(string steamPath)
    {
        try { return FindLatestBackup(ValidateSteamPath(steamPath)) is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void RestoreLatestBackup(string steamPath)
    {
        var install = ValidateSteamPath(steamPath);
        _gate.Wait();
        try
        {
            using var operationLock = AcquireInstallLock(install);
            if (_platform.IsSteamRunning) throw new InvalidOperationException("请先正常退出 Steam，再恢复登录设置。");
            var latest = FindLatestBackup(install) ?? throw new InvalidOperationException("此 Steam 安装没有可恢复的本地备份。");
            var backup = LoadBackup(latest);
            if (!string.Equals(backup.SteamPath, install, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份与当前 Steam 安装不匹配。");
            var path = LoginFile(install);
            var currentRegistry = _platform.ReadLoginSettings();
            if (currentRegistry != backup.ChangedRegistry) throw new IOException("Steam 登录设置在备份后已发生改变。为保留最新设置，恢复已中止。");
            var current = VdfSnapshot.Read(path);
            var exactFile = current.Exists == backup.ChangedFileExists && Hash(current.Bytes) == backup.ChangedHash;
            byte[]? restoredBytes = backup.OriginalFile;
            var restoredExists = backup.OriginalFileExists;
            if (!exactFile)
            {
                // Steam updates timestamps and other metadata after launch. Restore only fields
                // owned by this operation, keeping every unrelated current value intact.
                restoredExists = current.Exists;
                restoredBytes = current.Bytes;
                if (backup.OwnedFields.Count > 0)
                {
                    if (!current.Exists) throw new IOException("Steam 登录配置已被移除，恢复已中止。");
                    var document = ValveKeyValues.Parse(current.Text!);
                    var users = RequireUsers(document);
                    var edits = new List<KeyValuesChange>();
                    foreach (var field in backup.OwnedFields)
                    {
                        var user = users.Children.FirstOrDefault(e => e.IsObject && e.Key == field.SteamId) ?? throw new IOException("备份中的 Steam 账号记录已被移除，恢复已中止。");
                        var values = user.Children.Where(e => e.Key.Equals(field.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (values.Count == 0 || values.Any(v => v.IsObject || v.Value != field.AfterValue)) throw new IOException($"Steam 的 {field.Key} 设置在切换后已改变。为保留最新选择，恢复已中止。");
                        edits.Add(new(user, field.Key, field.BeforeValue));
                    }
                    restoredBytes = current.Encode(document.WithValues(edits));
                }
            }
            var fileRestored = false;
            try
            {
                if (_platform.IsSteamRunning) throw new InvalidOperationException("Steam 已重新运行，请先退出后恢复。");
                if (_platform.ReadLoginSettings() != currentRegistry) throw new IOException("Steam 登录设置已被其他程序更改，恢复已中止。");
                RestoreFileChecked(path, current.Exists, Hash(current.Bytes), restoredExists, restoredBytes);
                fileRestored = true;
                _platform.WriteLoginSettings(backup.OriginalRegistry);
                File.Delete(latest);
            }
            catch
            {
                if (_platform.IsSteamRunning) throw new IOException("Steam 已重新运行，恢复未完成；加密备份已保留，请退出 Steam 后重试。");
                if (fileRestored && !IsOwnedRegistryState(_platform.ReadLoginSettings(), currentRegistry, backup.OriginalRegistry)) throw new IOException("Steam 登录设置被其他程序再次更改，未覆盖最新设置；加密备份已保留。");
                if (fileRestored) RestoreFileChecked(path, restoredExists, Hash(restoredBytes), current.Exists, current.Bytes);
                if (fileRestored) _platform.WriteLoginSettings(currentRegistry);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static (ValveKeyValues? Document, KeyValuesEntry? Users, KeyValuesEntry? Target) ValidateTarget(VdfSnapshot file, SteamAccount account, bool offline)
    {
        if (!file.Exists)
        {
            if (!string.IsNullOrWhiteSpace(account.SteamId)) throw new InvalidOperationException("找不到本机 Steam 登录记录，请先在 Steam 在线登录该账号。");
            return (null, null, null);
        }
        var document = ValveKeyValues.Parse(file.Text!);
        var users = RequireUsers(document);
        var target = string.IsNullOrWhiteSpace(account.SteamId) ? null : users.Children.FirstOrDefault(e => e.IsObject && e.Key == account.SteamId);
        if (!string.IsNullOrWhiteSpace(account.SteamId) && (target is null || !string.Equals(target.GetValue("AccountName"), account.AccountName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("该账号的本机记录已改变，请重新导入账号后重试。");
        if (offline && (target is null || !IsCached(target))) throw new InvalidOperationException("该账号未记住本机登录，或禁止自动登录。请先在 Steam 在线登录并勾选记住登录信息。");
        return (document, users, target);
    }
    private static KeyValuesEntry ReadUsers(string text) => RequireUsers(ValveKeyValues.Parse(text));
    private static KeyValuesEntry RequireUsers(ValveKeyValues document) => document.Find("users") is { IsObject: true } users ? users : throw new FormatException("Steam 登录配置缺少 users 对象。");
    private static bool IsCached(KeyValuesEntry user) => user.GetValue("RememberPassword") == "1" && user.GetValue("AllowAutoLogin") != "0";
    private static bool IsSteamId(string id) => id.Length > 0 && id.All(c => c is >= '0' and <= '9');
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return null; }
    }
    private static string ValidateSteamPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请设置有效的 Steam 安装文件夹。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!File.Exists(Path.Combine(full, "steam.exe"))) throw new FileNotFoundException("指定文件夹内找不到 steam.exe。");
        return full;
    }
    private static string LoginFile(string install) => Path.Combine(install, "config", "loginusers.vdf");
    private string BackupFolder(string install) => Path.Combine(_backupRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(install.ToUpperInvariant())))[..24]);
    private FileStream AcquireInstallLock(string install)
    {
        var folder = BackupFolder(install);
        Directory.CreateDirectory(folder);
        try { return new FileStream(Path.Combine(folder, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("另一个 Gameorder 窗口正在操作此 Steam 安装，请稍后重试。", e); }
    }
    private string SaveBackup(string install, BackupRecord backup)
    {
        var folder = BackupFolder(install);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".gobk");
        var encrypted = WindowsDataProtection.Protect(JsonSerializer.SerializeToUtf8Bytes(backup));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(encrypted); stream.Flush(true); }
        return path;
    }
    private string? FindLatestBackup(string install)
    {
        var folder = BackupFolder(install);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.gobk").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault() : null;
    }
    private void PruneBackups(string install)
    {
        // Retention is best effort; a cleanup failure must not turn a completed login switch into an error.
        try { foreach (var old in Directory.EnumerateFiles(BackupFolder(install), "*.gobk").OrderByDescending(p => p, StringComparer.Ordinal).Skip(10)) File.Delete(old); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static BackupRecord LoadBackup(string path) => JsonSerializer.Deserialize<BackupRecord>(WindowsDataProtection.Unprotect(File.ReadAllBytes(path))) ?? throw new IOException("无法读取本地加密备份。");
    private static string Hash(byte[]? bytes) => bytes is null ? "" : Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsOwnedRegistryState(SteamRegistryState value, SteamRegistryState before, SteamRegistryState after) =>
        (value.AutoLoginUser == before.AutoLoginUser || value.AutoLoginUser == after.AutoLoginUser) &&
        (value.RememberPassword == before.RememberPassword || value.RememberPassword == after.RememberPassword);
    private static void AssertFileMatches(string path, bool exists, string hash)
    {
        var current = VdfSnapshot.Read(path);
        if (current.Exists != exists || Hash(current.Bytes) != hash) throw new IOException("Steam 配置已被其他程序更改。为保留最新设置，操作已中止。");
    }
    private static void ReplaceChecked(string path, bool oldExists, string oldHash, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".gameorder-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            AssertFileMatches(path, oldExists, oldHash);
            if (oldExists) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void RestoreFileChecked(string path, bool currentExists, string currentHash, bool restoreExists, byte[]? restoreBytes)
    {
        if (restoreExists) ReplaceChecked(path, currentExists, currentHash, restoreBytes ?? throw new IOException("备份内容缺失。"));
        else { AssertFileMatches(path, currentExists, currentHash); if (currentExists) File.Delete(path); }
    }

    private sealed class BackupRecord
    {
        public string SteamPath { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public bool OriginalFileExists { get; set; }
        public byte[]? OriginalFile { get; set; }
        public SteamRegistryState OriginalRegistry { get; set; } = new(null, null);
        public bool ChangedFileExists { get; set; }
        public string ChangedHash { get; set; } = "";
        public SteamRegistryState ChangedRegistry { get; set; } = new(null, null);
        public List<OwnedField> OwnedFields { get; set; } = [];
    }
    private sealed class OwnedField
    {
        public string SteamId { get; set; } = "";
        public string Key { get; set; } = "";
        public string? BeforeValue { get; set; }
        public string AfterValue { get; set; } = "";
    }
    private sealed class VdfSnapshot
    {
        public bool Exists { get; init; }
        public byte[]? Bytes { get; init; }
        public string? Text { get; init; }
        private Encoding Encoding { get; init; } = new UTF8Encoding(false, true);
        private byte[] Preamble { get; init; } = [];
        public byte[] Encode(string text) => [.. Preamble, .. Encoding.GetBytes(text)];
        public static VdfSnapshot Read(string path)
        {
            if (!File.Exists(path)) return new();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumConfigBytes) throw new IOException("Steam 登录配置过大，无法安全处理。");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            Encoding encoding = new UTF8Encoding(false, true);
            var skip = 0;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) skip = 3;
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
            return new() { Exists = true, Bytes = bytes, Text = encoding.GetString(bytes, skip, bytes.Length - skip), Encoding = encoding, Preamble = bytes[..skip] };
        }
    }
}

public sealed class WindowsSteamPlatform : ISteamPlatform
{
    private const string RegistryPath = @"Software\Valve\Steam";
    public string? GetSteamPath() { using var key = Registry.CurrentUser.OpenSubKey(RegistryPath); return key?.GetValue("SteamPath") as string; }
    public bool IsSteamRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("steam");
            try { return processes.Any(p => !p.HasExited); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public SteamRegistryState ReadLoginSettings()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return new(key?.GetValue("AutoLoginUser") as string, key?.GetValue("RememberPassword") is int value ? value : null);
    }
    public void WriteLoginSettings(SteamRegistryState state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true) ?? throw new IOException("无法打开当前用户 Steam 登录设置。");
        if (state.AutoLoginUser is null) key.DeleteValue("AutoLoginUser", false); else key.SetValue("AutoLoginUser", state.AutoLoginUser, RegistryValueKind.String);
        if (state.RememberPassword is null) key.DeleteValue("RememberPassword", false); else key.SetValue("RememberPassword", state.RememberPassword.Value, RegistryValueKind.DWord);
        key.Flush();
    }
    public Task RequestShutdownAsync(string steamExe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(steamExe) { Arguments = "-shutdown", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(steamExe)! }) ?? throw new IOException("无法请求 Steam 正常退出。");
        return Task.CompletedTask;
    }
    public void Launch(string steamExe)
    {
        using var process = Process.Start(new ProcessStartInfo(steamExe) { Arguments = "-silent", UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steamExe)! }) ?? throw new IOException("无法启动 Steam。");
    }
}

internal static class WindowsDataProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    public static byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public static byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = encrypt ? CryptProtectData(ref input, "FloatOrder Steam backup", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法处理本地加密备份；仅同一 Windows 用户可以恢复。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero) { for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != IntPtr.Zero) { for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
        }
    }
}
