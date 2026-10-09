using System.Buffers.Binary;
using System.Text;
using LLamaModelLoader.Infrastructure;
using Xunit;

namespace LLamaModelLoader.Tests;

public sealed class GgufTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gguf-tests-" + Guid.NewGuid());
    public GgufTests() => Directory.CreateDirectory(_folder);
    private string FilePath(string name = "model.gguf") => Path.Combine(_folder, name);

    private static void Write(string path, bool bigEndian = false, uint version = 3, uint? shard = null, bool overflow = false)
    {
        using var stream = File.Create(path);
        void U32(uint value) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, value); else BinaryPrimitives.WriteUInt32LittleEndian(b, value); stream.Write(b); }
        void U64(ulong value) { var b = new byte[8]; if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, value); else BinaryPrimitives.WriteUInt64LittleEndian(b, value); stream.Write(b); }
        void Text(string value) { var b = Encoding.UTF8.GetBytes(value); U64((ulong)b.Length); stream.Write(b); }
        void StringValue(string key, string value) { Text(key); U32(8); Text(value); }
        void Integer(string key, uint value) { Text(key); U32(4); U32(value); }
        stream.Write("GGUF"u8); U32(version); U64(2); U64(shard is null ? 9UL : 11UL);
        StringValue("general.name", "Model \u03A9"); StringValue("general.architecture", "qwen35");
        Integer("qwen35.context_length", 262144); Integer("qwen35.block_count", 32);
        StringValue("tokenizer.ggml.model", "gpt2"); StringValue("tokenizer.chat_template", "{{ messages }}");
        Text("tokenizer.ggml.tokens"); U32(9); U32(8); U64(3); Text("a"); Text("long token"); Text("\u03A9");
        Text("unknown.array"); U32(9); U32(6); U64(2); U32(0); U32(0);
        StringValue("general.size_label", "9B");
        if (shard is { } number) { Integer("split.no", number); Integer("split.count", 2); }
        Text("weight"); U32(2); U64(overflow ? ulong.MaxValue : 10); U64(20); U32(30); U64(0);
        Text("bias"); U32(1); U64(10); U32(0); U64(0);
        // No tensor payload is needed for metadata inspection.
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public void ReadsHeadersAndSkipsVocabularyWithoutTensorPayload(bool bigEndian, int version)
    {
        var path = FilePath(); Write(path, bigEndian, (uint)version);
        var metadata = GgufReader.Read(path);
        Assert.Equal("Model \u03A9", metadata.Name); Assert.Equal("qwen35", metadata.Architecture);
        Assert.Equal(210UL, metadata.StoredParameters); Assert.Equal(262144UL, metadata.ContextLength);
        Assert.Equal(32UL, metadata.Layers); Assert.Equal(3UL, metadata.VocabularySize);
        Assert.Equal("{{ messages }}", metadata.ChatTemplate);
        Assert.Equal(1, metadata.TensorTypes["BF16"]); Assert.Equal(1, metadata.TensorTypes["F32"]);
        Assert.Equal(new FileInfo(path).Length, metadata.FileBytes);
    }

    [Fact]
    public void AggregatesAllShardsAndRejectsMissingOrMisnumberedParts()
    {
        var first = FilePath("model-00001-of-00002.gguf"); var second = FilePath("model-00002-of-00002.gguf");
        Write(first, shard: 0);
        Assert.Throws<IOException>(() => GgufReader.Read(first));
        Write(second, shard: 1);
        var result = GgufReader.Read(first);
        Assert.Equal(2, result.Parts); Assert.Equal(420UL, result.StoredParameters); Assert.Equal(4UL, result.TensorCount);
        Write(second, shard: 0);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(first));
    }

    [Fact]
    public void RejectsUnsupportedTruncatedMaliciousAndOverflowingHeaders()
    {
        var path = FilePath();
        File.WriteAllText(path, "NOPE"); Assert.Throws<InvalidDataException>(() => GgufReader.Read(path));
        Write(path, version: 4); Assert.Throws<InvalidDataException>(() => GgufReader.Read(path));
        Write(path, overflow: true); Assert.Throws<OverflowException>(() => GgufReader.Read(path));
        Write(path); using (var file = File.OpenWrite(path)) file.SetLength(50);
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(path));
        Write(path); using (var file = File.OpenWrite(path)) { file.Position = 24; file.Write(BitConverter.GetBytes(ulong.MaxValue)); }
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(path));
    }

    [Fact]
    public async Task CancellationDoesNotReturnStaleMetadata()
    {
        var path = FilePath(); Write(path);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GgufReader.ReadAsync(path, canceled.Token));
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
