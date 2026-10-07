using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gameorder;

public interface IAppLockStorage
{
    byte[]? Read();
    void Write(byte[] protectedConfiguration);
    void Delete() => throw new NotSupportedException("此存储未实现安全配置删除。");
}
public interface IAppLockProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>Password-gated UI and password-wrapped random data key. Steam credentials are outside this store.</summary>
public sealed class AppLockService
{
    public const int MinimumPasswordLength=8;
    public const int MaximumPasswordHintLength=80;
    public const int PasswordIterations=600_000;
    public const string RecoveryInstructions="安全配置无法读取，密码保护资料不会自动解开。忘记密码只能清空 Gameorder 本地资料后重置；Steam 游戏与存档不受影响。";
    private static readonly byte[] Magic="GOL1"u8.ToArray();
    private static readonly byte[] VerifierPurpose="Gameorder.password.verifier.v2"u8.ToArray();
    private readonly IAppLockStorage _storage;
    private readonly IAppLockProtector _protector;
    private readonly object _gate=new();
    private Configuration? _configuration;
    private LocalStore? _store;
    private byte[]? _unlockedKey;
    public bool Enabled {get;private set;}
    public bool ConfigurationError {get;private set;}
    public bool IsAuthenticated {get {lock(_gate) return _unlockedKey is not null&&!ConfigurationError;}}
    // A hint is deliberately available before password authentication. It must
    // contain a personal clue rather than the password or private account data.
    public string PasswordHint {get {lock(_gate) return Enabled&&!ConfigurationError?_configuration?.PasswordHint??"":"";}}
    public string Status=>ConfigurationError?RecoveryInstructions:Enabled?"已启用 · 账号资料由应用密码参与加密":"未启用 · 当前 Windows 用户加密保护";
    public static string DefaultConfigurationPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Gameorder","security.golock");
    public AppLockService(IAppLockStorage? storage=null,IAppLockProtector? protector=null)
    {_storage=storage??new AppLockFileStorage(DefaultConfigurationPath);_protector=protector??new AppLockWindowsProtector();Load();}
    public static AppLockService CreateVolatile()=>new(new VolatileAppLockStorage());
    public void AttachStore(LocalStore store)
    {
        lock(_gate)
        {
            if(_store is not null&&!Path.GetFullPath(_store.StoragePath).Equals(Path.GetFullPath(store.StoragePath),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("一个安全配置不能绑定不同账号库。");
            _store=store;
            try {if(store.HasPasswordProtectedFile&&!Enabled) {Enabled=true;ConfigurationError=true;}}
            catch(Exception ex) when(LocalStore.IsStorageFailure(ex)) {Enabled=true;ConfigurationError=true;}
        }
    }
    internal T AccessStore<T>(Func<bool,byte[]?,Guid,T> action)
    {lock(_gate) return action(Enabled||ConfigurationError,_unlockedKey,KeyId(_configuration));}
    public void Lock() {lock(_gate) {Clear(_unlockedKey);_unlockedKey=null;}}
    private void Load()
    {
        byte[]? plain=null;
        try
        {
            var data=_storage.Read();if(data is null) return;
            if(data.Length<5||data.Length>64*1024||!data.AsSpan(0,4).SequenceEqual(Magic)) throw new InvalidDataException();
            plain=_protector.Unprotect(data[4..]);var c=JsonSerializer.Deserialize<Configuration>(plain)??throw new InvalidDataException();
            c.PasswordHint??="";
            if(c.Version is not (1 or 2)||c.Iterations<PasswordIterations||c.Iterations>2_000_000||c.Salt is null||c.Verifier is null||c.KeyNonce is null||c.KeyTag is null||c.WrappedKey is null
                ||(c.Enabled&&(c.Salt.Length!=32||c.Verifier.Length!=32))||(!c.Enabled&&(c.Salt.Length!=0||c.Verifier.Length!=0))
                ||(c.Version==2&&c.Enabled&&(KeyId(c)==Guid.Empty||c.KeyNonce.Length!=12||c.KeyTag.Length!=16||c.WrappedKey.Length!=32))
                ||!ValidPasswordHint(c.PasswordHint,out var loadedHint,out _)) throw new InvalidDataException();
            c.PasswordHint=loadedHint;
            _configuration=c;Enabled=c.Enabled;
        }
        catch(Exception ex) when(LocalStore.IsStorageFailure(ex)||ex is System.Security.SecurityException)
        {Enabled=true;ConfigurationError=true;_configuration=null;}
        finally {Clear(plain);}
    }
    public bool Verify(string password,out string error)
    {
        lock(_gate)
        {
            if(ConfigurationError) {error=RecoveryInstructions;return false;}
            if(!Enabled||_configuration is not {Enabled:true} c) {error="应用锁未启用。";return false;}
            if(password.Length>4096) {error="密码不正确。";return false;}
            byte[]? derived=null,verifier=null,key=null;
            try
            {
                derived=Rfc2898DeriveBytes.Pbkdf2(password,c.Salt,c.Iterations,HashAlgorithmName.SHA256,32);
                verifier=c.Version==1?derived.ToArray():HMACSHA256.HashData(derived,VerifierPurpose);
                if(!CryptographicOperations.FixedTimeEquals(verifier,c.Verifier)) {error="密码不正确。";return false;}
                if(c.Version==1)
                {
                    key=RandomNumberGenerator.GetBytes(32);var next=PasswordConfiguration(password,key,Guid.NewGuid(),c.PasswordHint);
                    if(!CommitTransition(next,null,key,KeyId(next),out error)) return false;
                }
                else
                {
                    key=new byte[32];using var aes=new AesGcm(derived,16);aes.Decrypt(c.KeyNonce,c.WrappedKey,c.KeyTag,key,WrapPurpose(KeyId(c)));
                    _store?.ValidateAuthenticatedProtection(key,KeyId(c));
                    Clear(_unlockedKey);_unlockedKey=key.ToArray();
                }
                error="";return true;
            }
            catch(Exception ex) when(LocalStore.IsStorageFailure(ex)) {Lock();error="密码保护资料无法验证或迁移，原文件已保留。";return false;}
            finally {Clear(derived);Clear(verifier);Clear(key);}
        }
    }
    public bool Enable(string password,out string error,string? passwordHint=null)
    {
        lock(_gate)
        {
            if(Enabled||ConfigurationError) {error=ConfigurationError?RecoveryInstructions:"应用锁已启用，请验证当前密码后更改。";return false;}
            if(!ValidPassword(password,out error)) return false;
            if(!ValidPasswordHint(passwordHint,out var hint,out error,password)) return false;
            var key=RandomNumberGenerator.GetBytes(32);
            try {var next=PasswordConfiguration(password,key,Guid.NewGuid(),hint);return CommitTransition(next,null,key,KeyId(next),out error);}
            finally {Clear(key);}
        }
    }
    public bool ChangePassword(string currentPassword,string newPassword,out string error,string? passwordHint=null)
    {
        lock(_gate)
        {
            if(!ValidPasswordHint(passwordHint??PasswordHint,out var hint,out error,currentPassword,newPassword)) return false;
            if(!Verify(currentPassword,out error)||!ValidPassword(newPassword,out error)) return false;
            var next=PasswordConfiguration(newPassword,_unlockedKey!,KeyId(_configuration),hint);
            try {WriteConfiguration(next);Publish(next);error="";return true;}
            catch(Exception ex) when(LocalStore.IsStorageFailure(ex)) {ClearConfiguration(next);error="新密码未保存，原密码和账号资料仍有效。";return false;}
        }
    }
    public bool ChangePasswordHint(string currentPassword,string? passwordHint,out string error)
    {
        lock(_gate)
        {
            if(ConfigurationError) {error=RecoveryInstructions;return false;}
            if(!Enabled||_configuration is not {Enabled:true} current) {error="应用锁未启用。";return false;}
            if(!ValidPasswordHint(passwordHint,out var hint,out error,currentPassword)) return false;
            // Hint editing must not migrate or rewrite account ciphertext,
            // including legacy v1 configurations awaiting normal unlock.
            if(!VerifyPasswordOnly(currentPassword,current,out error)) return false;
            var next=Clone(current);next.PasswordHint=hint;
            try {WriteConfiguration(next);Publish(next);error="";return true;}
            catch(Exception ex) when(LocalStore.IsStorageFailure(ex))
            {
                ClearConfiguration(next);
                byte[]? persisted=null;
                try {persisted=_storage.Read();if(persisted is null){ConfigurationError=true;Lock();}}
                catch(Exception readFailure) when(LocalStore.IsStorageFailure(readFailure)) {ConfigurationError=true;Lock();}
                finally {Clear(persisted);}
                error=ConfigurationError?"密码提示未保存，安全配置需要恢复；加密资料及恢复材料已保留。":"密码提示未保存，原提示和密码保持不变。";
                return false;
            }
        }
    }
    public bool Disable(string currentPassword,out string error)
    {
        lock(_gate)
        {
            if(!Verify(currentPassword,out error)) return false;
            // Keep the password-wrapped key until the DPAPI metadata has committed durably.
            var pending=Clone(_configuration!);pending.PendingDisable=true;
            var disabled=new Configuration {Version=2,Enabled=false};
            return CommitTransition(pending,disabled,null,Guid.Empty,out error);
        }
    }
    private bool CommitTransition(Configuration beforeMetadata,Configuration? afterMetadata,byte[]? targetKey,Guid targetId,out string error)
    {
        byte[]? original=null;
        try
        {
            original=_storage.Read();
            Action commit=()=>WriteConfiguration(beforeMetadata);
            Action? finish=afterMetadata is null?null:()=>WriteConfiguration(afterMetadata);
            Action rollback=()=>{if(original is null) _storage.Delete();else _storage.Write(original);};
            if(_store is null) WriteConfiguration(afterMetadata??beforeMetadata);
            else _store.ChangeProtection(_unlockedKey,KeyId(_configuration),targetKey,targetId,commit,finish,rollback);
            Publish(afterMetadata??beforeMetadata);Clear(_unlockedKey);_unlockedKey=targetKey?.ToArray();error="";return true;
        }
        catch(Exception ex) when(LocalStore.IsStorageFailure(ex)||ex is NotSupportedException)
        {
            ClearConfiguration(beforeMetadata);if(afterMetadata is not null) ClearConfiguration(afterMetadata);
            if(_store?.LoadError is not null) {Enabled=true;ConfigurationError=true;Lock();}
            error=_store?.LoadError??"资料保护方式未更改完成，加密资料及恢复材料已保留；请重试或重新打开。";return false;
        }
        finally {Clear(original);}
    }
    private void WriteConfiguration(Configuration config)
    {
        byte[]? plain=null,protectedBytes=null;
        try {plain=JsonSerializer.SerializeToUtf8Bytes(config);protectedBytes=_protector.Protect(plain);_storage.Write(Magic.Concat(protectedBytes).ToArray());}
        finally {Clear(plain);Clear(protectedBytes);}
    }
    private void Publish(Configuration next)
    {var previous=_configuration;_configuration=next;Enabled=next.Enabled;ConfigurationError=false;if(previous is not null&&!ReferenceEquals(previous,next)) ClearConfiguration(previous);}
    internal void ResetAfterDestruction()
    {lock(_gate) {Lock();if(_configuration is not null) ClearConfiguration(_configuration);_configuration=null;Enabled=false;ConfigurationError=false;_store?.ResetAfterDestruction();}}
    private static bool ValidPassword(string password,out string error)
    {error=password.Length<MinimumPasswordLength?"密码至少需要 8 个字符。":password.Length>4096?"密码过长。":"";return error.Length==0;}
    private static bool ValidPasswordHint(string? value,out string hint,out string error,params string[] passwords)
    {
        var normalized=(value??"").Trim();hint=normalized;
        error=(value??"").Any(ch=>char.IsControl(ch)||ch is '\u2028' or '\u2029')?"密码提示只能是一行文字，不能含换行或控制字符。":
            hint.Length>MaximumPasswordHintLength?"密码提示最多 80 个字符。":
            passwords.Any(password=>!string.IsNullOrEmpty(password)&&normalized.Contains(password,StringComparison.Ordinal))?"密码提示不能包含完整密码，请使用只有你能理解的线索。":"";
        return error.Length==0;
    }
    private static bool VerifyPasswordOnly(string password,Configuration config,out string error)
    {
        if(password.Length>4096) {error="密码不正确。";return false;}
        byte[]? derived=null,verifier=null;
        try
        {
            derived=Rfc2898DeriveBytes.Pbkdf2(password,config.Salt,config.Iterations,HashAlgorithmName.SHA256,32);
            verifier=config.Version==1?derived.ToArray():HMACSHA256.HashData(derived,VerifierPurpose);
            bool valid=CryptographicOperations.FixedTimeEquals(verifier,config.Verifier);error=valid?"":"密码不正确。";return valid;
        }
        finally {Clear(derived);Clear(verifier);}
    }
    private static Configuration PasswordConfiguration(string password,byte[] key,Guid id,string passwordHint="")
    {
        var c=new Configuration {Version=2,Enabled=true,Salt=RandomNumberGenerator.GetBytes(32),KeyId=id.ToString("D"),KeyNonce=RandomNumberGenerator.GetBytes(12),KeyTag=new byte[16],WrappedKey=new byte[32],PasswordHint=passwordHint};
        var derived=Rfc2898DeriveBytes.Pbkdf2(password,c.Salt,PasswordIterations,HashAlgorithmName.SHA256,32);
        try {c.Verifier=HMACSHA256.HashData(derived,VerifierPurpose);using var aes=new AesGcm(derived,16);aes.Encrypt(c.KeyNonce,key,c.WrappedKey,c.KeyTag,WrapPurpose(id));return c;}
        finally {Clear(derived);}
    }
    private static byte[] WrapPurpose(Guid id)=>Encoding.UTF8.GetBytes("Gameorder.data-key.wrap.v2:"+id.ToString("D"));
    private static Guid KeyId(Configuration? c)=>Guid.TryParse(c?.KeyId,out var id)?id:Guid.Empty;
    private static Configuration Clone(Configuration c)=>new() {Version=c.Version,Enabled=c.Enabled,Iterations=c.Iterations,Salt=c.Salt.ToArray(),Verifier=c.Verifier.ToArray(),KeyId=c.KeyId,KeyNonce=c.KeyNonce.ToArray(),KeyTag=c.KeyTag.ToArray(),WrappedKey=c.WrappedKey.ToArray(),PendingDisable=c.PendingDisable,PasswordHint=c.PasswordHint};
    private static void Clear(byte[]? data) {if(data is not null) CryptographicOperations.ZeroMemory(data);}
    private static void ClearConfiguration(Configuration c) {Clear(c.Salt);Clear(c.Verifier);Clear(c.KeyNonce);Clear(c.KeyTag);Clear(c.WrappedKey);}
    private sealed class Configuration
    {
        public int Version {get;set;}=1;
        public bool Enabled {get;set;}
        public int Iterations {get;set;}=PasswordIterations;
        public byte[] Salt {get;set;}=[];
        public byte[] Verifier {get;set;}=[];
        public string KeyId {get;set;}="";
        public byte[] KeyNonce {get;set;}=[];
        public byte[] KeyTag {get;set;}=[];
        public byte[] WrappedKey {get;set;}=[];
        public bool PendingDisable {get;set;}
        public string PasswordHint {get;set;}="";
    }
}
public sealed class VolatileAppLockStorage : IAppLockStorage
{
    private byte[]? _payload;
    public byte[]? Read()=>_payload?.ToArray();
    public void Write(byte[] protectedConfiguration)=>_payload=protectedConfiguration.ToArray();
    public void Delete()=>_payload=null;
}
public sealed class AppLockFileStorage(string path) : IAppLockStorage
{
    internal Action<string,string>? ReplaceForVerification {get;init;}
    internal Action<int>? WaitForVerification {get;init;}
    public void Delete()=>File.Delete(path);
    public byte[]? Read()
    {
        try
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length<5||stream.Length>64*1024) throw new InvalidDataException("应用锁配置大小无效。");
            var payload=new byte[(int)stream.Length]; stream.ReadExactly(payload); return payload;
        }
        catch(FileNotFoundException) {EncryptedLocalFile.RejectInterruptedWrite(path);return null;}
        catch(DirectoryNotFoundException) { return null; }
    }
    public void Write(byte[] protectedConfiguration)
    {
        string target=Path.GetFullPath(path),directory=Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary=Path.Combine(directory,Path.GetFileName(target)+"."+Guid.NewGuid().ToString("N")+".tmp");
        // A trusted rollback may recreate a missing target from its captured
        // encrypted original even when a failed replacement temp still exists.
        var original=File.Exists(target)?Read():null;var expectedHash=original is null?null:SHA256.HashData(original);
        string? recovery=null;
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            { stream.Write(protectedConfiguration); stream.Flush(true); }
            EncryptedLocalFile.VerifyUnchanged(target,expectedHash);
            if(expectedHash is not null) EncryptedLocalFile.ReplaceWithRetry(temporary,target,()=>EncryptedLocalFile.VerifyUnchanged(target,expectedHash),ReplaceForVerification,WaitForVerification);
            else File.Move(temporary,target);
        }
        catch(IOException failure) when(original is not null&&!File.Exists(target))
        {
            // ReplaceFile's rare partial failures must not discard the sole old
            // password-wrapped key (in particular when disabling protection).
            recovery=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(recovery,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {stream.Write(original);stream.Flush(true);}
                File.Move(recovery,target);
            }
            catch(IOException restoreFailure) {throw new IOException("安全配置替换及恢复未完成，原配置密文暂存已保留；不要删除资料目录。",new AggregateException(failure,restoreFailure));}
            throw;
        }
        finally
        {
            if(original is not null)CryptographicOperations.ZeroMemory(original);if(expectedHash is not null)CryptographicOperations.ZeroMemory(expectedHash);
            if(expectedHash is null||File.Exists(target)) {if(File.Exists(temporary))File.Delete(temporary);if(recovery is not null&&File.Exists(recovery))File.Delete(recovery);}
        }
    }
}
public sealed class AppLockWindowsProtector : IAppLockProtector
{
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Gameorder.app-lock.configuration.v1");
    public byte[] Protect(byte[] plaintext)=>Transform(plaintext,true);
    public byte[] Unprotect(byte[] protectedBytes)=>Transform(protectedBytes,false);
    private static byte[] Transform(byte[] input,bool protect)
    {
        Blob source=Allocate(input),entropy=Allocate(Entropy),result=default;
        try
        {
            bool success=protect?CryptProtectData(ref source,"Gameorder app lock",ref entropy,IntPtr.Zero,IntPtr.Zero,1,out result)
                :CryptUnprotectData(ref source,IntPtr.Zero,ref entropy,IntPtr.Zero,IntPtr.Zero,1,out result);
            if(!success) throw new CryptographicException("Windows 无法保护或读取应用锁配置。",new Win32Exception(Marshal.GetLastWin32Error()));
            if(result.Length<=0||result.Length>64*1024) throw new CryptographicException("应用锁配置大小无效。");
            var data=new byte[result.Length]; Marshal.Copy(result.Data,data,0,data.Length); return data;
        }
        finally
        {
            ClearAndFree(source); ClearAndFree(entropy);
            if(result.Data!=IntPtr.Zero) { Zero(result); LocalFree(result.Data); }
        }
    }
    private static Blob Allocate(byte[] bytes)
    { var pointer=Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes,0,pointer,bytes.Length); return new() {Length=bytes.Length,Data=pointer}; }
    private static void Zero(Blob blob) { for(int i=0;i<blob.Length;i++) Marshal.WriteByte(blob.Data,i,0); }
    private static void ClearAndFree(Blob blob) { if(blob.Data!=IntPtr.Zero) { Zero(blob); Marshal.FreeHGlobal(blob.Data); } }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input,string description,ref Blob entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,ref Blob entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

public enum AppLockSettingsAction { Enable,ChangePassword,Disable,ChangeHint }
public sealed class AppLockSettingsRequestedEventArgs(AppLockSettingsAction action) : EventArgs
{
    public AppLockSettingsAction Action {get;}=action;
}
