using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gameorder;

// Only encrypted bytes are ever passed to disk writers. DPAPI uses the existing CurrentUser implementation.
internal static class EncryptedLocalFile
{
    internal const int MaximumPlaintextBytes = 8 * 1024 * 1024;
    internal const int MaximumFileBytes = MaximumPlaintextBytes + 64 * 1024;
    private const int HeaderBytes = 16;
    private const uint Version = 1;
    private static ReadOnlySpan<byte> Magic => "GORDERLD"u8;
    private static ReadOnlySpan<byte> Purpose => "Gameorder.LocalState.v1\0"u8;
    private static ReadOnlySpan<byte> PasswordMagic => "GORDERPW"u8;
    private const int PasswordHeaderBytes = 60;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool HasEncryptedMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);
    internal static bool HasPasswordMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PasswordMagic);

    internal static byte[] EncryptWithPassword(byte[] json, byte[] key, Guid keyId)
    {
        if (json.Length is <= 0 or > MaximumPlaintextBytes || key.Length != 32 || keyId == Guid.Empty) throw new InvalidDataException("密码保护资料参数无效");
        var envelope = new byte[PasswordHeaderBytes + json.Length];
        PasswordMagic.CopyTo(envelope); BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8, 4), 1);
        keyId.TryWriteBytes(envelope.AsSpan(12, 16)); RandomNumberGenerator.Fill(envelope.AsSpan(28, 12));
        BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(56, 4), json.Length);
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(envelope.AsSpan(28, 12), json, envelope.AsSpan(60), envelope.AsSpan(40, 16), envelope.AsSpan(0, 28));
        return envelope;
    }

    internal static byte[] DecryptWithPassword(byte[] envelope, byte[] key, Guid keyId)
    {
        if (envelope.Length <= PasswordHeaderBytes || envelope.Length > MaximumFileBytes || !HasPasswordMagic(envelope)
            || BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(8, 4)) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(56, 4)) != envelope.Length - PasswordHeaderBytes
            || envelope.Length - PasswordHeaderBytes > MaximumPlaintextBytes || key.Length != 32
            || new Guid(envelope.AsSpan(12, 16)) != keyId || keyId == Guid.Empty)
            throw new InvalidDataException("密码保护资料格式或密钥标识无效");
        var json = new byte[envelope.Length - PasswordHeaderBytes];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(envelope.AsSpan(28, 12), envelope.AsSpan(60), envelope.AsSpan(40, 16), json, envelope.AsSpan(0, 28));
            return json;
        }
        catch { CryptographicOperations.ZeroMemory(json); throw; }
    }

    internal static byte[] SerializeBounded(LocalState state)
    {
        using var buffer = new LimitedJsonBuffer();
        try { JsonSerializer.Serialize(buffer, state); return buffer.ToArray(); }
        finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }

    internal static byte[]? ReadIfExists(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("本地资料大小无效");
            var data = new byte[(int)stream.Length];
            try
            {
                stream.ReadExactly(data);
                if (stream.ReadByte() != -1) throw new InvalidDataException("本地资料在读取时发生变化");
                return data;
            }
            catch { CryptographicOperations.ZeroMemory(data); throw; }
        }
    }

    internal static byte[] LegacyJsonToUtf8(byte[] bytes)
    {
        if (bytes.Length > MaximumPlaintextBytes) throw new InvalidDataException("本地资料超出大小限制");
        byte[] result;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            result = Encoding.Convert(new UnicodeEncoding(false, false, true), StrictUtf8, bytes, 2, bytes.Length - 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            result = Encoding.Convert(new UnicodeEncoding(true, false, true), StrictUtf8, bytes, 2, bytes.Length - 2);
        else
        {
            var skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            _ = StrictUtf8.GetCharCount(bytes, skip, bytes.Length - skip);
            result = bytes.AsSpan(skip).ToArray();
        }
        if (result.Length <= MaximumPlaintextBytes) return result;
        CryptographicOperations.ZeroMemory(result);
        throw new InvalidDataException("本地资料超出大小限制");
    }

    internal static byte[] Encrypt(byte[] utf8Json)
    {
        if (utf8Json.Length is <= 0 or > MaximumPlaintextBytes) throw new InvalidDataException("本地资料超出大小限制");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地资料保护需要 Windows DPAPI");
        var payload = new byte[Purpose.Length + utf8Json.Length];
        try
        {
            Purpose.CopyTo(payload);
            utf8Json.CopyTo(payload, Purpose.Length);
            var ciphertext = WindowsDataProtection.Protect(payload);
            if (ciphertext.Length > MaximumFileBytes - HeaderBytes) throw new InvalidDataException("加密资料超出大小限制");
            var envelope = new byte[HeaderBytes + ciphertext.Length];
            Magic.CopyTo(envelope);
            BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8, 4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(12, 4), ciphertext.Length);
            ciphertext.CopyTo(envelope, HeaderBytes);
            return envelope;
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static byte[] Decrypt(byte[] envelope)
    {
        if (envelope.Length < HeaderBytes || envelope.Length > MaximumFileBytes || !HasEncryptedMagic(envelope)
            || BinaryPrimitives.ReadUInt32LittleEndian(envelope.AsSpan(8, 4)) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(12, 4)) != envelope.Length - HeaderBytes)
            throw new InvalidDataException("本地加密资料格式无效或版本不受支持");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地资料保护需要 Windows DPAPI");
        var payload = WindowsDataProtection.Unprotect(envelope.AsSpan(HeaderBytes).ToArray());
        try
        {
            if (!payload.AsSpan().StartsWith(Purpose) || payload.Length <= Purpose.Length || payload.Length - Purpose.Length > MaximumPlaintextBytes)
                throw new InvalidDataException("本地加密资料用途或大小无效");
            return payload.AsSpan(Purpose.Length).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static void WriteAtomically(string path, byte[] encrypted, byte[]? expectedHash, Action<string>? beforeCommitForVerification = null,
        Action<string,string>? replaceForVerification = null, Action<int>? waitForVerification = null)
    {
        if (!(HasEncryptedMagic(encrypted) || HasPasswordMagic(encrypted)) || encrypted.Length > MaximumFileBytes) throw new InvalidDataException("只允许写入加密资料");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        VerifyUnchanged(fullPath, expectedHash);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(encrypted); output.Flush(true); }
            beforeCommitForVerification?.Invoke(temporary);
            VerifyUnchanged(fullPath, expectedHash);
            if (expectedHash is null) File.Move(temporary, fullPath);
            else ReplaceWithRetry(temporary, fullPath, () => VerifyUnchanged(fullPath, expectedHash), replaceForVerification, waitForVerification);
        }
        finally
        {
            // A failed cleanup can leave ciphertext, never a plaintext temporary or backup.
            // Some native ReplaceFile errors can remove the target while leaving the
            // replacement. Preserve that ciphertext for recovery instead of deleting
            // the last surviving encrypted copy; subsequent loads fail closed.
            if(expectedHash is null||File.Exists(fullPath))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Windows file scanners can briefly hold a freshly written target. Retry only errors
    // documented to retain both names, and recheck the source before every attempt.
    // Four waits total 400ms; a persistent lock still throws to the caller's rollback path.
    internal static void ReplaceWithRetry(string temporary, string target, Action verifyUnchanged,
        Action<string,string>? replaceForVerification = null, Action<int>? waitForVerification = null)
    {
        for(int attempt=0;;attempt++)
        {
            verifyUnchanged();
            try { if(replaceForVerification is null) File.Replace(temporary,target,null); else replaceForVerification(temporary,target); return; }
            catch(IOException ex) when(attempt<4&&(ex.HResult&0xffff) is 32 or 33 or 1175)
            {
                int milliseconds=(attempt+1)*40;
                if(waitForVerification is null) Thread.Sleep(milliseconds); else waitForVerification(milliseconds);
            }
        }
    }

    internal static void VerifyUnchanged(string path, byte[]? expectedHash)
    {
        var current = ReadIfExists(path);
        try
        {
            if (current is null ? expectedHash is not null : expectedHash is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), expectedHash))
                throw new LocalFileChangedException();
        }
        finally { if (current is not null) CryptographicOperations.ZeroMemory(current); }
    }

    internal static void RejectInterruptedWrite(string path)
    {
        if(File.Exists(path)) return;
        var full=Path.GetFullPath(path);var directory=Path.GetDirectoryName(full)!;
        if(!Directory.Exists(directory)) return;
        string prefix=Path.GetFileName(full)+".";
        foreach(var candidate in Directory.EnumerateFiles(directory,prefix+"*.tmp"))
        {
            string name=Path.GetFileName(candidate);
            if(Guid.TryParseExact(name[prefix.Length..^4],"N",out _))
                throw new IOException("上次加密写入未完成，密文已保留。请保留资料目录，不要覆盖原文件。");
        }
    }

    private sealed class LimitedJsonBuffer : MemoryStream
    {
        private void CheckWrite(int count)
        { if (count < 0 || Position + count > MaximumPlaintextBytes) throw new InvalidDataException("本地资料超出大小限制"); }
        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
    }
}

internal sealed class LocalFileChangedException : IOException
{
    internal LocalFileChangedException() : base("本地资料已由其他操作更改，原文件已保留") { }
}
