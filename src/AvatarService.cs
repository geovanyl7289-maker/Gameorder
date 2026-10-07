using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Gameorder;

/// <summary>Loads public profile pictures without reading or using Steam login credentials.</summary>
public sealed class AvatarService : IDisposable
{
    private const int MaximumProfileBytes = 512 * 1024;
    private const int MaximumAvatarBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly SemaphoreSlim Downloads = new(4, 4);
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif"];
    private readonly string _steamPath;
    private readonly bool _demo;
    private readonly bool _allowNetwork;
    private readonly Func<byte[], bool>? _imageValidator;
    private readonly string _cacheRoot;
    private readonly HttpClient _http;
    private static long _retryAfterUtcTicks;
    public static DateTimeOffset? NextNetworkAttemptUtc
    {
        get { var ticks=Interlocked.Read(ref _retryAfterUtcTicks); return ticks>DateTime.UtcNow.Ticks ? new DateTimeOffset(new DateTime(ticks,DateTimeKind.Utc)) : null; }
    }

    public AvatarService(string steamPath, bool demo, string? cacheRoot = null, bool allowNetwork = true, Func<byte[], bool>? imageValidator = null)
        : this(steamPath, demo, cacheRoot, new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }, allowNetwork, imageValidator) { }

    // The injected handler lets isolated tests exercise responses without contacting Steam.
    internal AvatarService(string steamPath, bool demo, string? cacheRoot, HttpMessageHandler handler, bool allowNetwork = true, Func<byte[], bool>? imageValidator = null)
    {
        _steamPath = steamPath;
        _demo = demo;
        _allowNetwork = allowNetwork;
        _imageValidator = imageValidator;
        _cacheRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatOrder", "avatars");
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Gameorder/1.3");
    }

    public async Task<string?> LoadAvatarAsync(string steamId, CancellationToken cancellationToken = default)
    {
        // Manual accounts and demo IDs never cause requests or cache writes.
        if (_demo || !IsSteamId(steamId)) return null;
        cancellationToken.ThrowIfCancellationRequested();

        // Steam's avatarcache uses the exact SteamID64 as its filename. Never guess a
        // hash association, enumerate another account's picture, or read login files.
        var local = FindImage(Path.Combine(_steamPath, "config", "avatarcache"), steamId);
        if (local is not null) return local;
        var previous = FindImage(_cacheRoot, steamId, true);
        if (previous is not null && IsFresh(previous)) return previous;
        // Local-only mode still displays old usable pictures while network access is disabled.
        if (!_allowNetwork || IsBackingOff) return previous;

        await Downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBackingOff) return previous;
            // A second concurrent load may have populated this exact account's cache.
            var current = FindImage(_cacheRoot, steamId, true);
            if (current is not null && IsFresh(current)) return current;
            previous ??= current;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var profileUri = new Uri($"https://steamcommunity.com/profiles/{steamId}/?xml=1");
            var profile = await DownloadAsync(profileUri, MaximumProfileBytes, false, timeout.Token).ConfigureAwait(false);
            var avatarUri = ReadAvatarUri(profile, steamId, out var needsHtml, out var defaultAvatar);
            if (avatarUri is null && needsHtml)
            {
                var htmlUri = new Uri($"https://steamcommunity.com/profiles/{steamId}/");
                var html = await DownloadAsync(htmlUri, MaximumProfileBytes, false, timeout.Token).ConfigureAwait(false);
                avatarUri = ReadHtmlAvatar(html, steamId);
            }
            if (avatarUri is null) return previous;
            var image = await DownloadAsync(avatarUri, MaximumAvatarBytes, true, timeout.Token).ConfigureAwait(false);
            var extension = CompleteImageExtension(image);
            if (extension is null || !CanDecode(image)) return previous;
            timeout.Token.ThrowIfCancellationRequested();
            return await SaveImageAsync(steamId, extension, image, defaultAvatar, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return previous; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or XmlException or ArgumentException or InvalidOperationException)
        {
            // No account identifiers or response content are logged. Unavailable public
            // profiles, connection errors and invalid images preserve an older picture.
            return previous;
        }
        finally { Downloads.Release(); }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximumBytes, bool avatar, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (IsBackingOff) throw new HttpRequestException("公开头像服务暂时限流。", null, HttpStatusCode.TooManyRequests);
            if (!IsAllowedUri(uri, avatar)) throw new HttpRequestException("头像来源无效。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(avatar ? "image/png, image/jpeg, image/gif" : "application/xml, text/xml");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 429)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(5);
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);
                Interlocked.Exchange(ref _retryAfterUtcTicks, Math.Max(Interlocked.Read(ref _retryAfterUtcTicks), DateTime.UtcNow.Add(delay).Ticks));
            }
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new HttpRequestException("头像来源未返回位置。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                // Validate before any follow-up request; automatic redirects are disabled.
                if (!IsAllowedUri(uri, avatar)) throw new HttpRequestException("头像重定向来源无效。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
                throw new IOException("头像响应过大。");
            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var destination = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) break;
                if (destination.Length + read > maximumBytes) throw new IOException("头像响应过大。");
                destination.Write(buffer, 0, read);
            }
            return destination.ToArray();
        }
        throw new HttpRequestException("头像来源重定向次数过多。");
    }

    private static Uri? ReadAvatarUri(byte[] profile, string expectedSteamId, out bool needsHtml, out bool defaultAvatar)
    {
        needsHtml = false;
        defaultAvatar = false;
        using var bytes = new MemoryStream(profile, false);
        using var reader = XmlReader.Create(bytes, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumProfileBytes,
            IgnoreComments = true
        });
        var root = XDocument.Load(reader).Root;
        // The XML response must identify the requested account before associating a picture.
        if (root?.Name != "profile" || root.Element("steamID64")?.Value.Trim() != expectedSteamId) return null;
        var value = root.Element("avatarFull")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            // Unconfigured community profiles omit XML avatar fields, while their
            // public HTML still contains the profile's Steam default picture.
            needsHtml = true;
            defaultAvatar = root.Element("privacyMessage")?.Value.Contains("has not yet set up their Steam Community profile", StringComparison.OrdinalIgnoreCase) == true;
            return null;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsAllowedUri(uri, true)) return null;
        return uri;
    }

    private static Uri? ReadHtmlAvatar(byte[] content, string expectedSteamId)
    {
        try
        {
            var html = Encoding.UTF8.GetString(content);
            var assignment = Regex.Match(html, @"\bg_rgProfileData\s*=\s*", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (!assignment.Success) return null;
            var jsonBytes = Encoding.UTF8.GetBytes(html[(assignment.Index + assignment.Length)..]);
            var jsonReader = new Utf8JsonReader(jsonBytes);
            using var profile = JsonDocument.ParseValue(ref jsonReader);
            if (!profile.RootElement.TryGetProperty("steamid", out var identity) || identity.ValueKind != JsonValueKind.String || identity.GetString() != expectedSteamId) return null;

            // Steam's og:image may be its brand logo on an unconfigured profile.
            // Prefer the picture inside the profile's actual playerAvatar container.
            var avatarContainer = Regex.Match(html, @"<div\b[^>]*\bclass\s*=\s*(?:""[^""]*\bplayerAvatar\b[^""]*""|'[^']*\bplayerAvatar\b[^']*')[^>]*>(?<inside>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
            if (avatarContainer.Success)
            {
                foreach (Match imageTag in Regex.Matches(avatarContainer.Groups["inside"].Value, @"<img\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                {
                    var picture = AllowedAvatar(ReadAttribute(imageTag.Value, "src"));
                    if (picture is not null) return picture;
                }
            }
            foreach (Match meta in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            {
                if (!string.Equals(ReadAttribute(meta.Value, "property"), "og:image", StringComparison.OrdinalIgnoreCase)) continue;
                var picture = AllowedAvatar(ReadAttribute(meta.Value, "content"));
                // Only avatar resources qualify for the metadata fallback, never a logo.
                if (picture is not null && (picture.IdnHost.StartsWith("avatars.", StringComparison.OrdinalIgnoreCase) || picture.AbsolutePath.Contains("/avatars/", StringComparison.OrdinalIgnoreCase))) return picture;
            }
        }
        catch (Exception ex) when (ex is JsonException or RegexMatchTimeoutException or ArgumentException or InvalidOperationException) { }
        return null;

        static Uri? AllowedAvatar(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsAllowedUri(uri, true) ? uri : null;
    }

    private static string? ReadAttribute(string tag, string attribute)
    {
        foreach (Match value in Regex.Matches(tag, @"(?<key>[A-Za-z_:][\w:.-]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)')", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            if (value.Groups["key"].Value.Equals(attribute, StringComparison.OrdinalIgnoreCase)) return WebUtility.HtmlDecode(value.Groups["value"].Value);
        return null;
    }

    private static bool IsAllowedUri(Uri uri, bool avatar)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
        var host = uri.IdnHost;
        bool Within(string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        return Within("steamcommunity.com") || (avatar && (Within("steamstatic.com") || Within("steamusercontent.com") || Within("steampowered.com")));
    }

    private static bool IsSteamId(string value) => value is { Length: 17 } && value.All(c => c is >= '0' and <= '9');
    private bool IsBackingOff => Interlocked.Read(ref _retryAfterUtcTicks) > DateTime.UtcNow.Ticks;

    private string? FindImage(string root, string steamId, bool applicationCache = false)
    {
        try
        {
            foreach (var extension in applicationCache ? ImageExtensions.Concat(ImageExtensions.Select(e => ".steam-default" + e)) : ImageExtensions)
            {
                var path = Path.Combine(root, steamId + extension);
                if (!File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 16 or > MaximumAvatarBytes) continue;
                var image = new byte[checked((int)stream.Length)];
                stream.ReadExactly(image);
                if (CompleteImageExtension(image) is not null && CanDecode(image)) return Path.GetFullPath(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private bool CanDecode(byte[] image)
    {
        // The desktop supplies a WPF decoder; the service stays independent of WPF.
        // A corrupt existing image must not prevent fetching a usable replacement.
        try { return _imageValidator?.Invoke(image) ?? true; }
        catch { return false; }
    }

    private static string? ImageExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 16 && bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return ".png";
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        if (bytes.Length >= 10 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))) return ".gif";
        return null;
    }

    // Container completeness and bounded dimensions reject HTML, truncated files and
    // obvious invalid images before replacing a cache entry. The UI also decodes the
    // returned image with WPF, which is kept outside this service and its tests.
    private static string? CompleteImageExtension(ReadOnlySpan<byte> bytes)
    {
        var extension = ImageExtension(bytes);
        if (extension == ".png")
        {
            var offset = 8;
            var hasHeader = false;
            var hasData = false;
            while (offset <= bytes.Length - 12)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                if (size > bytes.Length - offset - 12) return null;
                var count = checked((int)size);
                var kind = bytes.Slice(offset + 4, 4);
                var data = bytes.Slice(offset + 8, count);
                var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + count, 4));
                if (PngCrc(bytes.Slice(offset + 4, count + 4)) != expectedCrc) return null;
                if (!hasHeader)
                {
                    if (!kind.SequenceEqual("IHDR"u8) || count != 13 || !ValidDimension(BinaryPrimitives.ReadUInt32BigEndian(data[..4])) || !ValidDimension(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)))) return null;
                    hasHeader = true;
                }
                else if (kind.SequenceEqual("IHDR"u8)) return null;
                if (kind.SequenceEqual("IDAT"u8)) hasData = true;
                if (kind.SequenceEqual("IEND"u8)) return hasData && count == 0 && offset + 12 == bytes.Length ? extension : null;
                offset += count + 12;
            }
            return null;
        }
        if (extension == ".gif")
        {
            return ValidDimension(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2))) && ValidDimension(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2))) && bytes[^1] == 0x3B ? extension : null;
        }
        if (extension == ".jpg")
        {
            if (bytes[^2] != 0xFF || bytes[^1] != 0xD9) return null;
            var offset = 2;
            var hasFrame = false;
            while (offset < bytes.Length - 2)
            {
                if (bytes[offset++] != 0xFF) return null;
                while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
                if (offset >= bytes.Length) return null;
                var marker = bytes[offset++];
                if (marker == 0xDA) return hasFrame ? extension : null;
                if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
                if (offset > bytes.Length - 2) return null;
                var size = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                if (size < 2 || size > bytes.Length - offset) return null;
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (size < 8 || !ValidDimension(BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2))) || !ValidDimension(BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2)))) return null;
                    hasFrame = true;
                }
                offset += size;
            }
        }
        return null;
    }

    private static bool ValidDimension(uint dimension) => dimension is > 0 and <= 4096;
    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }

    private static bool IsFresh(string path)
    {
        try { return File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow - CacheLifetime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsDefaultAvatarPath(string? path) => path is not null && Path.GetFileNameWithoutExtension(path).EndsWith(".steam-default", StringComparison.OrdinalIgnoreCase);

    private async Task<string> SaveImageAsync(string steamId, string extension, byte[] image, bool defaultAvatar, CancellationToken token)
    {
        Directory.CreateDirectory(_cacheRoot);
        var suffix = (defaultAvatar ? ".steam-default" : "") + extension;
        var path = Path.Combine(_cacheRoot, steamId + suffix);
        var temporary = Path.Combine(_cacheRoot, ".avatar-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, image, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            // Remove only alternate versions belonging to the same exact account so a
            // stale PNG cannot win over a newly downloaded JPEG on the next load.
            foreach (var other in ImageExtensions.Concat(ImageExtensions.Select(e => ".steam-default" + e)).Where(e => e != suffix))
            {
                try { File.Delete(Path.Combine(_cacheRoot, steamId + other)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return Path.GetFullPath(path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _http.Dispose();
}
