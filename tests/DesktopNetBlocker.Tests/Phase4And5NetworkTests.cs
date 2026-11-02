using System;
using System.Collections.Generic;
using System.IO;
using DesktopNetBlocker.Core.Models;
using DesktopNetBlocker.Core.Network;
using DesktopNetBlocker.Service.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNetBlocker.Tests;

public class Phase4And5NetworkTests : IDisposable
{
    private readonly string _tempHostsFile;

    public Phase4And5NetworkTests()
    {
        _tempHostsFile = Path.Combine(Path.GetTempPath(), $"hosts_test_{Guid.NewGuid():N}");
        File.WriteAllText(_tempHostsFile, "# Localhost mappings\n127.0.0.1 localhost\n::1 localhost\n");
    }

    public void Dispose()
    {
        if (File.Exists(_tempHostsFile))
        {
            try { File.Delete(_tempHostsFile); } catch { }
        }
    }

    [Fact]
    public void DnsParser_ParseAndCreateBlockedResponse_IPv4()
    {
        // Build a mock DNS query packet for "example.com", Type A (1), Class IN (1)
        byte[] query = [
            0x12, 0x34, // Transaction ID: 0x1234
            0x01, 0x00, // Standard query, RD=1
            0x00, 0x01, // QDCOUNT = 1
            0x00, 0x00, // ANCOUNT = 0
            0x00, 0x00, // NSCOUNT = 0
            0x00, 0x00, // ARCOUNT = 0
            // QNAME: 7example3com0
            0x07, 0x65, 0x78, 0x61, 0x6D, 0x70, 0x6C, 0x65,
            0x03, 0x63, 0x6F, 0x6D,
            0x00,
            0x00, 0x01, // Type A
            0x00, 0x01  // Class IN
        ];

        bool success = DnsMessageParser.TryParseQuery(query, query.Length, out ushort txId, out string qname, out ushort qtype);
        Assert.True(success);
        Assert.Equal(0x1234, txId);
        Assert.Equal("example.com", qname);
        Assert.Equal(DnsMessageParser.TypeA, qtype);

        // Generate blocked response
        byte[] response = DnsMessageParser.CreateBlockedResponse(query, query.Length, txId, qname, qtype);
        Assert.NotNull(response);
        Assert.True(response.Length > query.Length);

        // Check Transaction ID in response
        Assert.Equal(0x12, response[0]);
        Assert.Equal(0x34, response[1]);

        // Check Response Flag QR = 1
        Assert.True((response[2] & 0x80) != 0);

        // Check ANCOUNT = 1
        Assert.Equal(0x00, response[6]);
        Assert.Equal(0x01, response[7]);

        // Check IPv4 RDATA contains 0.0.0.0 (last 4 bytes)
        int len = response.Length;
        Assert.Equal(0, response[len - 4]);
        Assert.Equal(0, response[len - 3]);
        Assert.Equal(0, response[len - 2]);
        Assert.Equal(0, response[len - 1]);
    }

    [Fact]
    public void DnsParser_ParseAndCreateBlockedResponse_IPv6()
    {
        // Mock query for "distraction.net", Type AAAA (28)
        byte[] query = [
            0xAB, 0xCD, // Tx ID
            0x01, 0x00,
            0x00, 0x01,
            0x00, 0x00,
            0x00, 0x00,
            0x00, 0x00,
            0x0B, 0x64, 0x69, 0x73, 0x74, 0x72, 0x61, 0x63, 0x74, 0x69, 0x6F, 0x6E,
            0x03, 0x6E, 0x65, 0x74,
            0x00,
            0x00, 0x1C, // Type AAAA (28)
            0x00, 0x01  // Class IN
        ];

        bool success = DnsMessageParser.TryParseQuery(query, query.Length, out ushort txId, out string qname, out ushort qtype);
        Assert.True(success);
        Assert.Equal(0xABCD, txId);
        Assert.Equal("distraction.net", qname);
        Assert.Equal(DnsMessageParser.TypeAAAA, qtype);

        byte[] response = DnsMessageParser.CreateBlockedResponse(query, query.Length, txId, qname, qtype);
        Assert.NotNull(response);

        // Verify last 16 bytes are all 0 (IPv6 unspecified :: address)
        int len = response.Length;
        for (int i = len - 16; i < len; i++)
        {
            Assert.Equal(0, response[i]);
        }
    }

    [Fact]
    public void HostsFileManager_SyncAndClean_PreservesOriginalContent()
    {
        var manager = new HostsFileManager(NullLogger.Instance, _tempHostsFile);

        var list = new List<BlockedDomain>
        {
            new() { Domain = "reddit.com" },
            new() { Domain = "tiktok.com" }
        };

        // Sync
        manager.SyncHostsFile(list);

        string updated = File.ReadAllText(_tempHostsFile);
        Assert.Contains("127.0.0.1 localhost", updated);
        Assert.Contains("# BEGIN DESKTOP_NET_BLOCKER", updated);
        Assert.Contains("0.0.0.0 reddit.com", updated);
        Assert.Contains(":: reddit.com", updated);
        Assert.Contains("0.0.0.0 tiktok.com", updated);
        Assert.Contains("# END DESKTOP_NET_BLOCKER", updated);

        // Remove managed entries
        manager.RemoveManagedEntries();

        string cleaned = File.ReadAllText(_tempHostsFile);
        Assert.Contains("127.0.0.1 localhost", cleaned);
        Assert.DoesNotContain("# BEGIN DESKTOP_NET_BLOCKER", cleaned);
        Assert.DoesNotContain("reddit.com", cleaned);
    }
}

