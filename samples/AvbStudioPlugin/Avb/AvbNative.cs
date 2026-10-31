using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AvbStudioPlugin;

namespace AvbStudioPlugin.Avb;

public sealed class AvbDescriptor
{
    public ulong Tag { get; init; }
    public string Kind { get; init; } = "";
    public string Partition { get; init; } = "";
    public uint RollbackLocation { get; init; }
    public byte[] Raw { get; init; } = Array.Empty<byte>();
    public string Detail { get; init; } = "";
}

public sealed class AvbInfo
{
    public string Path { get; init; } = "";
    public long FileSize { get; init; }
    public bool Parsed { get; init; }
    public bool Unsigned { get; init; }
    public bool IsFooter { get; init; }
    public bool IsVbmeta => !IsFooter && !Unsigned;
    public bool IsHashtree { get; init; }
    public string Algorithm { get; init; } = "SHA256_RSA4096";
    public ulong RollbackIndex { get; init; }
    public uint RollbackLocation { get; init; }
    public uint Flags { get; init; }
    public string PartitionName { get; init; } = "";
    public long OriginalImageSize { get; init; }
    public long VbmetaOffset { get; init; }
    public long VbmetaSize { get; init; }
    public byte[] Salt { get; init; } = Array.Empty<byte>();
    public byte[] Vbmeta { get; init; } = Array.Empty<byte>();
    public List<AvbDescriptor> Descriptors { get; init; } = new();
    public List<KeyValuePair<string, string>> Props { get; init; } = new();
    public bool SignatureValid { get; init; }
    public string VerifyNote { get; init; } = "";
    public string Error { get; init; } = "";

    public bool CanSignFooter => Parsed && (Unsigned || IsFooter);
}

public sealed class SignOptions
{
    public string KeyPem { get; set; } = "";
    public string Algorithm { get; set; } = "SHA256_RSA4096";
    public string PartitionName { get; set; } = "";
    public ulong RollbackIndex { get; set; }
    public uint RollbackLocation { get; set; }
    public long PartitionSize { get; set; }
    public byte[]? Salt { get; set; }
    public bool RegenerateSalt { get; set; }
    public List<KeyValuePair<string, string>>? Props { get; set; }
    /// <summary>未签名镜像是否创建 hashtree footer（system/vendor 等 dm-verity 分区）。</summary>
    public bool UseHashtree { get; set; }
}

public sealed class SignResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public AvbInfo? Info { get; init; }
}


public sealed class RebuildOptions
{
    public string InputDir { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public bool ChainedMode { get; set; }
    public bool RegenerateSalt { get; set; }
    public bool VerifyOnly { get; set; }
    public string Partitions { get; set; } = "";
    public bool UseAospKey { get; set; } = true;
    public string? KeyPem { get; set; }
    public Action<string>? Log { get; set; }
}

public sealed class RebuildResult
{
    public bool Ok { get; init; }
    public int Rebuilt { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public string Message { get; init; } = "";
}

public static class AvbNative
{
    public const int HeaderSize = 256;
    public const int FooterSize = 64;
    public const ulong TagProperty = 0;
    public const ulong TagHashtree = 1;
    public const ulong TagHash = 2;
    public const ulong TagKernelCmdline = 3;
    public const ulong TagChain = 4;

    public static string AlgorithmFromType(uint type) => type switch
    {
        1 => "SHA256_RSA2048",
        2 => "SHA256_RSA4096",
        3 => "SHA256_RSA8192",
        4 => "SHA512_RSA2048",
        5 => "SHA512_RSA4096",
        6 => "SHA512_RSA8192",
        _ => "NONE",
    };

    public static uint TypeFromAlgorithm(string algorithm) => (algorithm ?? "").Trim().ToUpperInvariant() switch
    {
        "SHA256_RSA2048" => 1,
        "SHA256_RSA4096" => 2,
        "SHA256_RSA8192" => 3,
        "SHA512_RSA2048" => 4,
        "SHA512_RSA4096" => 5,
        "SHA512_RSA8192" => 6,
        _ => 0,
    };

    public static bool IsSupportedAlgorithm(string algorithm) => TypeFromAlgorithm(algorithm) != 0;

    public static string HashName(string algorithm)
        => (algorithm ?? "").Contains("512", StringComparison.Ordinal) ? "SHA512" : "SHA256";

    private static int HashSize(string algorithm) => HashName(algorithm) == "SHA512" ? 64 : 32;

    private static int KeyBitsFromAlgorithm(string algorithm)
    {
        if ((algorithm ?? "").Contains("8192", StringComparison.Ordinal)) return 8192;
        if ((algorithm ?? "").Contains("2048", StringComparison.Ordinal)) return 2048;
        return 4096;
    }

    // ------------------------------------------------------------------ inspect

    public static AvbInfo Inspect(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AvbInfo { Path = path, Parsed = false, Error = "文件不存在" };

            using var fs = File.OpenRead(path);
            var length = fs.Length;
            var first = ReadAt(fs, 0, 4);
            if (first.SequenceEqual(Encoding.ASCII.GetBytes("AVB0")))
            {
                var vb = ReadVbmeta(fs, 0);
                return ParseVbmeta(path, length, vb, 0, false, length, 0);
            }

            if (length >= FooterSize)
            {
                var footer = ReadAt(fs, length - FooterSize, FooterSize);
                if (footer.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes("AVBf")))
                {
                    var original = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(12));
                    var vbOff = BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(20));
                    if (vbOff < (ulong)length)
                    {
                        var vb = ReadVbmeta(fs, (long)vbOff);
                        return ParseVbmeta(path, length, vb, (long)original, true, (long)vbOff, FooterSize);
                    }
                }
            }

            return new AvbInfo
            {
                Path = path,
                Parsed = true,
                Unsigned = true,
                FileSize = length,
                PartitionName = GuessPartition(path),
                Algorithm = "SHA256_RSA4096",
            };
        }
        catch (Exception ex)
        {
            return new AvbInfo { Path = path, Parsed = false, Error = ex.Message };
        }
    }

    private static byte[] ReadVbmeta(FileStream fs, long offset)
    {
        var header = ReadAt(fs, offset, HeaderSize);
        if (!header.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes("AVB0")))
            throw new InvalidDataException("不是 AVB 镜像");
        var authSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12));
        var auxSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(20));
        var total = HeaderSize + (long)authSize + (long)auxSize;
        if (total < HeaderSize || total > 64L * 1024 * 1024)
            throw new InvalidDataException("AVB 头大小异常");
        if (offset + total > fs.Length)
            throw new InvalidDataException("AVB 数据不完整");
        var all = ReadAt(fs, offset, (int)total);
        return all;
    }

    private static AvbInfo ParseVbmeta(string path, long fileSize, byte[] vb, long originalSize,
        bool hasFooter, long vbmetaOffset, long footerSize)
    {
        var header = vb.AsSpan(0, HeaderSize);
        var authSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[12..]);
        var auxSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[20..]);
        var algType = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        var algorithm = AlgorithmFromType(algType);
        var auth = vb.AsSpan(HeaderSize, authSize).ToArray();
        var aux = vb.AsSpan(HeaderSize + authSize, auxSize).ToArray();
        var rollback = BinaryPrimitives.ReadUInt64BigEndian(header[112..]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header[120..]);
        var rollbackLoc = BinaryPrimitives.ReadUInt32BigEndian(header[124..]);
        var descOff = (int)BinaryPrimitives.ReadUInt64BigEndian(header[96..]);
        var descSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[104..]);

        var descriptors = ParseDescriptors(aux, descOff, descSize);
        var props = new List<KeyValuePair<string, string>>();
        foreach (var d in descriptors.Where(d => d.Tag == TagProperty))
        {
            var body = d.Raw.AsSpan(16);
            if (body.Length < 16) continue;
            var keySize = (int)BinaryPrimitives.ReadUInt64BigEndian(body);
            var valueSize = (int)BinaryPrimitives.ReadUInt64BigEndian(body[8..]);
            if (keySize < 0 || 16 + keySize > body.Length) continue;
            var key = Encoding.UTF8.GetString(body.Slice(16, keySize));
            var valueStart = 16 + keySize + 1;
            var value = valueStart <= body.Length
                ? Encoding.UTF8.GetString(body.Slice(valueStart, Math.Min(valueSize, body.Length - valueStart)))
                : "";
            props.Add(new(key, value));
        }

        var hashtree = descriptors.FirstOrDefault(d => d.Tag == TagHashtree);
        var hash = descriptors.FirstOrDefault(d => d.Tag == TagHash);
        var partition = hashtree?.Partition ?? hash?.Partition ?? GuessPartition(path);
        var salt = Array.Empty<byte>();
        if (hashtree != null) salt = ExtractSalt(aux, hashtree, true);
        else if (hash != null) salt = ExtractSalt(aux, hash, false);

        var verifyOk = false;
        var note = "未校验";
        if (AlgorithmFromType(algType) != "NONE")
        {
            verifyOk = VerifyVbmeta(vb, out note);
        }
        else note = "无签名";

        return new AvbInfo
        {
            Path = path,
            FileSize = fileSize,
            Parsed = true,
            Unsigned = false,
            IsFooter = hasFooter,
            IsHashtree = hashtree != null,
            Algorithm = algorithm,
            RollbackIndex = rollback,
            RollbackLocation = rollbackLoc,
            Flags = flags,
            PartitionName = partition,
            OriginalImageSize = originalSize,
            VbmetaOffset = vbmetaOffset,
            VbmetaSize = vb.Length,
            Salt = salt,
            Vbmeta = vb,
            Descriptors = descriptors,
            Props = props,
            SignatureValid = verifyOk,
            VerifyNote = note,
            Error = flags == uint.MaxValue ? "" : "",
        };
    }

    private static List<AvbDescriptor> ParseDescriptors(byte[] aux, int offset, int size)
    {
        var list = new List<AvbDescriptor>();
        var end = Math.Min(aux.Length, offset + size);
        var p = offset;
        while (p + 16 <= end)
        {
            var tag = BinaryPrimitives.ReadUInt64BigEndian(aux.AsSpan(p));
            var n = (int)BinaryPrimitives.ReadUInt64BigEndian(aux.AsSpan(p + 8));
            if (n < 0 || p + 16 + n > end) break;
            var body = aux.AsSpan(p + 16, n).ToArray();
            var raw = aux.AsSpan(p, 16 + n).ToArray();
            string kind = tag switch
            {
                TagProperty => "Property",
                TagHashtree => "Hashtree",
                TagHash => "Hash",
                TagChain => "Chain",
                TagKernelCmdline => "KernelCmdline",
                _ => "Tag " + tag,
            };
            var d = new AvbDescriptor { Tag = tag, Kind = kind, Raw = raw };
            if (tag == TagHash && body.Length >= 144)
            {
                var imageSize = BinaryPrimitives.ReadUInt64BigEndian(body);
                var alg = CStr(body.AsSpan(8, 32));
                var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(40));
                int saltLen, digestLen, flags, saltOff;
                string name;
                if (nameLen > 0 && nameLen <= 64 && 116 + nameLen <= body.Length)
                {
                    // avbtool 1.3 / libavb 1.3 可变长分区名布局
                    name = CStr(body.AsSpan(116, nameLen));
                    saltLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(44));
                    digestLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(48));
                    flags = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(52));
                    saltOff = 116 + nameLen;
                }
                else
                {
                    // 旧固定 32 字节分区名布局
                    name = CStr(body.AsSpan(40, 32));
                    saltLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(72));
                    digestLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(76));
                    flags = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(80));
                    saltOff = 144;
                }
                var salt = body.AsSpan(saltOff, Math.Min(saltLen, Math.Max(0, body.Length - saltOff))).ToArray();
                d = new AvbDescriptor
                {
                    Tag = tag, Kind = kind, Partition = name, Raw = raw,
                    Detail = $"image={imageSize} alg={alg} flags={flags} salt={salt.Length}B",
                };
            }
            else if (tag == TagHashtree && body.Length >= 164)
            {
                var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(88));
                var saltLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(92));
                var rootLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(96));
                var name = nameLen > 0 && 164 + nameLen <= body.Length
                    ? CStr(body.AsSpan(164, nameLen)) : "";
                d = new AvbDescriptor
                {
                    Tag = tag, Kind = kind, Partition = name, Raw = raw,
                    Detail = $"tree_offset={BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(12))} " +
                             $"tree_size={BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(20))} " +
                             $"salt={saltLen}B root={rootLen}B",
                };
            }
            else if (tag == TagChain && body.Length >= 40)
            {
                d = new AvbDescriptor
                {
                    Tag = tag, Kind = kind,
                    Partition = CStr(body.AsSpan(4, 32)),
                    RollbackLocation = BinaryPrimitives.ReadUInt32BigEndian(body),
                    Raw = raw,
                    Detail = "rollback_location=" + BinaryPrimitives.ReadUInt32BigEndian(body),
                };
            }
            else if (tag == TagProperty)
            {
                var text = Encoding.UTF8.GetString(body).TrimEnd('\0');
                d = new AvbDescriptor { Tag = tag, Kind = kind, Raw = raw, Detail = text.Replace('\0', ':') };
            }
            list.Add(d);
            p += 16 + n;
        }
        return list;
    }

    private static byte[] ExtractSalt(byte[] aux, AvbDescriptor d, bool hashtree)
    {
        // 从 raw descriptor body 重新取 salt：raw 前 16 字节是 tag/len
        var body = d.Raw.AsSpan(16).ToArray();
        if (!hashtree)
        {
            if (body.Length < 144) return Array.Empty<byte>();
            var nameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(40));
            if (nameLen > 0 && nameLen <= 64 && 116 + nameLen <= body.Length)
            {
                var saltLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(44));
                var off = 116 + nameLen;
                return body.AsSpan(off, Math.Min(saltLen, Math.Max(0, body.Length - off))).ToArray();
            }
            var oldLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(72));
            return body.AsSpan(144, Math.Min(oldLen, Math.Max(0, body.Length - 144))).ToArray();
        }
        if (body.Length < 164) return Array.Empty<byte>();
        var hNameLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(88));
        var hSaltLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(92));
        var hOff = 164 + Math.Max(0, hNameLen);
        return body.AsSpan(hOff, Math.Min(hSaltLen, Math.Max(0, body.Length - hOff))).ToArray();
    }

    private static string CStr(ReadOnlySpan<byte> b)
    {
        var i = b.IndexOf((byte)0);
        if (i < 0) i = b.Length;
        return Encoding.UTF8.GetString(b[..i]);
    }

    private static string GuessPartition(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith("_a", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("_b", StringComparison.OrdinalIgnoreCase))
            name = name[..^2];
        return name;
    }

    private static byte[] ReadAt(FileStream fs, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > fs.Length)
            throw new InvalidDataException("读取越界");
        var buf = new byte[count];
        fs.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    // ------------------------------------------------------------------ verify

    private static bool VerifyVbmeta(byte[] vb, out string note)
    {
        try
        {
            var header = vb.AsSpan(0, HeaderSize);
            var authSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[12..]);
            var auxSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[20..]);
            var algType = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
            var algorithm = AlgorithmFromType(algType);
            var hashOff = (int)BinaryPrimitives.ReadUInt64BigEndian(header[32..]);
            var hashSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[40..]);
            var sigOff = (int)BinaryPrimitives.ReadUInt64BigEndian(header[48..]);
            var sigSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[56..]);
            var pubOff = (int)BinaryPrimitives.ReadUInt64BigEndian(header[64..]);
            var pubSize = (int)BinaryPrimitives.ReadUInt64BigEndian(header[72..]);
            var aux = vb.AsSpan(HeaderSize + authSize, auxSize);
            var auth = vb.AsSpan(HeaderSize, authSize);
            if (pubOff < 0 || pubOff + pubSize > aux.Length || hashOff + hashSize > auth.Length ||
                sigOff + sigSize > auth.Length)
            {
                note = "字段越界";
                return false;
            }

            var hash = Hash(algorithm, header.ToArray().Concat(aux.ToArray()).ToArray());
            if (hash.Length < hashSize || !hash.AsSpan(0, hashSize).SequenceEqual(auth.Slice(hashOff, hashSize)))
            {
                note = "哈希不匹配";
                return false;
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(ParsePublicKey(aux.Slice(pubOff, pubSize).ToArray()));
            var ok = rsa.VerifyHash(hash, auth.Slice(sigOff, sigSize),
                HashName(algorithm) == "SHA512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            note = ok ? "签名有效" : "签名不匹配";
            return ok;
        }
        catch (Exception ex)
        {
            note = "校验异常：" + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ sign

    public static SignResult SignImage(string source, string output, SignOptions options)
    {
        try
        {
            if (!File.Exists(source)) return new SignResult { Ok = false, Message = "源文件不存在" };
            if (string.IsNullOrWhiteSpace(options.KeyPem) || !File.Exists(options.KeyPem))
                return new SignResult { Ok = false, Message = "找不到私钥" };
            var algorithm = NormalizeAlgorithm(options.Algorithm);
            if (!IsSupportedAlgorithm(algorithm))
                return new SignResult { Ok = false, Message = "不支持的算法：" + options.Algorithm };

            var info = Inspect(source);
            if (!info.Parsed) return new SignResult { Ok = false, Message = info.Error };
            if (!info.Unsigned && !info.IsFooter && !info.IsVbmeta)
                return new SignResult { Ok = false, Message = "无法识别的镜像" };

            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(options.KeyPem));
            ValidateKey(rsa, algorithm);

            if (info.IsVbmeta)
            {
                var vbDescriptors = info.Descriptors
                    .SelectMany(d => d.Raw).ToArray();
                var vb = BuildVbmeta(vbDescriptors, algorithm, rsa, options.RollbackIndex,
                    options.RollbackLocation);
                File.WriteAllBytes(output, vb);
                return new SignResult { Ok = true, Message = "VBMeta 已重新签名", Info = Inspect(output) };
            }

            var bytes = File.ReadAllBytes(source);
            var partition = string.IsNullOrWhiteSpace(options.PartitionName)
                ? (string.IsNullOrWhiteSpace(info.PartitionName) ? GuessPartition(source) : info.PartitionName)
                : options.PartitionName.Trim();

            byte[] descriptors;
            byte[] prefix;
            int footerOriginal;
            if (info.Unsigned && options.UseHashtree)
            {
                var imageSize = (int)(((long)bytes.Length + 4095) & ~4095L);
                var salt = options.Salt ?? RandomBytes(32);
                var (root, tree) = BuildHashTree(bytes, imageSize, 4096, algorithm, salt);
                prefix = new byte[imageSize + tree.Length];
                bytes.CopyTo(prefix, 0);
                tree.CopyTo(prefix, imageSize);
                descriptors = BuildHashtreeDescriptor(partition, imageSize, imageSize, tree.Length,
                    algorithm, salt, root, 0);
                footerOriginal = bytes.Length;
            }
            else
            {
                // hashtree 的 tree 位于 original_image_size 之后、vbmeta 之前，必须一起保留
                var dataLen = info.Unsigned ? bytes.Length
                    : info.IsHashtree && info.VbmetaOffset > 0 ? (int)Math.Min(bytes.Length, info.VbmetaOffset)
                    : (int)Math.Min(bytes.Length, info.OriginalImageSize);
                if (dataLen <= 0) dataLen = bytes.Length;
                footerOriginal = info.IsHashtree && !info.Unsigned && info.OriginalImageSize > 0
                    ? (int)Math.Min(dataLen, info.OriginalImageSize)
                    : dataLen;
                prefix = bytes.AsSpan(0, dataLen).ToArray();

                if (info.IsHashtree && !info.Unsigned)
                {
                    descriptors = info.Descriptors
                        .Where(d => d.Tag is TagHash or TagHashtree)
                        .SelectMany(d => d.Raw).ToArray();
                }
                else
                {
                    var salt = options.RegenerateSalt ? RandomBytes(32)
                        : options.Salt ?? (info.Salt.Length > 0 ? info.Salt : RandomBytes(32));
                    var digest = HashWithSalt(algorithm, salt, prefix);
                    descriptors = BuildHashDescriptor(partition, prefix.Length, algorithm, salt, digest, 0)
                        .Concat(BuildPropertyDescriptors(options.Props)).ToArray();
                }
            }

            var vbmeta = BuildVbmeta(descriptors, algorithm, rsa, options.RollbackIndex,
                options.RollbackLocation);
            var footer = BuildFooter(footerOriginal, prefix.Length, vbmeta.Length);
            var end = prefix.Length + vbmeta.Length + FooterSize;
            var total = options.PartitionSize >= end ? options.PartitionSize
                : (!info.Unsigned && info.FileSize >= end ? info.FileSize : end);
            var outBytes = new byte[total];
            prefix.CopyTo(outBytes, 0);
            vbmeta.CopyTo(outBytes, prefix.Length);
            footer.CopyTo(outBytes, total - FooterSize);
            File.WriteAllBytes(output, outBytes);
            return new SignResult { Ok = true, Message = "签名完成", Info = Inspect(output) };
        }
        catch (Exception ex)
        {
            return new SignResult { Ok = false, Message = ex.Message };
        }
    }

    public static byte[] BuildVbmetaImage(IEnumerable<byte[]> descriptorSets, string algorithm,
        string keyPem, ulong rollback, uint rollbackLocation, uint flags = 0)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(keyPem));
        ValidateKey(rsa, NormalizeAlgorithm(algorithm));
        var descriptors = OrderRawDescriptors(descriptorSets);
        return BuildVbmeta(descriptors, NormalizeAlgorithm(algorithm), rsa, rollback, rollbackLocation, flags);
    }

    public static byte[] BuildChainDescriptor(string partition, uint rollbackLocation, byte[] publicKey)
    {
        var body = new byte[40 + publicKey.Length];
        BinaryPrimitives.WriteUInt32BigEndian(body, rollbackLocation);
        WriteCStr(body.AsSpan(4, 32), partition);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(36), (uint)publicKey.Length);
        publicKey.CopyTo(body, 40);
        var raw = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(raw, TagChain);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(8), (ulong)body.Length);
        body.CopyTo(raw, 16);
        return raw;
    }

    private static byte[] BuildVbmeta(byte[] descriptors, string algorithm, RSA rsa,
        ulong rollback, uint rollbackLocation, uint flags = 0)
    {
        var type = TypeFromAlgorithm(algorithm);
        var hashName = HashName(algorithm);
        var hashSize = HashSize(algorithm);
        var keyBytes = rsa.KeySize / 8;
        var pub = BuildPublicKey(rsa);
        // avbtool 的顺序：descriptors 在 aux 块开头，公钥跟在后面；auth/aux 都补到 64 字节
        var auxSize = Align64(descriptors.Length + pub.Length);
        var aux = new byte[auxSize];
        descriptors.CopyTo(aux, 0);
        pub.CopyTo(aux, descriptors.Length);
        var header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("AVB0").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0);
        var authSize = Align64(hashSize + keyBytes);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12), (ulong)authSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(20), (ulong)aux.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), type);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(32), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(40), (ulong)hashSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(48), (ulong)hashSize);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(56), (ulong)keyBytes);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), (ulong)descriptors.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(72), (ulong)pub.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(80), (ulong)(descriptors.Length + pub.Length));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(88), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(96), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(104), (ulong)descriptors.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(112), rollback);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(120), flags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(124), rollbackLocation);
        WriteCStr(header.AsSpan(128, 48), "avbtool 1.3.0");

        var signed = new byte[header.Length + aux.Length];
        header.CopyTo(signed, 0);
        aux.CopyTo(signed, header.Length);
        var hash = Hash(algorithm, signed);
        var signature = rsa.SignHash(hash,
            hashName == "SHA512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var auth = new byte[authSize];
        hash.CopyTo(auth, 0);
        signature.CopyTo(auth, hash.Length);
        var result = new byte[header.Length + auth.Length + aux.Length];
        header.CopyTo(result, 0);
        auth.CopyTo(result, header.Length);
        aux.CopyTo(result, header.Length + auth.Length);
        return result;
    }

    private static byte[] BuildHashDescriptor(string partition, long imageSize, string algorithm,
        byte[] salt, byte[] digest, uint flags)
    {
        var name = Encoding.UTF8.GetBytes(partition);
        var payload = 116 + name.Length + salt.Length + digest.Length;
        var nbf = (payload + 7) & ~7;
        var body = new byte[nbf];
        BinaryPrimitives.WriteUInt64BigEndian(body, (ulong)imageSize);
        WriteCStr(body.AsSpan(8, 32), HashName(algorithm).ToLowerInvariant());
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(40), (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(44), (uint)salt.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(48), (uint)digest.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(52), flags);
        name.CopyTo(body, 116);
        salt.CopyTo(body, 116 + name.Length);
        digest.CopyTo(body, 116 + name.Length + salt.Length);
        var raw = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(raw, TagHash);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(8), (ulong)nbf);
        body.CopyTo(raw, 16);
        return raw;
    }


    private static byte[] BuildHashtreeDescriptor(string partition, long imageSize, long treeOffset,
        long treeSize, string algorithm, byte[] salt, byte[] rootDigest, uint flags)
    {
        var name = Encoding.UTF8.GetBytes(partition);
        var payload = 164 + name.Length + salt.Length + rootDigest.Length;
        var nbf = (payload + 7) & ~7;
        var body = new byte[nbf];
        BinaryPrimitives.WriteUInt32BigEndian(body, 1); // dm-verity version
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(4), (ulong)imageSize);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(12), (ulong)treeOffset);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(20), (ulong)treeSize);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(28), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(32), 4096);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(36), 0);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(40), 0);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(48), 0);
        WriteCStr(body.AsSpan(56, 32), HashName(algorithm).ToLowerInvariant());
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(88), (uint)name.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(92), (uint)salt.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(96), (uint)rootDigest.Length);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(100), flags);
        name.CopyTo(body, 164);
        salt.CopyTo(body, 164 + name.Length);
        rootDigest.CopyTo(body, 164 + name.Length + salt.Length);
        var raw = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(raw, TagHashtree);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(8), (ulong)nbf);
        body.CopyTo(raw, 16);
        return raw;
    }

    private static (byte[] Root, byte[] Tree) BuildHashTree(byte[] data, int imageSize, int blockSize,
        string algorithm, byte[] salt)
    {
        var digestSize = HashSize(algorithm);
        var padded = new byte[imageSize];
        data.AsSpan(0, Math.Min(data.Length, imageSize)).CopyTo(padded);
        var (offsets, treeSize) = CalcHashLevelOffsets(imageSize, blockSize, digestSize);
        var tree = new byte[treeSize];

        if (imageSize <= blockSize)
        {
            var one = new byte[blockSize];
            padded.AsSpan(0, Math.Min(padded.Length, blockSize)).CopyTo(one);
            return (HashWithSalt(algorithm, salt, one), tree);
        }

        var source = padded;
        var sourceSize = imageSize;
        var level = 0;
        byte[] lastLevel = Array.Empty<byte>();
        while (sourceSize > blockSize)
        {
            var hashes = new MemoryStream();
            var remaining = sourceSize;
            var pos = 0;
            while (remaining > 0)
            {
                var block = new byte[blockSize];
                var take = Math.Min(blockSize, remaining);
                Array.Copy(source, pos, block, 0, take);
                var digest = HashWithSalt(algorithm, salt, block);
                hashes.Write(digest, 0, digest.Length);
                pos += take;
                remaining -= take;
            }

            var raw = hashes.ToArray();
            var levelSize = (raw.Length + blockSize - 1) & ~(blockSize - 1);
            var levelBytes = new byte[levelSize];
            raw.CopyTo(levelBytes, 0);
            Array.Copy(levelBytes, 0, tree, offsets[level], levelSize);
            lastLevel = levelBytes;
            source = levelBytes;
            sourceSize = levelSize;
            level++;
        }

        var root = HashWithSalt(algorithm, salt, lastLevel);
        return (root, tree);
    }

    private static (int[] Offsets, int TreeSize) CalcHashLevelOffsets(int imageSize, int blockSize,
        int digestSize)
    {
        var levelSizes = new List<int>();
        var size = imageSize;
        while (size > blockSize)
        {
            var blocks = (size + blockSize - 1) / blockSize;
            var level = (blocks * digestSize + blockSize - 1) & ~(blockSize - 1);
            levelSizes.Add(level);
            size = level;
        }

        var offsets = new int[levelSizes.Count];
        for (var n = 0; n < offsets.Length; n++)
        {
            var offset = 0;
            for (var m = n + 1; m < levelSizes.Count; m++) offset += levelSizes[m];
            offsets[n] = offset;
        }
        return (offsets, levelSizes.Sum());
    }

    private static byte[] BuildUnsignedVbmeta(byte[] descriptors, ulong rollback, uint rollbackLocation, uint flags)
    {
        var auxSize = Align64(descriptors.Length);
        var aux = new byte[auxSize];
        descriptors.CopyTo(aux, 0);
        var header = new byte[HeaderSize];
        Encoding.ASCII.GetBytes("AVB0").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(20), (ulong)auxSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), 0);
        // avbtool 即使没有公钥，也把 pubkey / metadata 的 offset 指向 descriptors 之后（size=0）
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), (ulong)descriptors.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(72), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(80), (ulong)descriptors.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(88), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(96), 0);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(104), (ulong)descriptors.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(112), rollback);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(120), flags);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(124), rollbackLocation);
        WriteCStr(header.AsSpan(128, 48), "avbtool 1.3.0");
        var result = new byte[HeaderSize + auxSize];
        header.CopyTo(result, 0);
        aux.CopyTo(result, HeaderSize);
        return result;
    }

    private static byte[] BuildPropertyDescriptor(string key, string value)
    {
        var k = Encoding.UTF8.GetBytes(key ?? "");
        var v = Encoding.UTF8.GetBytes(value ?? "");
        var payload = 16 + k.Length + 1 + v.Length + 1;
        var nbf = (payload + 7) & ~7;
        var raw = new byte[16 + nbf];
        BinaryPrimitives.WriteUInt64BigEndian(raw, TagProperty);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(8), (ulong)nbf);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(16), (ulong)k.Length);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(24), (ulong)v.Length);
        k.CopyTo(raw, 32);
        v.CopyTo(raw, 32 + k.Length + 1);
        return raw;
    }

    private static byte[] BuildPropertyDescriptors(IEnumerable<KeyValuePair<string, string>>? props)
    {
        if (props == null) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        foreach (var p in props)
        {
            if (string.IsNullOrEmpty(p.Key)) continue;
            var b = BuildPropertyDescriptor(p.Key, p.Value);
            ms.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }

    /// <summary>非链式模式：给普通分区重新写入“无签名”的 hash footer（等价 avbtool add_hash_footer --algorithm NONE）。</summary>
    public static SignResult RewriteUnsignedFooter(string source, string output, string partition,
        byte[]? salt, List<KeyValuePair<string, string>>? props)
    {
        try
        {
            var info = Inspect(source);
            if (!info.Parsed || !info.IsFooter)
                return new SignResult { Ok = false, Message = "不是带 footer 的分区镜像" };
            var bytes = File.ReadAllBytes(source);
            var dataLen = info.IsHashtree && info.VbmetaOffset > 0
                ? (int)Math.Min(bytes.Length, info.VbmetaOffset)
                : (int)Math.Min(bytes.Length, info.OriginalImageSize);
            if (info.IsHashtree && info.OriginalImageSize > 0) dataLen = (int)info.OriginalImageSize;
            var prefix = bytes.AsSpan(0, dataLen).ToArray();
            salt ??= info.Salt.Length > 0 ? info.Salt : RandomBytes(32);
            var digest = HashWithSalt("SHA256", salt, prefix);
            var desc = BuildHashDescriptor(partition, prefix.Length, "SHA256", salt, digest, 0)
                .Concat(BuildPropertyDescriptors(props)).ToArray();
            // 原脚本 add_hash_footer --algorithm NONE 不传 rollback/flags，avbtool 默认 0
            var vb = BuildUnsignedVbmeta(desc, 0, 0, 0);
            var footer = BuildFooter(info.OriginalImageSize > 0 ? (int)info.OriginalImageSize : prefix.Length,
                prefix.Length, vb.Length);
            var end = prefix.Length + vb.Length + FooterSize;
            var total = info.FileSize >= end ? (int)info.FileSize : end;
            var outBytes = new byte[total];
            prefix.CopyTo(outBytes, 0);
            vb.CopyTo(outBytes, prefix.Length);
            footer.CopyTo(outBytes, total - FooterSize);
            File.WriteAllBytes(output, outBytes);
            return new SignResult { Ok = true, Message = "已写入无签名 hash footer", Info = Inspect(output) };
        }
        catch (Exception ex)
        {
            return new SignResult { Ok = false, Message = ex.Message };
        }
    }


    private static byte[] BuildFooter(int originalImageSize, int vbmetaOffset, int vbmetaSize)
    {
        var footer = new byte[FooterSize];
        Encoding.ASCII.GetBytes("AVBf").CopyTo(footer, 0);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(12), (ulong)originalImageSize);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(20), (ulong)vbmetaOffset);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(28), (ulong)vbmetaSize);
        return footer;
    }

    public static string NormalizeAlgorithm(string algorithm)
    {
        var a = (algorithm ?? "").Trim().ToUpperInvariant();
        return IsSupportedAlgorithm(a) ? a : "SHA256_RSA4096";
    }

    private static void ValidateKey(RSA rsa, string algorithm)
    {
        var expected = KeyBitsFromAlgorithm(algorithm);
        if (rsa.KeySize != expected)
            throw new InvalidOperationException($"私钥是 RSA-{rsa.KeySize}，算法需要 RSA-{expected}");
    }

    private static byte[] BuildPublicKey(RSA rsa)
    {
        var p = rsa.ExportParameters(false);
        var modulus = TrimLeadingZeros(p.Modulus!);
        var bits = rsa.KeySize;
        var bytes = bits / 8;
        if (modulus.Length < bytes)
            modulus = new byte[bytes - modulus.Length].Concat(modulus).ToArray();
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: true);
        var mod32 = BigInteger.One << 32;
        // 2^32 不是素数，不能用费马小定理；φ(2^32)=2^31，逆元指数为 2^31-1
        var inv = BigInteger.ModPow(n, (BigInteger.One << 31) - 1, mod32);
        var n0inv = (uint)(mod32 - inv);
        var rr = BigInteger.ModPow(new BigInteger(2), bits * 2, n);
        var rrBytes = rr.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (rrBytes.Length < bytes) rrBytes = new byte[bytes - rrBytes.Length].Concat(rrBytes).ToArray();
        if (rrBytes.Length > bytes) rrBytes = rrBytes[^bytes..];
        var blob = new byte[8 + bytes * 2];
        BinaryPrimitives.WriteUInt32BigEndian(blob, (uint)bits);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), n0inv);
        modulus.CopyTo(blob, 8);
        rrBytes.CopyTo(blob, 8 + bytes);
        return blob;
    }

    private static RSAParameters ParsePublicKey(byte[] blob)
    {
        var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(blob);
        var bytes = bits / 8;
        if (blob.Length < 8 + bytes * 2) throw new InvalidDataException("公钥长度不足");
        var modulus = blob.AsSpan(8, bytes).ToArray();
        return new RSAParameters { Modulus = modulus, Exponent = new byte[] { 1, 0, 1 } };
    }

    public static byte[] PublicKeyFromPem(string pem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return BuildPublicKey(rsa);
    }

    private static int Align64(int n) => (n + 63) & ~63;

    private static byte[] TrimLeadingZeros(byte[] b)
    {
        var i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }

    private static byte[] Hash(string algorithm, byte[] data)
        => HashName(algorithm) == "SHA512" ? SHA512.HashData(data) : SHA256.HashData(data);

    private static byte[] HashWithSalt(string algorithm, byte[] salt, byte[] data)
    {
        using var h = IncrementalHash.CreateHash(HashName(algorithm) == "SHA512"
            ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256);
        h.AppendData(salt);
        h.AppendData(data);
        return h.GetHashAndReset();
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static void WriteCStr(Span<byte> dst, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var n = Math.Min(bytes.Length, dst.Length - 1);
        bytes.AsSpan(0, n).CopyTo(dst);
        dst[n] = 0;
    }

    // ------------------------------------------------------------------ whole-tree rebuild

    public static RebuildResult RebuildAll(RebuildOptions options)
    {
        var log = options.Log ?? (_ => { });
        try
        {
            var input = options.InputDir;
            var output = options.OutputDir;
            if (string.IsNullOrWhiteSpace(input) || !Directory.Exists(input))
                return new RebuildResult { Ok = false, Message = "输入目录不存在" };
            if (string.IsNullOrWhiteSpace(output))
                return new RebuildResult { Ok = false, Message = "请指定输出目录" };
            Directory.CreateDirectory(output);

            var selected = ParsePartitionList(options.Partitions);
            var files = new List<string>();
            if (selected.Count > 0)
            {
                foreach (var name in selected)
                {
                    var path = Path.Combine(input, name + ".img");
                    if (File.Exists(path)) files.Add(path);
                    else log($"[跳过] 找不到 {name}.img");
                }
            }
            else
            {
                // 与原工具 rebuild_avb.py 的 detect_partition_images 保持一致：
                // 默认只处理 boot / init_boot / vendor_boot；其它分区请显式填分区列表。
                foreach (var common in new[] { "boot", "init_boot", "vendor_boot" })
                {
                    var path = Path.Combine(input, common + ".img");
                    if (File.Exists(path)) files.Add(path);
                }
            }
            if (files.Count == 0)
                return new RebuildResult { Ok = false, Message = "未找到 boot / init_boot / vendor_boot（可在分区列表里显式填写）" };

            var vbmetaPath = Path.Combine(input, "vbmeta.img");
            if (!options.ChainedMode && !options.VerifyOnly && !File.Exists(vbmetaPath))
                return new RebuildResult { Ok = false, Message = "非链式模式需要输入目录里有 vbmeta.img" };

            var backupRoot = string.IsNullOrWhiteSpace(options.BackupDir)
                ? Path.Combine(input, "backup") : options.BackupDir;
            var backupDir = Path.Combine(backupRoot, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            if (!options.VerifyOnly)
            {
                Directory.CreateDirectory(backupDir);
                // 与原工具一致：输入目录下所有 .img 先整体备份（含 vbmeta 和未处理分区）
                foreach (var f in Directory.GetFiles(input, "*.img", SearchOption.TopDirectoryOnly))
                    File.Copy(f, Path.Combine(backupDir, Path.GetFileName(f)), overwrite: true);
            }

            var rebuilt = 0;
            var failed = 0;
            var skipped = 0;
            var regularDescriptors = new List<AvbDescriptor>();

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var info = Inspect(file);
                if (!info.Parsed || info.Unsigned)
                {
                    skipped++;
                    log($"[跳过] {name}：{(!info.Parsed ? info.Error : "未检测到 AVB footer")}");
                    continue;
                }

                var algorithm = info.Algorithm;
                var chained = !string.Equals(algorithm, "NONE", StringComparison.OrdinalIgnoreCase);
                if (options.ChainedMode && !chained)
                {
                    skipped++;
                    log($"[跳过] {name}：非链式分区（算法 NONE）");
                    continue;
                }

                if (options.VerifyOnly)
                {
                    log($"[校验] {name} · {algorithm} · 回滚 {info.RollbackIndex} · {info.VerifyNote}");
                    continue;
                }

                var outPath = Path.Combine(output, name);

                if (chained)
                {
                    var key = ResolveKey(options, algorithm, log);
                    if (key == null)
                    {
                        failed++;
                        log($"[失败] {name}：{algorithm} 没有可用私钥");
                        continue;
                    }
                    var result = SignImage(file, outPath, new SignOptions
                    {
                        KeyPem = key,
                        Algorithm = algorithm,
                        PartitionName = info.PartitionName,
                        RollbackIndex = info.RollbackIndex,
                        RollbackLocation = 0,
                        Salt = options.RegenerateSalt ? null : info.Salt,
                        RegenerateSalt = options.RegenerateSalt,
                        Props = info.Props,
                    });
                    if (!result.Ok)
                    {
                        failed++;
                        log($"[失败] {name}：{result.Message}");
                        continue;
                    }
                    rebuilt++;
                    log($"[完成] {name} → {outPath}（链式 / {algorithm}）");
                    if (info.IsHashtree && options.RegenerateSalt)
                        log($"[提示] {name} 是 hashtree 分区，重新生成 salt 需要重建整棵树；当前保留原树。");
                }
                else
                {
                    // 非链式：普通分区只写无签名 hash footer，由 vbmeta 统一签名
                    var result = RewriteUnsignedFooter(file, outPath, info.PartitionName,
                        options.RegenerateSalt ? null : info.Salt, info.Props);
                    if (!result.Ok)
                    {
                        failed++;
                        log($"[失败] {name}：{result.Message}");
                        continue;
                    }
                    rebuilt++;
                    if (result.Info != null)
                        regularDescriptors.AddRange(result.Info.Descriptors);
                    log($"[完成] {name} → {outPath}（普通 / 无签名 footer）");
                }
            }

            if (options.VerifyOnly)
                return new RebuildResult { Ok = failed == 0, Rebuilt = 0, Failed = failed, Skipped = skipped, Message = "校验完成" };

            if (options.ChainedMode)
            {
                // 与原工具一致：链式模式不生成/不复制 vbmeta.img，原文件保留在输入目录
                log("[完成] 链式模式只重签分区 footer，原 vbmeta.img 保持不变");
                return new RebuildResult
                {
                    Ok = rebuilt > 0 && failed == 0,
                    Rebuilt = rebuilt, Failed = failed, Skipped = skipped,
                    Message = rebuilt > 0 ? "链式重建完成" : "没有分区被重建",
                };
            }

            var vbInfo = Inspect(vbmetaPath);
            if (!vbInfo.Parsed)
                return new RebuildResult { Ok = false, Rebuilt = rebuilt, Failed = failed, Skipped = skipped, Message = "vbmeta.img 解析失败" };

            if (regularDescriptors.Count == 0)
            {
                // 与原工具一致：没有普通分区时不生成 vbmeta.img
                log("[提示] 没有普通分区，未重建 vbmeta.img");
                return new RebuildResult { Ok = rebuilt > 0 && failed == 0, Rebuilt = rebuilt, Failed = failed, Skipped = skipped, Message = "重建完成" };
            }

            var vbAlgorithm = NormalizeAlgorithm(vbInfo.Algorithm);
            var vbKey = ResolveKey(options, vbAlgorithm, log);
            if (vbKey == null)
                return new RebuildResult { Ok = false, Rebuilt = rebuilt, Failed = failed, Skipped = skipped, Message = "vbmeta 算法没有可用私钥" };

            var descriptorSets = OrderDescriptorsForVbmeta(
                vbInfo.Descriptors.Concat(regularDescriptors));

            var vbBytes = BuildVbmetaImage(descriptorSets, vbAlgorithm, vbKey, vbInfo.RollbackIndex, 0, vbInfo.Flags);
            var padded = new byte[Math.Max(4096, (vbBytes.Length + 4095) & ~4095)];
            vbBytes.CopyTo(padded, 0);
            File.WriteAllBytes(Path.Combine(output, "vbmeta.img"), padded);
            log($"[完成] vbmeta.img（{vbAlgorithm}，{descriptorSets.Count} 个描述符，4096 对齐）");

            return new RebuildResult
            {
                Ok = rebuilt > 0 && failed == 0,
                Rebuilt = rebuilt, Failed = failed, Skipped = skipped,
                Message = rebuilt > 0 ? "重建完成" : "没有分区被重建",
            };
        }
        catch (Exception ex)
        {
            return new RebuildResult { Ok = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// 复刻 avbtool make_vbmeta_image 的 --include_descriptors_from_image 行为：
    /// 无 partition_name 的描述符（Property/KernelCmdline）按出现顺序全部追加；
    /// Hash/Hashtree/Chain 按 type+partition 去重（后出现覆盖），最后按键名排序。
    /// </summary>
    private static List<byte[]> OrderDescriptorsForVbmeta(IEnumerable<AvbDescriptor> descriptors)
    {
        var extras = new List<byte[]>();
        var partitioned = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            if (d.Tag is TagHash or TagHashtree or TagChain)
            {
                var type = d.Tag == TagHash ? "AvbHashDescriptor"
                    : d.Tag == TagHashtree ? "AvbHashtreeDescriptor"
                    : "AvbChainPartitionDescriptor";
                partitioned[type + "_" + d.Partition] = d.Raw;
            }
            else
            {
                extras.Add(d.Raw);
            }
        }
        extras.AddRange(partitioned.Values);
        return extras;
    }


    private static byte[] OrderRawDescriptors(IEnumerable<byte[]> raws)
    {
        var extras = new List<byte[]>();
        var partitioned = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var raw in raws)
        {
            if (raw.Length < 16) continue;
            var tag = BinaryPrimitives.ReadUInt64BigEndian(raw);
            if (tag is TagHash or TagHashtree or TagChain)
            {
                var body = raw.AsSpan(16);
                string name = "";
                if (tag == TagHash && body.Length >= 44)
                {
                    var nameLen = BinaryPrimitives.ReadUInt32BigEndian(body[40..]);
                    name = nameLen > 0 && 116 + nameLen <= body.Length
                        ? CStr(body.Slice(116, (int)nameLen))
                        : CStr(body.Slice(40, 32));
                }
                else if (tag == TagHashtree && body.Length >= 92)
                {
                    var nameLen = BinaryPrimitives.ReadUInt32BigEndian(body[88..]);
                    if (nameLen > 0 && 164 + nameLen <= body.Length)
                        name = CStr(body.Slice(164, (int)nameLen));
                }
                else if (tag == TagChain && body.Length >= 36)
                {
                    name = CStr(body.Slice(4, 32));
                }
                var type = tag == TagHash ? "AvbHashDescriptor"
                    : tag == TagHashtree ? "AvbHashtreeDescriptor" : "AvbChainPartitionDescriptor";
                partitioned[type + "_" + name] = raw;
            }
            else extras.Add(raw);
        }
        extras.AddRange(partitioned.Values);
        return extras.SelectMany(x => x).ToArray();
    }


    private static List<string> ParsePartitionList(string text)
        => (text ?? "").Split(new[] { ' ', ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? ResolveKey(RebuildOptions options, string algorithm, Action<string> log)
    {
        if (!options.UseAospKey)
            return !string.IsNullOrWhiteSpace(options.KeyPem) && File.Exists(options.KeyPem) ? options.KeyPem : null;
        if (!AospKeys.Supports(algorithm))
        {
            log($"[提示] AOSP 测试密钥不支持 {algorithm}，请使用自定义私钥。");
            return null;
        }
        var dir = Path.Combine(Path.GetTempPath(), "DashFileAVB");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "aosp_" + AospKeys.KeyBits(algorithm) + ".pem");
        File.WriteAllText(path, AospKeys.Pem(algorithm), Encoding.ASCII);
        return path;
    }

}