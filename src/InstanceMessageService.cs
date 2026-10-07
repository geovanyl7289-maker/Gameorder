using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gameorder;

public sealed record InstanceCommand(string Action, string? Identity = null, string? Mode = null);
public sealed record InstanceResponse(bool Accepted, string Code);

/// <summary>Bounded same-user IPC. A switch message requests confirmation; it never confirms one.</summary>
public sealed class InstanceMessageService : IDisposable
{
    public const int MaximumMessageBytes = 4096;
    private readonly string _pipeName;
    private readonly bool _allowTestCancel;
    private readonly Func<InstanceCommand,Task<InstanceResponse>> _handler;
    private readonly Action? _exitAcknowledged;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private bool _disposed;

    public InstanceMessageService(string instanceNamespace, bool allowTestCancel, Func<InstanceCommand,Task<InstanceResponse>> handler,Action? exitAcknowledged=null)
    {
        _pipeName = GetPipeName(instanceNamespace);
        _allowTestCancel = allowTestCancel && IsVerificationNamespace(instanceNamespace);
        _handler = handler;
        _exitAcknowledged=exitAcknowledged;
        _listener = ListenAsync();
    }

    public static string GetPipeName(string instanceNamespace)
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户。");
        if (!Regex.IsMatch(instanceNamespace,@"^Gameorder(?:\.[A-Za-z0-9]+)*$",RegexOptions.CultureInvariant)) throw new ArgumentException("实例名称无效。");
        return instanceNamespace + "." + user + ".Instance";
    }

    public static bool IsVerificationNamespace(string instanceNamespace)
    {
        foreach(string prefix in new[]{"Gameorder.InstanceVerification.","Gameorder.ProtocolTest."})
            if(instanceNamespace.StartsWith(prefix,StringComparison.Ordinal)&&Guid.TryParseExact(instanceNamespace[prefix.Length..],"N",out _)) return true;
        return false;
    }

    public static bool IsValid(InstanceCommand command, bool allowTestCancel = false)
    {
        if (command.Action is "activate" or "quick-menu" or "exit" || allowTestCancel && command.Action == "test-cancel")
            return command.Identity is null && command.Mode is null;
        if (command.Action != "switch" || command.Mode is not ("online" or "offline") || command.Identity is null) return false;
        return Regex.IsMatch(command.Identity,@"^[0-9]{17}$",RegexOptions.CultureInvariant)
            || Regex.IsMatch(command.Identity,@"^account:[A-Za-z0-9_]{2,64}$",RegexOptions.CultureInvariant);
    }

    public static async Task<InstanceResponse> SendAsync(string instanceNamespace,InstanceCommand command,bool allowTestCancel=false,CancellationToken cancellationToken=default)
    {
        if (!IsValid(command,allowTestCancel && IsVerificationNamespace(instanceNamespace))) return new(false,"invalid-command");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new NamedPipeClientStream(".",GetPipeName(instanceNamespace),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            // Connect waits for a primary instance that is still starting its UI.
            await client.ConnectAsync(4000,deadline.Token).ConfigureAwait(false);
            await WriteMessageAsync(client,command,deadline.Token).ConfigureAwait(false);
            return await ReadMessageAsync<InstanceResponse>(client,deadline.Token).ConfigureAwait(false) ?? new(false,"invalid-response");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return new(false,"instance-unavailable"); }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,MaximumMessageBytes,MaximumMessageBytes);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                InstanceResponse response;
                try
                {
                    var command = await ReadMessageAsync<InstanceCommand>(server,deadline.Token).ConfigureAwait(false);
                    response = command is not null && IsValid(command,_allowTestCancel)
                        ? await _handler(command).ConfigureAwait(false) : new(false,"invalid-command");
                }
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or OperationCanceledException)
                { response = new(false,"invalid-message"); }
                await WriteMessageAsync(server,response,deadline.Token).ConfigureAwait(false);
                if(response.Accepted&&response.Code=="exiting") _exitAcknowledged?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    try { await Task.Delay(150,_stop.Token).ConfigureAwait(false); }
                    catch(OperationCanceledException) { break; }
                }
            }
        }
    }

    private static async Task WriteMessageAsync<T>(Stream stream,T value,CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        if (data.Length == 0 || data.Length > MaximumMessageBytes) throw new ArgumentException("消息过长。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,data.Length);
        await stream.WriteAsync(header,token).ConfigureAwait(false);
        await stream.WriteAsync(data,token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<T?> ReadMessageAsync<T>(Stream stream,CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header,token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count <= 0 || count > MaximumMessageBytes) throw new ArgumentException("消息大小无效。");
        var data = new byte[count]; await stream.ReadExactlyAsync(data,token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(data,new JsonSerializerOptions { MaxDepth=4,UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed=true; _stop.Cancel();
        // The listener owns its pipes. Cancelling asynchronously leaves the UI free to close.
        _ = _listener.ContinueWith(_=>_stop.Dispose(),TaskScheduler.Default);
    }
}
