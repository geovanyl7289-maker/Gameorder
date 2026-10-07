using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Gameorder;

public static class AvatarServiceTests
{
    private const string Id = "76561198000000001";
    private const string OtherId = "76561198000000002";
    private static readonly byte[] Png = CreatePng();
    public static IReadOnlyList<(string Name, Func<Task> Run)> Tests =>
    [
        ("Avatar uses exact local SteamID picture without network or cache writes", async () =>
        {
            using var fixture = new Fixture();
            var path = fixture.LocalImage(Id);
            fixture.LocalImage(OtherId);
            using var service = fixture.Service((_, _) => throw new Exception("Unexpected network request"));
            Equal(path, await service.LoadAvatarAsync(Id));
            Check(!Directory.Exists(fixture.Cache), "local import wrote an application cache");
        }),
        ("Avatar never guesses a hash or another account picture", async () =>
        {
            using var fixture = new Fixture();
            fixture.LocalImage(OtherId);
            File.WriteAllBytes(Path.Combine(fixture.Steam, "config", "avatarcache", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png"), Png);
            var requests = 0;
            using var service = fixture.Service((_, _) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); });
            Equal(null, await service.LoadAvatarAsync(Id));
            Equal(1, requests);
        }),
        ("Avatar demo and invalid SteamIDs do not read, request or create cache", async () =>
        {
            using var fixture = new Fixture();
            fixture.LocalImage(Id);
            using var demo = fixture.Service((_, _) => throw new Exception("Unexpected network request"), true);
            Equal(null, await demo.LoadAvatarAsync(Id));
            using var service = fixture.Service((_, _) => throw new Exception("Unexpected network request"));
            foreach (var value in new[] { "", "../avatar", "123", "76561198000000001/x", "７6561198000000001" })
                Equal(null, await service.LoadAvatarAsync(value));
            Check(!Directory.Exists(fixture.Cache), "demo or invalid ID created cache");
        }),
        ("Avatar downloads matched public profile to application cache", async () =>
        {
            using var fixture = new Fixture();
            var requests = 0;
            using var service = fixture.Service((request, _) =>
            {
                requests++;
                Check(request.RequestUri!.Scheme == "https", "insecure request");
                Check(!request.Headers.Contains("Cookie") && request.Headers.Authorization is null, "request used credentials");
                return Task.FromResult(request.RequestUri.Host == "steamcommunity.com" ? Xml(Profile()) : Image(Png));
            });
            var path = await service.LoadAvatarAsync(Id);
            Check(path is not null && path.StartsWith(fixture.Cache, StringComparison.OrdinalIgnoreCase), "picture was not cached in application directory");
            Check(File.ReadAllBytes(path!).SequenceEqual(Png), "downloaded image changed");
            Equal(2, requests);
            Equal(path, await service.LoadAvatarAsync(Id));
            Equal(2, requests);
            Check(!Directory.Exists(Path.Combine(fixture.Steam, "config")), "download wrote to Steam");
        }),
        ("Avatar rejects XML account mismatch and foreign avatar hosts", async () =>
        {
            using var fixture = new Fixture();
            foreach (var xml in new[] { Profile(OtherId), Profile(Id, "https://steamstatic.com.example.org/avatar.png"), Profile(Id, "http://avatars.steamstatic.com/avatar.png"), Profile(Id, "https://user:password@avatars.steamstatic.com/avatar.png") })
            {
                var requests = 0;
                using var service = fixture.Service((_, _) => { requests++; return Task.FromResult(Xml(xml)); });
                Equal(null, await service.LoadAvatarAsync(Id));
                Equal(1, requests);
            }
            Check(!Directory.Exists(fixture.Cache), "invalid profile created cache");
        }),
        ("Avatar rejects DTD and oversized profile responses", async () =>
        {
            using var fixture = new Fixture();
            foreach (var xml in new[] { "<!DOCTYPE profile [<!ENTITY xxe SYSTEM 'file:///private'>]><profile><steamID64>" + Id + "</steamID64><avatarFull>&xxe;</avatarFull></profile>", new string('x', 512 * 1024 + 1) })
            {
                using var service = fixture.Service((_, _) => Task.FromResult(Xml(xml)));
                Equal(null, await service.LoadAvatarAsync(Id));
            }
            Check(!Directory.Exists(fixture.Cache), "rejected XML created cache");
        }),
        ("Avatar validates redirect destinations before following", async () =>
        {
            using var fixture = new Fixture();
            var requests = 0;
            using var service = fixture.Service((_, _) =>
            {
                requests++;
                var result = new HttpResponseMessage(HttpStatusCode.Redirect);
                result.Headers.Location = new Uri("https://example.org/avatar.png");
                return Task.FromResult(result);
            });
            Equal(null, await service.LoadAvatarAsync(Id));
            Equal(1, requests);
        }),
        ("Avatar refresh errors preserve a seven day old cache", async () =>
        {
            using var fixture = new Fixture();
            var path = fixture.CachedImage(Id, true);
            using var service = fixture.Service((_, _) => throw new HttpRequestException("Offline"));
            Equal(path, await service.LoadAvatarAsync(Id));
            Check(File.ReadAllBytes(path).SequenceEqual(Png), "old picture was overwritten");
        }),
        ("Avatar rejects truncated or oversized pictures without overwriting old cache", async () =>
        {
            using var fixture = new Fixture();
            var path = fixture.CachedImage(Id, true);
            foreach (var image in new[] { Png[..16], Encoding.UTF8.GetBytes("<html>error</html>"), new byte[2 * 1024 * 1024 + 1] })
            {
                using var service = fixture.Service((request, _) => Task.FromResult(request.RequestUri!.Host == "steamcommunity.com" ? Xml(Profile()) : Image(image)));
                Equal(path, await service.LoadAvatarAsync(Id));
                Check(File.ReadAllBytes(path).SequenceEqual(Png), "bad picture replaced old cache");
            }
            Check(!Directory.EnumerateFiles(fixture.Cache, "*.tmp").Any(), "temporary download leaked");
        }),
        ("Avatar cancellation propagates without persisting partial files", async () =>
        {
            using var fixture = new Fixture();
            using var service = fixture.Service(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Xml(Profile()); });
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try { await service.LoadAvatarAsync(Id, cancel.Token); throw new Exception("cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Check(!Directory.Exists(fixture.Cache), "cancelled request wrote cache");
        }),
        ("Avatar requests never exceed four concurrent downloads", async () =>
        {
            using var fixture = new Fixture();
            var active = 0;
            var maximum = 0;
            using var service = fixture.Service(async (_, token) =>
            {
                var current = Interlocked.Increment(ref active);
                lock (fixture) maximum = Math.Max(maximum, current);
                try { await Task.Delay(20, token); return new HttpResponseMessage(HttpStatusCode.NotFound); }
                finally { Interlocked.Decrement(ref active); }
            });
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => service.LoadAvatarAsync((76561198000000001L + i).ToString())));
            Check(maximum is > 1 and <= 4, "unexpected concurrent request limit");
        }),
        ("Avatar local-only mode displays local and old cached pictures without requests", async () =>
        {
            using var fixture = new Fixture();
            var local = fixture.LocalImage(Id);
            var previous = fixture.CachedImage(OtherId, true);
            using var service = fixture.Service((_, _) => throw new Exception("Unexpected network request"), allowNetwork: false);
            Equal(local, await service.LoadAvatarAsync(Id));
            Equal(previous, await service.LoadAvatarAsync(OtherId));
            Equal(null, await service.LoadAvatarAsync("76561198000000003"));
            Equal(1, Directory.EnumerateFiles(fixture.Cache).Count());
        }),
        ("Avatar decoder rejects unusable local images and allows a valid network replacement", async () =>
        {
            using var fixture = new Fixture();
            var local = fixture.LocalImage(Id);
            File.WriteAllBytes(local, CreatePng(33));
            var requests = 0;
            using var service = fixture.Service((request, _) =>
            {
                requests++;
                return Task.FromResult(request.RequestUri!.Host == "steamcommunity.com" ? Xml(Profile()) : Image(Png));
            }, imageValidator: bytes => bytes.SequenceEqual(Png));
            var result = await service.LoadAvatarAsync(Id);
            Check(result is not null && result != local && result.StartsWith(fixture.Cache, StringComparison.OrdinalIgnoreCase), "undecodable local image blocked replacement");
            Equal(2, requests);
        }),
        ("Avatar decoder rejects broken downloads before replacing an older picture", async () =>
        {
            using var fixture = new Fixture();
            var previous = fixture.CachedImage(Id, true);
            using var service = fixture.Service((request, _) => Task.FromResult(request.RequestUri!.Host == "steamcommunity.com" ? Xml(Profile()) : Image(CreatePng(44))), imageValidator: bytes => bytes.SequenceEqual(Png));
            Equal(previous, await service.LoadAvatarAsync(Id));
            Check(File.ReadAllBytes(previous).SequenceEqual(Png), "undecodable download replaced an older usable image");
            using var throwing = fixture.Service((_, _) => throw new Exception("Unexpected network request"), allowNetwork: false, imageValidator: _ => throw new InvalidOperationException("Decoder error"));
            Equal(null, await throwing.LoadAvatarAsync(Id));
        }),
        ("Avatar HTML fallback validates ownership and displays the profile default picture", async () =>
        {
            using var fixture = new Fixture();
            var requests = 0;
            using var service = fixture.Service((request, _) =>
            {
                requests++;
                return Task.FromResult(request.RequestUri!.Host != "steamcommunity.com" ? Image(Png) : request.RequestUri.Query.Length != 0 ? Xml(ProfileWithoutAvatar(true)) : Html(HtmlProfile(Id)));
            });
            var path = await service.LoadAvatarAsync(Id);
            Check(path is not null && AvatarService.IsDefaultAvatarPath(path), "Steam's explicit default avatar was not marked");
            Equal(3, requests);
            Equal(path, await service.LoadAvatarAsync(Id));
            Equal(3, requests);
        }),
        ("Avatar HTML fallback refuses a different profile and a Steam brand logo", async () =>
        {
            using var fixture = new Fixture();
            foreach (var html in new[] { HtmlProfile(OtherId), HtmlProfile(Id, false) })
            {
                var requests = 0;
                using var service = fixture.Service((request, _) =>
                {
                    requests++;
                    return Task.FromResult(request.RequestUri!.Query.Length != 0 ? Xml(ProfileWithoutAvatar(true)) : Html(html));
                });
                Equal(null, await service.LoadAvatarAsync(Id));
                Equal(2, requests);
            }
            Check(!Directory.Exists(fixture.Cache), "wrong profile or Steam logo was persisted as an avatar");
        }),
        ("Avatar private profile HTML fallback preserves its custom avatar status", async () =>
        {
            using var fixture = new Fixture();
            using var service = fixture.Service((request, _) => Task.FromResult(request.RequestUri!.Host != "steamcommunity.com" ? Image(Png) : request.RequestUri.Query.Length != 0 ? Xml(ProfileWithoutAvatar(false)) : Html(HtmlProfile(Id))));
            var path = await service.LoadAvatarAsync(Id);
            Check(path is not null && !AvatarService.IsDefaultAvatarPath(path), "private profile custom picture was labelled default");
        }),
        ("Avatar rate limiting preserves cached pictures and backs off remaining batch requests", async () =>
        {
            using var fixture = new Fixture();
            var previous = fixture.CachedImage(Id, true);
            var requests = 0;
            using var service = fixture.Service((_, _) =>
            {
                requests++;
                var result = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                result.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(result);
            });
            Equal(previous, await service.LoadAvatarAsync(Id));
            Equal(null, await service.LoadAvatarAsync(OtherId));
            Equal(previous, await service.LoadAvatarAsync(Id));
            Equal(1, requests);
            Check(AvatarService.NextNetworkAttemptUtc.HasValue,"rate limit did not expose retry time");
            using var another = fixture.Service((_,_) => throw new Exception("Repeated refresh bypassed rate limit"));
            Equal(null,await another.LoadAvatarAsync(OtherId));
        })
    ];

    private static string Profile(string steamId = Id, string url = "https://avatars.steamstatic.com/test_full.png") =>
        $"<profile><steamID64>{steamId}</steamID64><avatarFull><![CDATA[{url}]]></avatarFull></profile>";
    private static HttpResponseMessage Xml(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/xml") };
    private static HttpResponseMessage Html(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/html") };
    private static HttpResponseMessage Image(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static string ProfileWithoutAvatar(bool unconfigured) => $"<profile><steamID64>{Id}</steamID64><privacyMessage>{(unconfigured ? "This user has not yet set up their Steam Community profile." : "This profile is private.")}</privacyMessage></profile>";
    private static string HtmlProfile(string id, bool avatar = true) =>
        "<html><script>var g_rgProfileData = {\"steamid\":\"" + id + "\",\"personaname\":\"Fixture; name\"};</script>" +
        "<meta property='og:image' content='https://community.fastly.steamstatic.com/public/images/steam_logo.png'>" +
        (avatar ? "<div class='playerAvatar profile_header_size'><div class='playerAvatarAutoSizeInner'><img src='https://avatars.steamstatic.com/profile_full.png'></div></div>" : "") + "</html>";
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal(string? expected, string? actual) => Check(expected == actual, "unexpected image path");
    private static void Equal(int expected, int actual) => Check(expected == actual, "unexpected request count");

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "floatorder-avatar-tests-" + Guid.NewGuid().ToString("N"));
        public string Steam => Path.Combine(Root, "Steam");
        public string Cache => Path.Combine(Root, "ApplicationCache");
        public Fixture() => Directory.CreateDirectory(Steam);
        public AvatarService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response, bool demo = false, bool allowNetwork = true, Func<byte[], bool>? imageValidator = null) => new(Steam, demo, Cache, new ResponseHandler(response), allowNetwork, imageValidator);
        public string LocalImage(string id)
        {
            var folder = Path.Combine(Steam, "config", "avatarcache");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, id + ".png");
            File.WriteAllBytes(path, Png);
            return path;
        }
        public string CachedImage(string id, bool stale)
        {
            Directory.CreateDirectory(Cache);
            var path = Path.Combine(Cache, id + ".png");
            File.WriteAllBytes(path, Png);
            if (stale) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            return path;
        }
        public void Dispose()
        {
            var resolved=Path.GetFullPath(Root);
            var temporary=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!resolved.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("floatorder-avatar-tests-",StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup target.");
            if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
        }
    }
    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private static byte[] CreatePng(byte pixel = 100)
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8; header[9] = 6;
        WriteChunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(new byte[] { 0, pixel, 150, 200, 255 });
        WriteChunk("IDAT", compressed.ToArray());
        WriteChunk("IEND", []);
        return result.ToArray();

        void WriteChunk(string type, byte[] data)
        {
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)data.Length);
            result.Write(prefix);
            var content = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            result.Write(content);
            var crc = uint.MaxValue;
            foreach (var value in content)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(prefix, ~crc);
            result.Write(prefix);
        }
    }
}
