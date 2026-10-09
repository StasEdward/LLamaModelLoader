using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

/// <summary>Bounded GGUF v2/v3 header reader. Tensor payloads and vocabulary strings are never loaded.</summary>
public static class GgufReader
{
    public static Task<GgufMetadata> ReadAsync(string path, CancellationToken token = default) => Task.Run(() => Read(path, token), token);

    public static GgufMetadata Read(string path, CancellationToken token = default)
    {
        var parts = ModelCatalog.PartPaths(path);
        Dictionary<string, object>? first = null;
        Dictionary<string, long> types = [];
        ulong parameters = 0, tensors = 0;
        long bytes = 0;
        uint version = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var stream = new FileStream(parts[index], FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            bytes = checked(bytes + stream.Length);
            var reader = new HeaderReader(stream, token);
            var current = reader.ReadMetadata(out var count, out var currentVersion);
            first ??= current; version = currentVersion;
            if (current.TryGetValue("split.count", out var splitCount) && Convert.ToUInt64(splitCount, CultureInfo.InvariantCulture) != (ulong)parts.Count)
                throw new InvalidDataException("GGUF split count does not match the available files.");
            if (current.TryGetValue("split.no", out var splitNo) && Convert.ToUInt64(splitNo, CultureInfo.InvariantCulture) != (ulong)index)
                throw new InvalidDataException("GGUF shards are not in the expected order.");
            if (index > 0 && first.GetValueOrDefault("general.architecture")?.ToString() != current.GetValueOrDefault("general.architecture")?.ToString())
                throw new InvalidDataException("GGUF shards have different architectures.");
            tensors = checked(tensors + count);
            for (ulong i = 0; i < count; i++)
            {
                reader.String(false);
                var dimensions = reader.U32();
                if (dimensions is < 1 or > 4) throw new InvalidDataException("Unsupported GGUF tensor dimensions.");
                ulong elements = 1;
                for (var d = 0; d < dimensions; d++) elements = checked(elements * reader.U64());
                parameters = checked(parameters + elements);
                var type = TypeName(reader.U32());
                types[type] = types.GetValueOrDefault(type) + 1;
                _ = reader.U64(); // Tensor data offset. Do not seek into the weights.
            }
        }
        string? Text(string key) => first!.GetValueOrDefault(key) as string;
        ulong? Number(string key) => first!.GetValueOrDefault(key) is { } value &&
            ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var n) ? n : null;
        var architecture = Text("general.architecture");
        return new(Text("general.name"), architecture, Text("general.size_label"), parameters, tensors,
            Number(architecture + ".context_length"), Number(architecture + ".block_count"),
            Text("tokenizer.ggml.model"), Number("tokenizer.ggml.tokens.length"), Text("tokenizer.chat_template"),
            types, parts.Count, bytes, version);
    }

    private static string TypeName(uint type) => type switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 6 => "Q5_0", 7 => "Q5_1", 8 => "Q8_0", 9 => "Q8_1",
        10 => "Q2_K", 11 => "Q3_K", 12 => "Q4_K", 13 => "Q5_K", 14 => "Q6_K", 15 => "Q8_K",
        16 => "IQ2_XXS", 17 => "IQ2_XS", 18 => "IQ3_XXS", 19 => "IQ1_S", 20 => "IQ4_NL", 21 => "IQ3_S",
        22 => "IQ2_S", 23 => "IQ4_XS", 24 => "I8", 25 => "I16", 26 => "I32", 27 => "I64", 28 => "F64",
        29 => "IQ1_M", 30 => "BF16", 34 => "TQ1_0", 35 => "TQ2_0", _ => $"Type {type}"
    };

    private sealed class HeaderReader(FileStream stream, CancellationToken token)
    {
        private bool _bigEndian;
        private const long HeaderLimit = 512 * 1024 * 1024;
        private readonly byte[] _number = new byte[8];
        private void Ensure(long length)
        {
            token.ThrowIfCancellationRequested();
            if (length < 0 || length > stream.Length - stream.Position || length > HeaderLimit - stream.Position)
                throw new InvalidDataException("GGUF header is truncated or exceeds the 512 MiB inspection limit.");
        }
        private ReadOnlySpan<byte> Bytes(int count) { Ensure(count); stream.ReadExactly(_number.AsSpan(0, count)); return _number.AsSpan(0, count); }
        public uint U32() => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Bytes(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public ulong U64() => _bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Bytes(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        private ushort U16() => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Bytes(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        public string? String(bool retain)
        {
            var length = U64();
            if (length > long.MaxValue) throw new InvalidDataException("Invalid GGUF string length.");
            Ensure((long)length);
            if (!retain || length > 65536) { stream.Seek((long)length, SeekOrigin.Current); return null; }
            var bytes = new byte[(int)length]; stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        public Dictionary<string, object> ReadMetadata(out ulong tensors, out uint version)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4)) != 0x46554747) throw new InvalidDataException("Not a GGUF file.");
            version = U32();
            if (version == 0x03000000) { _bigEndian = true; version = 3; }
            if (version is not (2 or 3)) throw new InvalidDataException($"GGUF version {version} is unsupported; expected 2 or 3.");
            tensors = U64(); var count = U64();
            if (tensors > 2_000_000 || count > 100_000) throw new InvalidDataException("GGUF header contains too many entries.");
            Dictionary<string, object> values = [];
            for (ulong i = 0; i < count; i++)
            {
                var key = String(true) ?? throw new InvalidDataException("GGUF metadata key is too long.");
                var type = U32();
                var retain = key is "general.name" or "general.architecture" or "general.size_label" or
                    "tokenizer.ggml.model" or "tokenizer.chat_template" or "split.count" or "split.no" ||
                    key.EndsWith(".context_length", StringComparison.Ordinal) || key.EndsWith(".block_count", StringComparison.Ordinal);
                var value = Value(type, retain, out var arrayLength);
                if (retain && value is not null && values.Count < 2048) values[key] = value;
                if (key == "tokenizer.ggml.tokens" && arrayLength is { } length) values[key + ".length"] = length;
            }
            return values;
        }
        private object? Value(uint type, bool retain, out ulong? arrayLength)
        {
            arrayLength = null;
            switch (type)
            {
                case 0: return Bytes(1)[0];
                case 1: return unchecked((sbyte)Bytes(1)[0]);
                case 2: return U16();
                case 3: return unchecked((short)U16());
                case 4: return U32();
                case 5: return unchecked((int)U32());
                case 6: return BitConverter.UInt32BitsToSingle(U32());
                case 7: var b = Bytes(1)[0]; return b <= 1 ? b == 1 : throw new InvalidDataException("Invalid GGUF boolean.");
                case 8: return String(retain);
                case 10: return U64();
                case 11: return unchecked((long)U64());
                case 12: return BitConverter.UInt64BitsToDouble(U64());
                case 9:
                    var element = U32(); var count = U64(); arrayLength = count;
                    if (element > 12 || element == 9 || count > 16_000_000) throw new InvalidDataException("Unsupported or oversized GGUF array.");
                    if (element == 8) { for (ulong i = 0; i < count; i++) String(false); }
                    else
                    {
                        var size = element switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, _ => 8 };
                        var bytes = checked((long)count * size); Ensure(bytes); stream.Seek(bytes, SeekOrigin.Current);
                    }
                    return null;
                default: throw new InvalidDataException($"Unsupported GGUF metadata type {type}.");
            }
        }
    }
}
