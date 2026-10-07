using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Gameorder;

static class InstanceServiceTests
{
    public static IReadOnlyList<(string Name,Func<Task> Run)> Tests =>
    [
        ("Instance protocol rejects malformed account identities before connecting",async()=>
        {
            foreach(var identity in new[]{"","7656119800000000","765611980000000001","7656119800000000x","account:a","account:has space","account:../alpha","account:中文账号",new string('9',4097)})
            {
                var command=new InstanceCommand("switch",identity,"online");
                Check(!InstanceMessageService.IsValid(command),"invalid identity passed validation");
                var response=await InstanceMessageService.SendAsync(Namespace(),command);
                Check(!response.Accepted&&response.Code=="invalid-command","invalid identity attempted a request");
            }
        }),
        ("Instance protocol validates modes and field combinations",async()=>
        {
            foreach(var command in new[]
            {
                new InstanceCommand("switch","76561198000000001","Online"),new InstanceCommand("switch","76561198000000001","bad-mode"),new InstanceCommand("switch","76561198000000001"),
                new InstanceCommand("activate","76561198000000001"),new InstanceCommand("quick-menu",null,"online"),new InstanceCommand("run-executable","account:alpha","online")
            })
                Check(!InstanceMessageService.IsValid(command),"bad mode/fields passed validation");
            using var f=new ProtocolFixture();
            var response=await RawRequest(f.Name,"""{"Action":"switch","Identity":"76561198000000001","Mode":"bad-mode"}""");
            Check(!response.Accepted&&response.Code=="invalid-command","server trusted client validation"); Equal(0,f.Commands.Count);
        }),
        ("Instance namespace rejects path characters and unrelated pipe names",()=>Run(()=>
        {
            foreach(var name in new[]{"OtherApp","Gameorder/alpha","Gameorder..alpha","Gameorder.alpha beta","Gameorder\\alpha"}) Throws<ArgumentException>(()=>InstanceMessageService.GetPipeName(name));
            Check(InstanceMessageService.GetPipeName(Namespace()).EndsWith(".Instance",StringComparison.Ordinal),"valid test namespace rejected");
        })),
        ("Test cancellation cannot be enabled in the normal application namespace",async()=>
        {
            var command=new InstanceCommand("test-cancel");
            Check(!InstanceMessageService.IsValid(command),"normal command validation enabled test cancellation");
            // Send must reject before opening the production pipe, even if the caller supplies true.
            var response=await InstanceMessageService.SendAsync("Gameorder",command,allowTestCancel:true);
            Check(!response.Accepted&&response.Code=="invalid-command","normal namespace enabled verification-only cancellation");
            using var f=new ProtocolFixture();
            response=await InstanceMessageService.SendAsync(f.Name,command,allowTestCancel:false);
            Check(!response.Accepted&&response.Code=="invalid-command","default test namespace accepted test cancellation"); Equal(0,f.Commands.Count);
        }),
        ("Same-user instance request returns response with a simulated switch only",async()=>
        {
            using var f=new ProtocolFixture();
            var command=new InstanceCommand("switch","76561198000000001","offline");
            var response=await InstanceMessageService.SendAsync(f.Name,command);
            Check(response.Accepted&&response.Code=="simulated","valid same-user switch was not acknowledged");
            Equal(1,f.Commands.Count); Equal(command,f.Commands.Single());
            response=await InstanceMessageService.SendAsync(f.Name,new("switch","account:manual_user","online"));
            Check(response.Accepted,"manual identity request failed"); Equal(2,f.Commands.Count);
        }),
        ("Unknown IPC fields are rejected and server stays healthy",async()=>
        {
            using var f=new ProtocolFixture();
            var response=await RawRequest(f.Name,"""{"Action":"activate","Execute":"unexpected"}""");
            Check(!response.Accepted&&response.Code=="invalid-message","unknown command fields were silently ignored"); Equal(0,f.Commands.Count);
            await CheckHealthy(f);
        }),
        ("Oversized and nonpositive IPC frames are rejected without invoking handler",async()=>
        {
            using var f=new ProtocolFixture();
            foreach(var length in new[]{InstanceMessageService.MaximumMessageBytes+1,int.MaxValue,0,-1})
            {
                var response=await RawFrame(f.Name,length,[]);
                Check(!response.Accepted&&response.Code=="invalid-message","invalid frame size was accepted"); Equal(0,f.Commands.Count);
            }
            await CheckHealthy(f);
        }),
        ("Partial IPC frame disconnect does not stop later valid requests",async()=>
        {
            using var f=new ProtocolFixture();
            await DisconnectPartial(f.Name,headerOnly:true);
            await CheckHealthy(f);
            await DisconnectPartial(f.Name,headerOnly:false);
            await CheckHealthy(f); Equal(2,f.Commands.Count);
        }),
        ("Verification-only test cancellation is isolated to a unique GUID namespace",async()=>
        {
            using var f=new ProtocolFixture(allowTestCancel:true);
            var response=await InstanceMessageService.SendAsync(f.Name,new("test-cancel"),allowTestCancel:true);
            Check(response.Accepted&&response.Code=="simulated","verification namespace refused test cancellation"); Equal("test-cancel",f.Commands.Single().Action);
        })
    ];

    private static string Namespace()=>"Gameorder.ProtocolTest."+Guid.NewGuid().ToString("N");
    private static Task Run(Action action) { action(); return Task.CompletedTask; }
    private static async Task CheckHealthy(ProtocolFixture fixture)
    {
        var response=await InstanceMessageService.SendAsync(fixture.Name,new("activate"));
        Check(response.Accepted&&response.Code=="simulated","server did not recover from malformed client");
    }
    private static Task<InstanceResponse> RawRequest(string name,string json)
    {
        var data=Encoding.UTF8.GetBytes(json); return RawFrame(name,data.Length,data);
    }
    private static async Task<NamedPipeClientStream> Connect(string name,CancellationToken token)
    {
        var client=new NamedPipeClientStream(".",InstanceMessageService.GetPipeName(name),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync(4000,token); return client; } catch { client.Dispose(); throw; }
    }
    private static async Task<InstanceResponse> RawFrame(string name,int length,byte[] data)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client=await Connect(name,deadline.Token);
        var header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,length);
        await client.WriteAsync(header,deadline.Token); if(data.Length>0) await client.WriteAsync(data,deadline.Token); await client.FlushAsync(deadline.Token);
        await client.ReadExactlyAsync(header,deadline.Token);
        var count=BinaryPrimitives.ReadInt32LittleEndian(header); Check(count>0&&count<=InstanceMessageService.MaximumMessageBytes,"unbounded server response");
        var response=new byte[count]; await client.ReadExactlyAsync(response,deadline.Token);
        return JsonSerializer.Deserialize<InstanceResponse>(response) ?? throw new Exception("empty server response");
    }
    private static async Task DisconnectPartial(string name,bool headerOnly)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var client=await Connect(name,deadline.Token);
        if(headerOnly) await client.WriteAsync(new byte[]{0x30,0x00},deadline.Token);
        else
        {
            var header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,100);
            await client.WriteAsync(header,deadline.Token); await client.WriteAsync(Encoding.UTF8.GetBytes("{\"Action\":"),deadline.Token);
        }
        await client.FlushAsync(deadline.Token);
    }
    private static void Check(bool result,string message) { if(!result) throw new Exception(message); }
    private static void Equal<T>(T expected,T actual) { if(!EqualityComparer<T>.Default.Equals(expected,actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }
    private sealed class ProtocolFixture:IDisposable
    {
        public string Name { get; }=Namespace();
        public ConcurrentQueue<InstanceCommand> Commands { get; }=new();
        private readonly InstanceMessageService _server;
        public ProtocolFixture(bool allowTestCancel=false)
        {
            _server=new(Name,allowTestCancel,command=> { Commands.Enqueue(command); return Task.FromResult(new InstanceResponse(true,"simulated")); });
        }
        public void Dispose()=>_server.Dispose();
    }
}
