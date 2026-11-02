using System;
using System.IO;
using System.Net;
using System.Text;

namespace DesktopNetBlocker.Core.Network;

public static class DnsMessageParser
{
    public const ushort TypeA = 1;
    public const ushort TypeAAAA = 28;
    public const ushort TypeCNAME = 5;
    public const ushort TypeHTTPS = 65;

    public static bool TryParseQuery(byte[] buffer, int length, out ushort transactionId, out string qname, out ushort qtype)
    {
        transactionId = 0;
        qname = string.Empty;
        qtype = 0;

        if (length < 12)
            return false;

        transactionId = (ushort)((buffer[0] << 8) | buffer[1]);
        ushort flags = (ushort)((buffer[2] << 8) | buffer[3]);
        ushort qdcount = (ushort)((buffer[4] << 8) | buffer[5]);

        // QR bit = 0 (Query), Opcode = 0 (Standard query)
        bool isQuery = (flags & 0x8000) == 0;
        if (!isQuery || qdcount < 1)
            return false;

        int offset = 12;
        var nameBuilder = new StringBuilder();

        while (offset < length)
        {
            byte labelLen = buffer[offset++];
            if (labelLen == 0)
                break;

            if ((labelLen & 0xC0) == 0xC0)
            {
                // DNS compression pointer (rare in questions, but handle gracefully)
                offset++;
                break;
            }

            if (offset + labelLen > length)
                return false;

            if (nameBuilder.Length > 0)
                nameBuilder.Append('.');

            nameBuilder.Append(Encoding.ASCII.GetString(buffer, offset, labelLen));
            offset += labelLen;
        }

        qname = nameBuilder.ToString();

        if (offset + 4 <= length)
        {
            qtype = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            // offset + 2 is qclass
            return true;
        }

        return false;
    }

    /// <summary>
    /// Constructs a blocked DNS answer (0.0.0.0 for A, :: for AAAA, or empty response for other query types like HTTPS/SVCB)
    /// </summary>
    public static byte[] CreateBlockedResponse(byte[] queryBuffer, int queryLength, ushort transactionId, string qname, ushort qtype, uint ttl = 300)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Transaction ID
        writer.Write((byte)(transactionId >> 8));
        writer.Write((byte)(transactionId & 0xFF));

        // Flags: QR=1 (Response), AA=1, RD=1, RA=1, RCODE=0 (NoError)
        ushort flags = 0x8580;
        writer.Write((byte)(flags >> 8));
        writer.Write((byte)(flags & 0xFF));

        // QDCOUNT = 1
        writer.Write((byte)0);
        writer.Write((byte)1);

        // ANCOUNT = 1 for A/AAAA, 0 for others (NODATA)
        bool hasAnswer = qtype is TypeA or TypeAAAA;
        writer.Write((byte)0);
        writer.Write((byte)(hasAnswer ? 1 : 0));

        // NSCOUNT = 0
        writer.Write((byte)0);
        writer.Write((byte)0);

        // ARCOUNT = 0
        writer.Write((byte)0);
        writer.Write((byte)0);

        // Copy Original Question from queryBuffer (from byte 12 to end of question)
        int qStart = 12;
        int qEnd = qStart;
        while (qEnd < queryLength && queryBuffer[qEnd] != 0)
        {
            qEnd += queryBuffer[qEnd] + 1;
        }
        qEnd += 5; // 0x00 + 2 bytes qtype + 2 bytes qclass

        if (qEnd <= queryLength)
        {
            writer.Write(queryBuffer, qStart, qEnd - qStart);
        }
        else
        {
            // Fallback: manually encode qname
            string[] labels = qname.Split('.');
            foreach (var label in labels)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
            }
            writer.Write((byte)0); // End of qname
            writer.Write((byte)(qtype >> 8));
            writer.Write((byte)(qtype & 0xFF));
            writer.Write((byte)0); // IN class
            writer.Write((byte)1);
        }

        // Write Answer Section if A or AAAA
        if (hasAnswer)
        {
            // Name pointer to question at byte 12
            writer.Write((byte)0xC0);
            writer.Write((byte)0x0C);

            // Type
            writer.Write((byte)(qtype >> 8));
            writer.Write((byte)(qtype & 0xFF));

            // Class IN = 1
            writer.Write((byte)0);
            writer.Write((byte)1);

            // TTL (4 bytes)
            writer.Write((byte)(ttl >> 24));
            writer.Write((byte)((ttl >> 16) & 0xFF));
            writer.Write((byte)((ttl >> 8) & 0xFF));
            writer.Write((byte)(ttl & 0xFF));

            if (qtype == TypeA)
            {
                // RDLENGTH = 4
                writer.Write((byte)0);
                writer.Write((byte)4);
                // 0.0.0.0
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((byte)0);
            }
            else if (qtype == TypeAAAA)
            {
                // RDLENGTH = 16
                writer.Write((byte)0);
                writer.Write((byte)16);
                // :: (16 zero bytes)
                for (int i = 0; i < 16; i++)
                {
                    writer.Write((byte)0);
                }
            }
        }

        return ms.ToArray();
    }
}

