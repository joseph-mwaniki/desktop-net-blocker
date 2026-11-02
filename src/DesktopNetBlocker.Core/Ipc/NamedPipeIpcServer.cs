using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopNetBlocker.Core.Ipc;

public class NamedPipeIpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<IpcMessage, Task<IpcResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;

    public NamedPipeIpcServer(string pipeName, Func<IpcMessage, Task<IpcResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start()
    {
        _listenerTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Create secure pipe security allow World / Authenticated Users read/write
                    var pipeSecurity = new PipeSecurity();
                    var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                    pipeSecurity.AddAccessRule(new PipeAccessRule(everyone, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    var current = WindowsIdentity.GetCurrent().User;
                    if (current != null)
                    {
                        pipeSecurity.AddAccessRule(new PipeAccessRule(current, PipeAccessRights.FullControl, AccessControlType.Allow));
                    }

                    serverStream = NamedPipeServerStreamAcl.Create(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);
                }
                else
                {
                    serverStream = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }

                await serverStream.WaitForConnectionAsync(_cts.Token);
                _ = ProcessClientAsync(serverStream, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IPC Server] Exception accepting connection: {ex.Message}");
                await Task.Delay(500, _cts.Token);
            }
        }
    }

    private async Task ProcessClientAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            using (stream)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrEmpty(line))
                {
                    var msg = JsonSerializer.Deserialize<IpcMessage>(line);
                    IpcResponse response;
                    if (msg != null)
                    {
                        response = await _handler(msg);
                    }
                    else
                    {
                        response = IpcResponse.Fail("Invalid or malformed IPC message.");
                    }

                    string respJson = JsonSerializer.Serialize(response);
                    await writer.WriteLineAsync(respJson.AsMemory(), ct);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPC Server] Client handling exception: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _listenerTask?.Wait(1000);
        }
        catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}

