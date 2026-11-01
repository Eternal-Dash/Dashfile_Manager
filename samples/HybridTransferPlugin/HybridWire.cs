// Adapted from HybridFileXfer (weixiansen574), GPL-3.0-or-later.
// Uses its 1 MiB multi-channel block model with DashFile RPC/TLS extensions.
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HybridTransferPlugin;

public sealed class HybridWire(Stream stream)
{
    private readonly byte[] _number = new byte[8];
    public async Task WriteByte(byte value, CancellationToken ct) { _number[0] = value; await stream.WriteAsync(_number.AsMemory(0,1), ct); }
    public async Task<byte> ReadByte(CancellationToken ct) { await stream.ReadExactlyAsync(_number.AsMemory(0,1), ct); return _number[0]; }
    public async Task WriteInt(int value, CancellationToken ct) { BinaryPrimitives.WriteInt32BigEndian(_number, value); await stream.WriteAsync(_number.AsMemory(0,4), ct); }
    public async Task<int> ReadInt(CancellationToken ct) { await stream.ReadExactlyAsync(_number.AsMemory(0,4), ct); return BinaryPrimitives.ReadInt32BigEndian(_number); }
    public async Task WriteLong(long value, CancellationToken ct) { BinaryPrimitives.WriteInt64BigEndian(_number, value); await stream.WriteAsync(_number, ct); }
    public async Task<long> ReadLong(CancellationToken ct) { await stream.ReadExactlyAsync(_number, ct); return BinaryPrimitives.ReadInt64BigEndian(_number); }
    public async Task WriteUtf(string value, CancellationToken ct)
    {
        var bytes = EncodeUtf(value);
        BinaryPrimitives.WriteUInt16BigEndian(_number, checked((ushort)bytes.Length));
        await stream.WriteAsync(_number.AsMemory(0,2), ct); await stream.WriteAsync(bytes, ct);
    }
    public async Task<string> ReadUtf(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_number.AsMemory(0,2), ct); var bytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(_number)];
        await stream.ReadExactlyAsync(bytes, ct); return DecodeUtf(bytes);
    }
    public async Task Reply(CancellationToken ct) { if (await ReadByte(ct) != 1) throw new IOException("设备传输服务：" + await ReadUtf(ct)); }
    // Java DataInput/DataOutput use modified UTF-8 (NUL and surrogate code units differ from UTF8Encoding).
    public static byte[] EncodeUtf(string text)
    {
        var bytes = new List<byte>(text.Length * 2);
        foreach (var c in text)
            if (c is >= (char)1 and <= (char)127) bytes.Add((byte)c);
            else if (c <= 0x7ff) { bytes.Add((byte)(0xc0 | (c >> 6))); bytes.Add((byte)(0x80 | (c & 63))); }
            else { bytes.Add((byte)(0xe0 | (c >> 12))); bytes.Add((byte)(0x80 | ((c >> 6) & 63))); bytes.Add((byte)(0x80 | (c & 63))); }
        if (bytes.Count > ushort.MaxValue) throw new InvalidDataException("协议字符串过长。");
        return bytes.ToArray();
    }
    public static string DecodeUtf(ReadOnlySpan<byte> bytes)
    {
        var text = new List<char>(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            var first = bytes[i++];
            if (first < 128) text.Add((char)first);
            else if ((first & 0xe0) == 0xc0)
            {
                if (i >= bytes.Length || (bytes[i] & 0xc0) != 0x80) throw new InvalidDataException("无效 modified UTF-8。");
                text.Add((char)(((first & 31) << 6) | (bytes[i++] & 63)));
            }
            else if ((first & 0xf0) == 0xe0)
            {
                if (i+1 >= bytes.Length || (bytes[i] & 0xc0) != 0x80 || (bytes[i+1] & 0xc0) != 0x80) throw new InvalidDataException("无效 modified UTF-8。");
                text.Add((char)(((first & 15) << 12) | ((bytes[i++] & 63) << 6) | (bytes[i++] & 63)));
            }
            else throw new InvalidDataException("无效 modified UTF-8。");
        }
        return new string(text.ToArray());
    }
}
public sealed record HybridEndpoint(string Name, IPAddress Address, int Port);
public sealed class HybridChannel : IDisposable
{
    private readonly TcpClient _socket;
    public SslStream Stream { get; }
    public HybridWire Wire { get; }
    public string Name { get; }
    public long Bytes;
    private HybridChannel(TcpClient socket, SslStream stream, string name) { _socket=socket;Stream=stream;Wire=new(stream);Name=name; }
    public static async Task<HybridChannel> Open(HybridEndpoint endpoint, string token, byte[] fingerprint, CancellationToken ct)
    {
        var socket = new TcpClient(endpoint.Address.AddressFamily) { NoDelay=true };
        SslStream? tls = null;
        try
        {
            await socket.ConnectAsync(endpoint.Address, endpoint.Port, ct);
            tls = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()),fingerprint));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost="dashfile-hybrid.local",EnabledSslProtocols=SslProtocols.Tls12,CertificateRevocationCheckMode=X509RevocationMode.NoCheck }, ct);
            var channel = new HybridChannel(socket,tls,endpoint.Name);
            await tls.WriteAsync("DFHFX001"u8.ToArray(),ct); await channel.Wire.WriteUtf(token,ct); await channel.Wire.Reply(ct);
            return channel;
        }
        catch { tls?.Dispose();socket.Dispose();throw; }
    }
    public void Dispose() { Stream.Dispose();_socket.Dispose(); }
}
