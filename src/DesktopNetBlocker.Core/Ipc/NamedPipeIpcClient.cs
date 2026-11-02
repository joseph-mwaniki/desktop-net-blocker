using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopNetBlocker.Core.Ipc;

public class NamedPipeIpcClient
{
    private readonly string _pipeName;

    public NamedPipeIpcClient(string pipeName = IpcConstants.PipeName)
    {
        _pipeName = pipeName;
    }

    public async Task<IpcResponse> SendAsync(string messageType, object? payload = null, int timeoutMs = IpcConstants.DefaultTimeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var clientStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await clientStream.ConnectAsync(cts.Token);

            using var writer = new StreamWriter(clientStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(clientStream, Encoding.UTF8, leaveOpen: true);

            var msg = new IpcMessage
            {
                MessageType = messageType,
                PayloadJson = payload != null ? JsonSerializer.Serialize(payload) : null
            };

            string json = JsonSerializer.Serialize(msg);
            await writer.WriteLineAsync(json.AsMemory(), cts.Token);

            string? responseLine = await reader.ReadLineAsync(cts.Token);
            if (string.IsNullOrEmpty(responseLine))
            {
                return IpcResponse.Fail("Empty response from background service.");
            }

            var resp = JsonSerializer.Deserialize<IpcResponse>(responseLine);
            return resp ?? IpcResponse.Fail("Malformed response from background service.");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail("Connection to background service timed out.");
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Fail("IPC operation timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail($"IPC communication error: {ex.Message}");
        }
    }
}

