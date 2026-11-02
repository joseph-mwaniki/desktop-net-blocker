using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DesktopNetBlocker.Core.Data;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Network;
using Microsoft.Extensions.Logging;

namespace DesktopNetBlocker.Service.Network;

public class DnsProxyServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockerDbContext _db;
    private readonly IPEndPoint _upstreamDns = new(IPAddress.Parse("1.1.1.1"), 53);
    private readonly CancellationTokenSource _cts = new();

    private Socket? _udpV4Socket;
    private Task? _listenerTaskV4;

    private readonly object _rulesLock = new();
    private List<BlockedDomain> _cachedBlockedDomains = [];
    private HashSet<string> _cachedAllowedTempDomains = new(StringComparer.OrdinalIgnoreCase);

    public DnsProxyServer(ILogger logger, BlockerDbContext db)
    {
        _logger = logger;
        _db = db;
        RefreshRules();
    }

    public void RefreshRules()
    {
        lock (_rulesLock)
        {
            _cachedBlockedDomains = _db.GetActiveBlockedDomains();

            var tempAccesses = _db.GetActiveTemporaryAccessList();
            _cachedAllowedTempDomains.Clear();
            foreach (var t in tempAccesses)
            {
                _cachedAllowedTempDomains.Add(t.TargetDomain);
            }
        }
    }

    public void Start(int port = 53)
    {
        try
        {
            _udpV4Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpV4Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpV4Socket.Bind(new IPEndPoint(IPAddress.Loopback, port));

            _listenerTaskV4 = Task.Run(() => ListenUdpLoopAsync(_udpV4Socket, _cts.Token));
            _logger.LogInformation("DNS Proxy Server listening on 127.0.0.1:{Port}", port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bind DNS Proxy Server on port {Port}: {Message}", port, ex.Message);
        }
    }

    private async Task ListenUdpLoopAsync(Socket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        EndPoint clientRemote = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, clientRemote);
                int receivedBytes = result.ReceivedBytes;
                EndPoint clientEp = result.RemoteEndPoint;

                byte[] queryData = new byte[receivedBytes];
                Buffer.BlockCopy(buffer, 0, queryData, 0, receivedBytes);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessDnsQueryAsync(socket, queryData, clientEp, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error processing DNS packet: {Message}", ex.Message);
                    }
                }, ct);
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("DNS listener receive exception: {Message}", ex.Message);
                    await Task.Delay(50, ct);
                }
            }
        }
    }

    private async Task ProcessDnsQueryAsync(Socket socket, byte[] queryBytes, EndPoint clientEp, CancellationToken ct)
    {
        if (!DnsMessageParser.TryParseQuery(queryBytes, queryBytes.Length, out ushort txId, out string qname, out ushort qtype))
        {
            // Forward malformed or unsupported query directly to upstream
            await ForwardToUpstreamAndReplyAsync(socket, queryBytes, clientEp, ct);
            return;
        }

        bool isBlocked = IsDomainBlocked(qname);

        if (isBlocked)
        {
            _logger.LogInformation("[DNS BLOCKED] Intercepted blocked query for '{QName}' (Type {QType})", qname, qtype);
            byte[] blockedResponse = DnsMessageParser.CreateBlockedResponse(queryBytes, queryBytes.Length, txId, qname, qtype);
            await socket.SendToAsync(blockedResponse, SocketFlags.None, clientEp);
            _db.RecordEvent("DnsQueryBlocked", qname, $"Blocked DNS resolution for {qname}");
        }
        else
        {
            await ForwardToUpstreamAndReplyAsync(socket, queryBytes, clientEp, ct);
        }
    }

    private bool IsDomainBlocked(string qname)
    {
        lock (_rulesLock)
        {
            // Check if domain is granted temporary access
            if (_cachedAllowedTempDomains.Contains(qname))
                return false;

            // Check against active blocked rules
            foreach (var rule in _cachedBlockedDomains)
            {
                if (DomainMatcher.Matches(qname, rule.Domain, rule.MatchingMode))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private async Task ForwardToUpstreamAndReplyAsync(Socket clientSocket, byte[] queryBytes, EndPoint clientEp, CancellationToken ct)
    {
        using var upstreamSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        upstreamSocket.ReceiveTimeout = 2500;
        upstreamSocket.SendTimeout = 2500;

        try
        {
            await upstreamSocket.SendToAsync(queryBytes, SocketFlags.None, _upstreamDns);

            byte[] respBuffer = new byte[2048];
            EndPoint upstreamEp = new IPEndPoint(IPAddress.Any, 0);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(2500);

            var result = await upstreamSocket.ReceiveFromAsync(respBuffer, SocketFlags.None, upstreamEp);
            byte[] replyBytes = new byte[result.ReceivedBytes];
            Buffer.BlockCopy(respBuffer, 0, replyBytes, 0, result.ReceivedBytes);

            await clientSocket.SendToAsync(replyBytes, SocketFlags.None, clientEp);
        }
        catch (Exception)
        {
            // Upstream resolution failed or timed out
        }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _udpV4Socket?.Close(); } catch { }
        try { _udpV4Socket?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}

