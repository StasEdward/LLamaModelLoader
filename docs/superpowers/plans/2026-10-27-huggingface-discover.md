# Hugging Face Discover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Search public GGUF repositories on huggingface.co, colour each quantization by whether it fits the GPU for a chosen context, download it into the models folder with resume and SHA-256 verification, then offer a prefilled profile.

**Architecture:** Pure records, name validation and the memory estimator live in Core. HTTP (search, details, remote GGUF header prefix, downloads) and the download queue live in Infrastructure, so xunit can test them with a fake `HttpMessageHandler`. Desktop adds one code-built `DiscoverView` page, wired through `MainViewModel`, in the same style as the existing views.

**Tech Stack:** .NET 10, C# (nullable, warnings as errors), Avalonia 12.1.1, CommunityToolkit.Mvvm 8.4.2, xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-27-huggingface-discover-design.md`

## Global Constraints

- **Precondition:** the uncommitted changes the user already has in the working tree (`OptionCatalog.cs`, `OptionDocumentation.cs`, `App.axaml`, `MainWindow.axaml.cs`, `CoreTests.cs`, `SmokeChecks/Program.cs`, `README.md`) must be committed or stashed before Task 8. Tasks 8 and 9 edit `MainWindow.axaml.cs`, `SmokeChecks/Program.cs` and `README.md`, and every commit must contain only that task's hunks.
- Branch: `feature/huggingface-discover`. Build with `dotnet build LLamaModelLoader.slnx -c Release -m:1`. Test with `dotnet test tests/LLamaModelLoader.Tests -c Release -m:1`.
- Hosts: API and downloads are `https://huggingface.co` only. CDN redirects must stay on `https`. User-Agent `LLamaModelLoader/1.0`.
- Public repositories only. No token, cookie or credential is stored or sent.
- API responses are buffered with an 8 MiB limit. Each API call has a 45 s timeout linked to the caller's token.
- Remote GGUF header prefix limit: 64 MiB. The server must answer `206`.
- Estimator constants: reserve `1024 MiB`, compute `vocab × 512 × 4 + 512 MiB` with default vocab `32000`, system RAM held back `8 GiB`. KV bytes/element: `f16`/`bf16` 2, `f32` 4, `q8_0` 34/32, `q4_0` 18/32, `q4_1` 20/32, `q5_0` 22/32, `q5_1` 24/32.
- Download free-space margin: `256 MiB`. Progress reports at most every `150 ms`.
- Destination: `{ModelsDirectory}\{owner}\{repo}\{repo path}`. Partial files use the `.part` suffix.
- UI copy is English, like the rest of the application.
- Match the surrounding style: compact expression-bodied members, `GeneratedRegex`, records, code-built Avalonia controls, no XAML for the new page.

**Deviations from the spec, decided here:**
1. `HttpRangeStream` is replaced by `RemoteGguf` fetching a growing byte prefix (1 MiB, then doubling, up to 64 MiB) and parsing it from a `MemoryStream`. The reader stays synchronous, there is no sync-over-async HTTP, and the limits are the same.
2. `DownloadQueue` lives in Infrastructure (not Desktop) so it can be unit-tested. `MainViewModel` owns it.
3. `GgufKvShape` stores flags (`SlidingWindow`, `Recurrent`, `KvLoraRank`) instead of a reason string. `Approximate` is derived from them.

## Review Focus

1. **A repo whose only GGUFs are split shards in subfolders, with one shard missing upstream.** The quantization must not be offered, rather than downloading an unusable set. Pinned in Task 1 (`GroupsShardsAndDropsIncompleteSets`).
2. **The app is closed or the network drops mid-download, then the user downloads again.** The download must resume from the `.part` and still verify the SHA-256 of the whole file. Pinned in Task 6 (`ResumesFromPartWith206AndVerifiesWholeFile`) and Task 7 (`CancelKeepsPartAndPausesItem`).
3. **The models folder is not configured, or was moved after Settings were saved.** Download must be disabled with guidance, not throw from deep in the downloader. Pinned in Task 7 (`EnqueueRequiresExistingModelsFolder`).
4. **A model whose context max is smaller than the selected context (for example 4096).** The selector must clamp, and the estimate must use the clamped value. Pinned in Task 3 (`ContextChoicesAreCappedAtModelMax`).
5. **A malicious or odd repo path (`../`, `CON.gguf`, a backslash, an absolute path) in the siblings list.** It must be skipped at parse time and rejected again at download time. Pinned in Task 1 (`RejectsUnsafeRepoPaths`) and Task 6 (`RejectsPathsOutsideModelsFolder`).

---

### Task 1: Core Hugging Face records and name rules

**Files:**
- Create: `src/LLamaModelLoader.Core/HuggingFace.cs`
- Test: `tests/LLamaModelLoader.Tests/HuggingFaceTests.cs`

**Interfaces:**
- Produces:
  - `record HfModelSummary(string Id, string Author, long Downloads, long Likes, DateTimeOffset? LastModified, bool Gated, string? PipelineTag, IReadOnlyList<string> Tags)` with `bool IsVision` (pipeline `image-text-to-text` or tag `vision`/`multimodal`).
  - `record HfFile(string Path, long Size, string Sha256)`.
  - `record HfQuantization(string Label, IReadOnlyList<HfFile> Parts)` with `long TotalBytes` and `string FirstPath`.
  - `record HfModelDetails(string Id, string Sha, bool Gated, string? Architecture, ulong? Parameters, ulong? ContextLength, string? License, IReadOnlyList<HfQuantization> Quantizations)`.
  - `enum HfSort { Downloads, Likes, LastModified, Trending }` with `static string HfNames.SortKey(HfSort)` → `downloads`, `likes`, `lastModified`, `trendingScore`.
  - `static partial class HfNames`: `bool IsValidRepoId(string)`, `bool IsSafeRepoPath(string)`, `bool IsSafeSegment(string)`, `string QuantLabel(string path)`, `IReadOnlyList<HfQuantization> GroupQuantizations(IEnumerable<HfFile>)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("unsloth/Qwen3-8B-GGUF", true)]
[InlineData("bartowski/Llama-3.3-70B-Instruct-GGUF", true)]
[InlineData("no-slash", false)]
[InlineData("a/b/c", false)]
[InlineData("../evil", false)]
[InlineData("owner/..", false)]
[InlineData("-owner/repo", false)]
public void ValidatesRepoIds(string id, bool valid) => Assert.Equal(valid, HfNames.IsValidRepoId(id));

[Theory]
[InlineData("Qwen3-8B-Q4_K_M.gguf", true)]
[InlineData("IQ2_XS/Model-IQ2_XS-00001-of-00002.gguf", true)]
[InlineData("../Model.gguf", false)]
[InlineData("sub/../../Model.gguf", false)]
[InlineData("CON.gguf", false)]
[InlineData("sub\\Model.gguf", false)]
[InlineData("/abs/Model.gguf", false)]
[InlineData("C:/Model.gguf", false)]
[InlineData("Model.bin", false)]
[InlineData("Model .gguf", true)]
public void RejectsUnsafeRepoPaths(string path, bool safe) => Assert.Equal(safe, HfNames.IsSafeRepoPath(path));

[Theory]
[InlineData("Qwen3-8B-Q4_K_M.gguf", "Q4_K_M")]
[InlineData("Qwen3-8B-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
[InlineData("IQ2_XS/Model-IQ2_XS-00001-of-00002.gguf", "IQ2_XS")]
[InlineData("model-bf16.gguf", "BF16")]
[InlineData("Model-Q8_0.gguf", "Q8_0")]
[InlineData("model.gguf", "model")]
public void ParsesQuantizationLabels(string path, string label) => Assert.Equal(label, HfNames.QuantLabel(path));

[Fact]
public void GroupsShardsAndDropsIncompleteSets()
{
    HfFile F(string p, long s) => new(p, s, new string('a', 64));
    var groups = HfNames.GroupQuantizations([
        F("Q8_0/M-Q8_0-00001-of-00002.gguf", 10), F("Q8_0/M-Q8_0-00002-of-00002.gguf", 5),
        F("Q4_K_M/M-Q4_K_M-00001-of-00003.gguf", 4), F("Q4_K_M/M-Q4_K_M-00003-of-00003.gguf", 4), // part 2 missing
        F("M-Q2_K.gguf", 3), F("mmproj-M-F16.gguf", 1)]);
    Assert.Equal(["Q2_K", "Q8_0"], groups.Select(g => g.Label));
    Assert.Equal(15, groups[1].TotalBytes);
    Assert.Equal("Q8_0/M-Q8_0-00001-of-00002.gguf", groups[1].FirstPath);
    Assert.Equal(2, groups[1].Parts.Count);
}
```

- [ ] **Step 2: Run the tests and check they fail**

Run: `dotnet test tests/LLamaModelLoader.Tests -c Release -m:1 --filter FullyQualifiedName~HuggingFaceTests`
Expected: build error, `HfNames` not found.

- [ ] **Step 3: Implement `HuggingFace.cs`**

- Repo id: `^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$`, and neither segment is `.` or `..`.
- Repo path: relative, `/`-separated, no `\`, no `:`, ends with `.gguf` (case-insensitive), every segment passes `IsSafeSegment`.
- `IsSafeSegment`: move the logic of `ServerInstaller.UnsafePart` into this public Core helper (inverted) and make `ServerInstaller` call `!HfNames.IsSafeSegment(part)`. Do not duplicate it.
- `QuantLabel`: strip the folder and the `-0000k-of-0000n` suffix, then match `(?i)(?:^|[-_.])((?:UD-)?(?:I?Q\d+(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?|TQ\d_\d))$` on the stem and return the match upper-cased; otherwise return the stem.
- `GroupQuantizations`: skip paths containing `mmproj` (case-insensitive) or failing `IsSafeRepoPath`. Group shards by the `(prefix, count)` of `^(.*)-(\d{5})-of-(\d{5})\.gguf$` and keep only complete sets, with parts ordered by number. Sort by `TotalBytes`.

- [ ] **Step 4: Run the tests and check they pass**

Run: the same command. Expected: all `HuggingFaceTests` pass. Also run the full suite: `ServerInstallationTests` must still pass after the `UnsafePart` move.

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Core/HuggingFace.cs src/LLamaModelLoader.Infrastructure/ServerInstaller.cs tests/LLamaModelLoader.Tests/HuggingFaceTests.cs
git commit -m "Add Hugging Face records and repository name rules"
```

---

### Task 2: HuggingFaceClient (search and details)

**Files:**
- Create: `src/LLamaModelLoader.Infrastructure/HuggingFaceClient.cs`
- Test: `tests/LLamaModelLoader.Tests/HuggingFaceTests.cs` (append)

**Interfaces:**
- Consumes: Task 1 records and `HfNames`.
- Produces: `sealed class HuggingFaceClient(HttpClient http)` with
  - `Task<(IReadOnlyList<HfModelSummary> Models, string? NextPageUrl)> SearchAsync(string query, HfSort sort, string? nextPageUrl, CancellationToken token)`
  - `Task<HfModelDetails> GetDetailsAsync(string repoId, CancellationToken token)`
  - `static IReadOnlyList<HfModelSummary> ParseSearch(string json)`, `static HfModelDetails ParseDetails(string json)`, `static string? ParseNextLink(string? linkHeader)`

- [ ] **Step 1: Write the failing tests**

- `ParsesSearchResultsAndSkipsMalformedEntries`: a JSON array with one valid model (`id`, `author`, `downloads`, `likes`, `lastModified`, `gated: false`, `pipeline_tag: "image-text-to-text"`, `tags`), one `gated: "manual"` model, one entry without `id`, and one with id `../x`. Assert 2 results, `Gated` false/true, `IsVision` true for the first.
- `ParsesDetailsWithLicenseShaAndQuantizations`: JSON with `id`, `sha` (40 hex), `gated: false`, `gguf { total: 8190735360, architecture: "qwen3", context_length: 40960 }`, `tags: ["license:apache-2.0"]`, and `siblings` holding `.gitattributes` (no lfs), a valid `Q4_K_M` file with `lfs.sha256`, a gguf without `lfs`, and a gguf with `size: 0`. Assert `License == "apache-2.0"`, `Parameters == 8190735360`, `ContextLength == 40960`, a single quantization `Q4_K_M`.
- `NextLinkAcceptsOnlyHuggingFaceApi`: `<https://huggingface.co/api/models?cursor=x>; rel="next"` returns the URL; `<https://evil.example/api/models?x>; rel="next"` and `<http://huggingface.co/api/models?x>; rel="next"` return null.
- `SearchBuildsQueryAndReportsRateLimit`: a fake handler asserts the request URI is `https://huggingface.co/api/models?search=qwen%203&filter=gguf&sort=downloads&limit=30&full=false` and the User-Agent contains `LLamaModelLoader/1.0`. A second call answering 429 throws `HttpRequestException` whose message contains `rate limit`.
- `DetailsRequestUsesBlobsAndExpand`: the URI is `https://huggingface.co/api/models/unsloth/Qwen3-8B-GGUF?blobs=true&expand[]=gguf&expand[]=siblings&expand[]=gated&expand[]=sha&expand[]=tags`. An invalid repo id throws `ArgumentException` without any request being made.

- [ ] **Step 2: Run the tests and check they fail** (`--filter FullyQualifiedName~HuggingFaceTests`). Expected: build error.

- [ ] **Step 3: Implement `HuggingFaceClient`**

Follow `ServerReleaseClient`: `LoadIntoBufferAsync(8 * 1024 * 1024, token)`, 403/429 → `HttpRequestException("Hugging Face refused the request (rate limit). Try again later.")`, other failures → `EnsureSuccessStatusCode`. Use a linked token with `CancelAfter(45 s)`. `gated` counts as true unless it is JSON `false` or absent. The license comes from the first `license:` tag. When `nextPageUrl` is non-null and passes the same check as `ParseNextLink`, request it verbatim. A fully malformed document (not an array/object) throws `InvalidDataException("Unrecognized response from Hugging Face.")`.

- [ ] **Step 4: Run the tests and check they pass.**

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Infrastructure/HuggingFaceClient.cs tests/LLamaModelLoader.Tests/HuggingFaceTests.cs
git commit -m "Add Hugging Face search and model details client"
```

---

### Task 3: FitEstimator

**Files:**
- Create: `src/LLamaModelLoader.Core/FitEstimator.cs`
- Test: `tests/LLamaModelLoader.Tests/FitEstimatorTests.cs`

**Interfaces:**
- Produces:
  - `record GgufKvShape(string Architecture, int Layers, IReadOnlyList<int> HeadCountKv, int KeyLength, int ValueLength, ulong? VocabularySize, int? KvLoraRank, int? RopeDimension, bool SlidingWindow, bool Recurrent)` with `bool Approximate => SlidingWindow || Recurrent || KvLoraRank is not null`.
  - `record FitInput(long WeightBytes, GgufKvShape? Shape, int ContextSize, string KvType, long? VramBytes, long RamBytes)`.
  - `enum FitLevel { Unknown, Fits, Partial, DoesNotFit }`.
  - `record FitResult(FitLevel Level, long WeightBytes, long? KvBytes, long ComputeBytes, long ReserveBytes, long TotalBytes, int? GpuLayers, bool ContextAware, bool Approximate)`.
  - `static class FitEstimator`: `FitResult Estimate(FitInput)`, `long KvBytes(GgufKvShape, int context, string kvType)`, `IReadOnlyList<int> ContextChoices(ulong? modelMax)`, `int DefaultContext(ulong? modelMax)`, `const long ReserveBytes = 1024L << 20`.

- [ ] **Step 1: Write the failing tests**

Use the Qwen3-8B shape: `new GgufKvShape("qwen3", 36, Enumerable.Repeat(8, 36).ToArray(), 128, 128, 151936, null, null, false, false)`.

```csharp
[Fact] public void KvCacheForQwen3At16kF16() => Assert.Equal(2_415_919_104L, FitEstimator.KvBytes(Qwen, 16384, "f16"));
[Fact] public void KvCacheQ8() => Assert.Equal(1_283_457_024L, FitEstimator.KvBytes(Qwen, 16384, "q8_0"));
[Fact] public void PerLayerHeadsSkipRecurrentLayers() =>
    Assert.Equal(8_388_608L, FitEstimator.KvBytes(Qwen with { Layers = 4, HeadCountKv = [8, 0, 8, 0], Recurrent = true }, 1024, "f16"));
[Fact] public void MlaUsesLoraRankPlusRope() =>
    Assert.Equal(287_834_112L, FitEstimator.KvBytes(Qwen with { Layers = 61, HeadCountKv = Enumerable.Repeat(128, 61).ToArray(), KvLoraRank = 512, RopeDimension = 64 }, 4096, "f16"));

[Fact]
public void Q4KmFitsOnSixteenGigabytes()
{
    var r = FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", 16303L << 20, 64L << 30));
    Assert.Equal(FitLevel.Fits, r.Level);
    Assert.Equal(848_035_840L, r.ComputeBytes);
    Assert.Equal(9_365_481_280L, r.TotalBytes);
    Assert.True(r.ContextAware); Assert.False(r.Approximate);
}

[Fact]
public void ExactBoundaryFitsAndOneByteLessIsPartial()
{
    var total = 9_365_481_280L;
    Assert.Equal(FitLevel.Fits, FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", total, 64L << 30)).Level);
    Assert.Equal(FitLevel.Partial, FitEstimator.Estimate(new(5_027_784_512, Qwen, 16384, "f16", total - 1, 64L << 30)).Level);
}

[Fact]
public void LargeModelIsPartialWithLayerCount()
{
    var r = FitEstimator.Estimate(new(20L << 30, Qwen, 16384, "f16", 16L << 30, 64L << 30));
    Assert.Equal(FitLevel.Partial, r.Level); Assert.Equal(22, r.GpuLayers);
}

[Fact] public void HugeModelDoesNotFit() =>
    Assert.Equal(FitLevel.DoesNotFit, FitEstimator.Estimate(new(80L << 30, Qwen, 16384, "f16", 16L << 30, 64L << 30)).Level);

[Fact]
public void WithoutShapeUsesSizeOnly()
{
    var r = FitEstimator.Estimate(new(5_000_000_000, null, 16384, "f16", 16L << 30, 64L << 30));
    Assert.Null(r.KvBytes); Assert.False(r.ContextAware); Assert.Null(r.GpuLayers);
    Assert.Equal(65_536_000L + (512L << 20), r.ComputeBytes);
}

[Fact] public void WithoutVramIsUnknown() =>
    Assert.Equal(FitLevel.Unknown, FitEstimator.Estimate(new(1, Qwen, 4096, "f16", null, 64L << 30)).Level);

[Fact]
public void ContextChoicesAreCappedAtModelMax()
{
    Assert.Equal([4096], FitEstimator.ContextChoices(4096));
    Assert.Equal([4096, 8192, 16384, 32768, 40960], FitEstimator.ContextChoices(40960));
    Assert.Equal(4096, FitEstimator.DefaultContext(4096));
    Assert.Equal(16384, FitEstimator.DefaultContext(40960));
    Assert.Equal(16384, FitEstimator.DefaultContext(null));
    Assert.Equal([4096, 8192, 16384, 32768, 65536, 131072], FitEstimator.ContextChoices(null));
}
```

- [ ] **Step 2: Run and check they fail** (`--filter FullyQualifiedName~FitEstimatorTests`).

- [ ] **Step 3: Implement `FitEstimator.cs`**

- Formulas and thresholds exactly as in the spec's "Formula" bullet, with the Task-3 correction: `GpuLayers = clamp(floor((Vram − Reserve − Compute) / ((Weights + Kv) / (double)Layers)), 0, Layers − 1)`.
- MLA: when both `KvLoraRank` and `RopeDimension` are set, elements = `ctx × Layers × (KvLoraRank + RopeDimension)`; otherwise the generic formula.
- Unknown `KvType` → `ArgumentException`.
- Byte math: `checked` long arithmetic; multiply elements by the numerator before dividing by the denominator.
- `ContextChoices`: the list `[4096, 8192, 16384, 32768, 65536, 131072]` filtered to values `< max`, plus `max` itself (or `4096` alone when max ≤ 4096). With null max, the full list.

- [ ] **Step 4: Run and check they pass.**

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Core/FitEstimator.cs tests/LLamaModelLoader.Tests/FitEstimatorTests.cs
git commit -m "Add context-aware GPU fit estimator"
```

---

### Task 4: GGUF KV shape reader

**Files:**
- Modify: `src/LLamaModelLoader.Infrastructure/GgufReader.cs`
- Test: `tests/LLamaModelLoader.Tests/GgufTests.cs` (append)

**Interfaces:**
- Consumes: `GgufKvShape` (Task 3).
- Produces: `public static GgufKvShape GgufReader.ReadKvShape(Stream stream, CancellationToken token = default)`; `public sealed class GgufTruncatedException(string message) : InvalidDataException(message)`.

- [ ] **Step 1: Write the failing tests**

Add a helper `byte[] KvHeader(Action<Writer>? extra = null)` that writes `general.architecture = "llama"`, `llama.block_count = 4`, `llama.attention.head_count = 32`, `llama.embedding_length = 4096`, a `tokenizer.ggml.tokens` array of 3 strings, and 0 tensors.
- `ReadsKvShapeWithScalarHeadsAndFallbackLengths`: with `llama.attention.head_count_kv = 8` and no key/value length → `HeadCountKv == [8,8,8,8]`, `KeyLength == ValueLength == 128`, `VocabularySize == 3`, `Approximate` false.
- `ReadsPerLayerHeadArrayAndFlags`: `head_count_kv` as an int32 array `[8,0,8,0]`, `key_length = 64`, `value_length = 64`, `sliding_window = 4096`, `ssm.state_size = 16`, `kv_lora_rank = 512`, `rope.dimension_count = 64` → array preserved, `SlidingWindow`, `Recurrent`, `KvLoraRank == 512`, `RopeDimension == 64`.
- `MissingHeadCountKvFallsBackToHeadCount` → `[32,32,32,32]`.
- `TruncatedPrefixThrowsTruncatedException`: the first 40 bytes of a valid header in a `MemoryStream` → `GgufTruncatedException`.
- The existing `GgufTests` stay green (the refactor must not change `Read(path)`).

- [ ] **Step 2: Run and check they fail** (`--filter FullyQualifiedName~GgufTests`).

- [ ] **Step 3: Implement**

- `HeaderReader` takes `Stream`. `Ensure` throws `GgufTruncatedException` when `length > stream.Length - stream.Position`, and plain `InvalidDataException` for the 512 MiB limit. Keep the message text.
- Extend `retain` with the keys listed in the spec (`head_count`, `head_count_kv`, `key_length`, `value_length`, `embedding_length`, `sliding_window`, `kv_lora_rank`, `rope.dimension_count`). Record `<arch>.ssm.` presence as a boolean entry.
- In `Value`, a retained integer array (element types 0–5, ≤ 4096 elements) returns `int[]`; all other arrays keep skipping.
- `ReadKvShape` reads the metadata only (no tensor descriptors). Missing `block_count` or architecture → `InvalidDataException("The GGUF header has no layer information.")`.

- [ ] **Step 4: Run and check they pass** (whole `GgufTests` class).

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Infrastructure/GgufReader.cs tests/LLamaModelLoader.Tests/GgufTests.cs
git commit -m "Read KV cache shape from GGUF metadata streams"
```

---

### Task 5: RemoteGguf header prefix fetch

**Files:**
- Create: `src/LLamaModelLoader.Infrastructure/RemoteGguf.cs`
- Test: `tests/LLamaModelLoader.Tests/RemoteGgufTests.cs`

**Interfaces:**
- Consumes: `GgufReader.ReadKvShape`, `GgufTruncatedException` (Task 4), `HfNames` (Task 1).
- Produces: `static class RemoteGguf` with `Uri FileUri(string repoId, string sha, string path)` (path segments `Uri.EscapeDataString`-escaped) and `Task<GgufKvShape> ReadKvShapeAsync(HttpClient http, string repoId, string sha, string path, CancellationToken token)`; `const long PrefixLimit = 64L << 20`.

- [ ] **Step 1: Write the failing tests**

Serve a synthetic header (Task 4 helper style, about 3 MiB of tokenizer strings) through a handler that honours `Range: bytes=a-b` with `206` and `Content-Range`.
- `FetchesGrowingPrefixUntilHeaderParses`: the result is correct; the recorded ranges are `0-1048575`, `1048576-2097151`, `2097152-4194303` (each request fetches only the new bytes).
- `StopsAtFileEnd`: a 200 KiB file → the first range is clamped by the server reply and parsing succeeds.
- `RejectsServersWithoutRangeSupport`: the handler answers `200` → `InvalidDataException` containing `range`.
- `EnforcesPrefixLimit`: a header that needs more than 64 MiB (a fake handler returning endless bytes of a long string) → `InvalidDataException` containing `64 MiB`; no request starts beyond `PrefixLimit`.
- `FileUriEscapesSegments`: `RemoteGguf.FileUri("o/r", "abc", "Q4 K/M#1.gguf")` → `https://huggingface.co/o/r/resolve/abc/Q4%20K/M%231.gguf`. An unsafe path → `ArgumentException`.

- [ ] **Step 2: Run and check they fail.**

- [ ] **Step 3: Implement `ReadKvShapeAsync`**

Loop: request `bytes={have}-{target-1}` with `target` = 1 MiB, then doubling, capped at `PrefixLimit`. Require `206`. Append to the buffer and try `ReadKvShape(new MemoryStream(buffer))`. On `GgufTruncatedException`, grow and retry, stopping when the total length from `Content-Range` is reached. Past the limit, throw `InvalidDataException("The GGUF header exceeds the 64 MiB inspection limit.")`.

- [ ] **Step 4: Run and check they pass.**

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Infrastructure/RemoteGguf.cs tests/LLamaModelLoader.Tests/RemoteGgufTests.cs
git commit -m "Read remote GGUF headers through range requests"
```

---

### Task 6: ModelDownloader

**Files:**
- Create: `src/LLamaModelLoader.Infrastructure/ModelDownloader.cs`, `src/LLamaModelLoader.Infrastructure/SafePaths.cs`
- Modify: `src/LLamaModelLoader.Infrastructure/ServerInstaller.cs` (use `SafePaths`), `src/LLamaModelLoader.Core/HuggingFace.cs` (`DownloadProgress`)
- Test: `tests/LLamaModelLoader.Tests/ModelDownloaderTests.cs`

**Interfaces:**
- Consumes: Task 1 records, `RemoteGguf.FileUri` (Task 5).
- Produces:
  - `enum LocalModelState { NotDownloaded, Partial, Downloaded, Conflict }`
  - `record DownloadProgress(long DownloadedBytes, long TotalBytes, int Part, int Parts, double BytesPerSecond)` (in Core `HuggingFace.cs`)
  - `sealed class ModelDownloader(string modelsDirectory, HttpClient http, Func<string, long>? freeSpace = null)` with `string Destination(string repoId, HfFile file)`, `(LocalModelState State, long PartialBytes) GetState(string repoId, HfQuantization quant)`, `Task<string> DownloadAsync(string repoId, string sha, HfQuantization quant, IProgress<DownloadProgress>? progress, CancellationToken token)` (returns the first part's path), and `void DeletePartial(string repoId, HfQuantization quant)`.

- [ ] **Step 1: Write the failing tests** (temp models folder per test, like `ServerInstallationTests`)

- `DownloadsVerifiesAndMovesIntoRepoFolder`: two shards; the final files are at `{root}\o\r\Q8_0\M-Q8_0-0000k-of-00002.gguf`; no `.part` remains; the returned path is the first shard; the request URIs are `https://huggingface.co/o/r/resolve/<sha>/...`.
- `ResumesFromPartWith206AndVerifiesWholeFile`: a pre-existing `.part` holding the first 100 bytes; the handler asserts `Range: bytes=100-` and returns 206 with the rest; the final hash matches.
- `RestartsWhenServerIgnoresRange`: `.part` exists, the handler returns 200 with the full body → the final file is correct (no duplicated prefix).
- `DiscardsOversizedPart`: a `.part` longer than the expected size is deleted before the request, which has no `Range` header.
- `ShaMismatchDeletesPartAndThrows`: `InvalidDataException` with message `The downloaded file is corrupted (size or SHA-256 mismatch). Download it again.`; no `.part`.
- `SizeMismatchThrows`: the body is 1 byte longer than declared → the same exception.
- `CancellationKeepsPart`: the handler streams half the content then waits; cancelling gives `OperationCanceledException` and a `.part` with the half length.
- `InsufficientSpaceFailsBeforeRequest`: `freeSpace` returns `remaining + 256 MiB - 1` → `IOException` mentioning both sizes; the handler was never called.
- `SkipsCompletePartsAndReportsState`: the first shard already final with the right size → only the second is requested; `GetState` reports `Partial` before and `Downloaded` after; a final file with the wrong size → `Conflict`.
- `RejectsPathsOutsideModelsFolder`: `HfFile("../x.gguf", …)` and `HfFile("CON.gguf", …)` → `ArgumentException` from `Destination`.

- [ ] **Step 2: Run and check they fail.**

- [ ] **Step 3: Implement `ModelDownloader`**

Reuse the `ServerInstaller` patterns:
- the reparse-point check, extracted to a shared `internal static class SafePaths { void CheckNoReparsePoints(string path); }` and used by both classes;
- `IncrementalHash` SHA-256, 128 KiB buffer, progress at most every 150 ms.

Download details:
- Open `.part` with `FileMode.Append` after hashing the existing bytes.
- Fail with `InvalidDataException("Download redirected to an insecure address.")` when `response.RequestMessage.RequestUri.Scheme != "https"`.
- When a `Conflict` file already exists at the destination, throw `IOException($"A different file already exists at {path}.")`.
- Free-space message: `$"Not enough disk space: {required / 1073741824d:N2} GiB required, {available / 1073741824d:N2} GiB available."`.

- [ ] **Step 4: Run and check they pass**, together with `ServerInstallationTests`.

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Core/HuggingFace.cs src/LLamaModelLoader.Infrastructure/ModelDownloader.cs src/LLamaModelLoader.Infrastructure/SafePaths.cs src/LLamaModelLoader.Infrastructure/ServerInstaller.cs tests/LLamaModelLoader.Tests/ModelDownloaderTests.cs
git commit -m "Add resumable verified model downloader"
```

---

### Task 7: DownloadQueue and GpuInfo

**Files:**
- Create: `src/LLamaModelLoader.Infrastructure/DownloadQueue.cs`, `src/LLamaModelLoader.Infrastructure/GpuInfo.cs`
- Test: `tests/LLamaModelLoader.Tests/DownloadQueueTests.cs`

**Interfaces:**
- Consumes: `ModelDownloader` (Task 6), `FitResult` (Task 3), `StatisticsClient.ReadGpusAsync`.
- Produces:
  - `enum DownloadItemState { Queued, Downloading, Paused, Failed, Completed }`
  - `sealed class DownloadItem` (mutable, raised through the queue's event): `string RepoId`, `string Sha`, `HfQuantization Quantization`, `int ContextSize`, `string KvType`, `FitResult? Fit`, `DownloadItemState State`, `DownloadProgress? Progress`, `string Message`, `string? FinalPath`.
  - `sealed class DownloadQueue(Func<string> modelsDirectory, HttpClient http, Func<string, long>? freeSpace = null) : IAsyncDisposable` with `IReadOnlyList<DownloadItem> Items`, `bool IsActive`, `event Action<DownloadItem>? Changed`, `DownloadItem Enqueue(string repoId, string sha, HfQuantization quant, int context, string kvType, FitResult? fit)`, `void Pause(DownloadItem)`, `void Resume(DownloadItem)`, `void Remove(DownloadItem)` (deletes the `.part`), `Task CancelAllAsync()`.
  - `record GpuMemory(string Name, long TotalBytes, long UsedBytes)`; `static class GpuInfo` with `Task<(GpuMemory? Gpu, string? Error)> ReadAsync(Func<CancellationToken, Task<(IReadOnlyList<GpuStatistics>, string?)>>? read, CancellationToken token)` (largest `TotalMiB` wins; MiB → bytes) and `long TotalRamBytes()` (`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`).

- [ ] **Step 1: Write the failing tests**

- `RunsOneDownloadAtATimeInOrder`: two items; the handler blocks the first; the second stays `Queued` until the first completes; both end `Completed` with `FinalPath` set.
- `CancelKeepsPartAndPausesItem`: `Pause` during the transfer → `Paused`, the `.part` exists; `Resume` → `Completed`, with the second request carrying `Range`.
- `FailureMarksItemFailedWithMessage`: a SHA mismatch → `Failed`, `Message` equals the downloader's corruption text; the next queued item still runs.
- `EnqueueRequiresExistingModelsFolder`: `modelsDirectory` returns `""` or a missing folder → `InvalidOperationException("Set a models folder in Settings first.")`.
- `EnqueueRejectsDuplicates`: the same repo and quantization already queued or downloading → returns the existing item.
- `CancelAllAsyncWaitsForActiveDownload`: after `await CancelAllAsync()`, `IsActive` is false and the `.part` remains.
- `GpuInfoPicksLargestDevice`: two `GpuStatistics` (8192 and 16303 MiB) → `TotalBytes == 16303L << 20`, `Name` of the larger; empty list → `(null, error)`.

- [ ] **Step 2: Run and check they fail.**

- [ ] **Step 3: Implement**

The queue runs a single background worker loop. Each item has its own `CancellationTokenSource`, linked to a queue-wide one. Pause cancels the item and marks it `Paused` (not `Failed`). `Changed` is raised on the worker thread; the Desktop layer marshals it to the UI thread. Each operation builds a fresh `ModelDownloader` from `modelsDirectory()`, so a changed Settings folder takes effect for new items.

- [ ] **Step 4: Run and check they pass.**

- [ ] **Step 5: Commit**

```bash
git add src/LLamaModelLoader.Infrastructure/DownloadQueue.cs src/LLamaModelLoader.Infrastructure/GpuInfo.cs tests/LLamaModelLoader.Tests/DownloadQueueTests.cs
git commit -m "Add sequential download queue and GPU memory probe"
```

---

### Task 8: Discover page, view-model wiring, Create profile

**Files:**
- Create: `src/LLamaModelLoader.Desktop/DiscoverView.cs`
- Modify: `src/LLamaModelLoader.Desktop/MainViewModel.cs` (queue ownership, dispose), `src/LLamaModelLoader.Desktop/MainWindow.axaml` (nav button), `src/LLamaModelLoader.Desktop/MainWindow.axaml.cs` (navigation, `Editor` prefill entry point, exit confirmation)
- Test: `tests/SmokeChecks/DiscoverSmokeChecks.cs`, `tests/SmokeChecks/Program.cs` (one call)

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `MainViewModel.Downloads` (`DownloadQueue`, built with `() => Configuration.Settings.ModelsDirectory` and an `HttpClient { Timeout = Timeout.InfiniteTimeSpan }`). `DisposeAsync` awaits `Downloads.CancelAllAsync()` and then disposes it.
  - `MainViewModel(string directory, HttpMessageHandler? huggingFaceHandler = null)`: the optional handler is for smoke checks.
  - `DiscoverView(MainViewModel vm, Action<ModelProfile> createProfile, Func<CancellationToken, Task<(GpuMemory?, string?)>>? readGpu = null, Func<long>? readRam = null) : UserControl`. Defaults are `GpuInfo.ReadAsync(null, …)` and `GpuInfo.TotalRamBytes`. Named controls for checks: `DiscoverSearch`, `DiscoverSort`, `DiscoverResults`, `DiscoverContext`, `DiscoverKvType`, `DiscoverManualVram`, `DiscoverQuantizations`, `DiscoverQueue`, `DiscoverStatus`.
  - `static ModelProfile DiscoverView.ProfileFor(string repoId, HfQuantization quant, string path, int context, string kvType, FitResult? fit)`.

- [ ] **Step 1: Write the failing smoke check** `DiscoverSmokeChecks.RunAsync(string directory)`

It uses a fixture handler for search (one public repo `o/r`, one gated repo), details (quantizations `Q4_K_M` 2 GiB, `Q8_0` 15 GiB and `F16` 200 GiB, declared sizes only) and range header requests (the Task 4 header: 4 layers, 8 KV heads, 128/128, vocab 3). `readGpu` returns `("NVIDIA GeForce RTX 5080", 16303 MiB, 2128 MiB)`; `readRam` returns `64 GiB`, so the result does not depend on the machine. The check asserts:
- searching shows the result rows; the gated repo's details show no Download buttons and an `Open on Hugging Face` button;
- selecting `o/r` shows the badges `Fits in VRAM` (Q4_K_M), `Partial offload · 3/4 layers` (Q8_0) and `Does not fit` (F16), plus the GPU line `NVIDIA GeForce RTX 5080 · 15.92 GiB (2.08 GiB in use)`;
- changing `DiscoverContext` updates the breakdown text without a new HTTP request;
- setting `DiscoverManualVram` to `"8"` changes the Q8_0 badge to `Partial offload · 1/4 layers`;
- when the search handler throws `HttpRequestException`, `DiscoverStatus` reads `Hugging Face is unavailable. Check the connection and try again.`, and on 429 the previous results stay listed;
- with an empty models folder, the Download buttons are disabled and `DiscoverStatus` reads `Set a models folder in Settings first.`;
- with a folder set, Download completes through the queue and `Create profile` calls the callback with `ProfileFor(...)` values: `Name == "r-Q4_K_M"`, `Options.ContextSize == 16384`, `Options.GpuLayers == "all"`;
- `ProfileFor` with a Partial fit yields `GpuLayers == "<N>"`; with `q8_0` it sets `CacheTypeK == CacheTypeV == "q8_0"`; a repo name containing a comma has the comma removed.

It saves `discover.png`. Call it from `Program.cs` after `ProfileTransferSmokeChecks`.

- [ ] **Step 2: Build and run, and check it fails**

Run: `dotnet build LLamaModelLoader.slnx -c Release -m:1`. Expected: a compile error (`DiscoverView` missing).

- [ ] **Step 3: Implement**

`DiscoverView` layout:
- Code-built, with the `Text`/`Stack`/`Row`/`Card` helpers copied from `ServerInstallationsWindow`.
- Search row; results `ListBox`; **Load more** button.
- Details card holding a context `ComboBox` from `FitEstimator.ContextChoices`, a KV-type `ComboBox` (`f16`, `q8_0`, `q4_0`), the GPU line and the manual VRAM `TextBox`.
- Quantization rows, each with a badge `Border`: green `#2E7D5B`, amber `#9A6B12`, red `#8E3B3B`, grey `#4A5568`. Badge texts are `Fits in VRAM`, `Partial offload · {GpuLayers}/{Layers} layers`, `Does not fit` and `Unknown`, with a `≈ ` prefix when approximate and a ` · size-only estimate` suffix when `!ContextAware`. Each row's button reads `Download`, `Resume` (Partial) or `Downloaded` (disabled), from `ModelDownloader.GetState`; a Conflict state disables it and shows the conflict text.
- Breakdown line: `weights {w:N1} + KV {kv:N1} + buffers {c:N1} + reserve 1.0 = {t:N1} GiB of {vram:N1}`.
- Gated repos: no Download buttons; `Open on Hugging Face` opens `https://huggingface.co/{id}` with `UseShellExecute`.
- Error text: other `HttpRequestException`s and timeouts → `Hugging Face is unavailable. Check the connection and try again.`; rate-limit and `InvalidDataException` messages are shown as thrown. Results already listed are kept.
- The queue card at the bottom.

Behaviour:
- One `CancellationTokenSource` for the page, cancelled on detach.
- Details load: `GetDetailsAsync`, then `RemoteGguf.ReadKvShapeAsync` on the smallest quantization; a failure sets the shape to null and shows a note.
- Re-estimating is local only.
- `Downloads.Changed` is posted through `Dispatcher.UIThread.Post`.

`MainWindow` changes:
- Nav button `⇩  Discover` between Models and Chat, with `DiscoverClick` → `NavigateAsync(() => SetPage(new DiscoverView(_vm, p => { Editor(p); _dirty = true; })))`.
- `ExitClick`: when `_vm.Downloads.IsActive`, confirm with `"A model download is in progress. Exit and pause it? The partial file is kept for resuming."`.

- [ ] **Step 4: Run the smoke checks and the unit suite**

```bash
dotnet build LLamaModelLoader.slnx -c Release -m:1
dotnet run --project tests/SmokeChecks -c Release --no-build -- render artifacts/screenshots
dotnet test tests/LLamaModelLoader.Tests -c Release -m:1
```

Expected: `UI: discover search, fit badges, download and create profile passed` printed, every prior UI line still printed, and all tests pass.

- [ ] **Step 5: Commit** (stage only this task's hunks; see the Global Constraints precondition)

```bash
git add src/LLamaModelLoader.Desktop/DiscoverView.cs src/LLamaModelLoader.Desktop/MainViewModel.cs src/LLamaModelLoader.Desktop/MainWindow.axaml src/LLamaModelLoader.Desktop/MainWindow.axaml.cs tests/SmokeChecks/DiscoverSmokeChecks.cs tests/SmokeChecks/Program.cs
git commit -m "Add Discover page for Hugging Face search, fit badges and downloads"
```

---

### Task 9: Documentation and screenshot

**Files:**
- Modify: `README.md`
- Create: `docs/images/discover.png` (copied from `artifacts/screenshots/discover.png`)

- [ ] **Step 1: Update the README**

- Add a **Features** bullet: "**Discover models:** search GGUF models on Hugging Face, see whether each quantization fits your GPU for a chosen context, and download with resume and SHA-256 verification."
- Add a `## Discover and download models` section after "Install and update llama.cpp". It covers:
  - search and sorting;
  - what the four badges mean and that they are estimates (formula summary, the reserve, the `≈` marker, size-only fallback);
  - manual VRAM;
  - the download location `{Models}\owner\repo\…`, `.part` resume, SHA-256, free-space check;
  - one-at-a-time queue; exit pauses;
  - Create profile;
  - public repositories only.
  It ends with a collapsed screenshot block in the existing `<details>` style.
- In **Current scope**, replace "Model downloads, LAN hosting, and multiple simultaneous servers are not implemented." with "LAN hosting and multiple simultaneous servers are not implemented. Model downloads support public Hugging Face GGUF repositories only."

- [ ] **Step 2: Verify**

Run: `dotnet run --project tests/SmokeChecks -c Release --no-build -- render artifacts/screenshots`, copy `discover.png` to `docs/images/`, and open it to confirm it shows the badges.

- [ ] **Step 3: Commit**

```bash
git add README.md docs/images/discover.png
git commit -m "Document Hugging Face discovery and downloads"
```
