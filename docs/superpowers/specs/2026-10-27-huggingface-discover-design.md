# Hugging Face model discovery and download — design

Date: 2026-10-27 · Status: approved in conversation, pending written-spec review

## Goal

Let the user search GGUF models on huggingface.co from inside the application, see **before downloading** whether each
quantization fits their GPU (colour-coded, like the hardware-compatibility hints on huggingface.co but context-aware),
download the chosen quantization into the configured models folder, and then create a profile for it.

### Decisions taken with the user

| Topic | Decision |
| --- | --- |
| Fit estimate | Accurate: weights + KV cache for a chosen context + compute buffers, using the remote GGUF header |
| After download | Offer **Create profile**: open the existing profile editor prefilled; the user saves |
| Gated / private models | Not supported. Public repositories only; no token is stored |
| Placement | New **Discover** page in the sidebar; downloads owned by `MainViewModel` so they survive navigation |

### Out of scope

Gated/private repositories and tokens; multimodal projectors (`mmproj`) — the application already rejects them as
models; parallel downloads; non-GGUF formats; automatic profile creation without the editor; non-NVIDIA VRAM detection
(manual VRAM entry covers it).

## Verified Hugging Face behaviour (2026-10-27)

- `GET https://huggingface.co/api/models?search=<q>&filter=gguf&sort=<downloads|likes|lastModified|trendingScore>&limit=30`
  returns `id`, `author`, `downloads`, `likes`, `lastModified`, `gated`, `pipeline_tag`, `tags`. Pagination uses the
  `Link: <...>; rel="next"` response header.
- `GET https://huggingface.co/api/models/<owner>/<repo>?blobs=true&expand[]=gguf&expand[]=siblings&expand[]=gated&expand[]=sha&expand[]=cardData`
  returns `sha` (commit), `gated`, `gguf { total, architecture, context_length }` and `siblings[]` with
  `rfilename`, `size`, `lfs { sha256, size }`.
- `GET https://huggingface.co/<repo>/resolve/<rev>/<path>` returns `302` to a CDN that honours `Range` (`206 Partial
  Content`, `Content-Range: bytes a-b/total`).

## Architecture

```
Desktop   DiscoverView ──► MainViewModel.Downloads (DownloadQueue) ──► Editor(prefilled)
              │                         │
Infra     HuggingFaceClient      ModelDownloader      RemoteGguf (HttpRangeStream + GgufReader)   GpuInfo (nvidia-smi)
              │                         │                      │
Core      HfModelSummary / HfModelDetails / HfQuantization      GgufKvShape      FitEstimator / FitResult
```

### Core (pure, no I/O)

**`HuggingFace.cs`**
- `record HfModelSummary(string Id, string Author, long Downloads, long Likes, DateTimeOffset? LastModified, bool Gated, string? PipelineTag, IReadOnlyList<string> Tags)`.
- `record HfFile(string Path, long Size, string Sha256)`.
- `record HfQuantization(string Label, IReadOnlyList<HfFile> Parts)` with `TotalBytes` and `FirstPath`. `Label` is the
  quantization type parsed from the file name (`Q4_K_M`, `IQ4_XS`, `BF16`, …), falling back to the file stem.
- `record HfModelDetails(string Id, string Sha, bool Gated, string? Architecture, ulong? Parameters, ulong? ContextLength, string? License, IReadOnlyList<HfQuantization> Quantizations)`.
- `static HfNames`: `IsValidRepoId` (`^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$`, no `..`),
  `IsSafeRepoPath` (relative, `/`-separated, each segment passes the same unsafe-name rules as `ServerInstaller.UnsafePart`,
  ends with `.gguf`), and `GroupQuantizations(IEnumerable<HfFile>)` which groups `name-0000k-of-0000n.gguf` shards
  (in any subfolder) into one quantization, drops incomplete shard sets and any path containing `mmproj`, and sorts by
  total size.

**`FitEstimator.cs`**
- `record GgufKvShape(string Architecture, int Layers, IReadOnlyList<int> HeadCountKv, int KeyLength, int ValueLength, ulong? VocabularySize, bool Approximate, string? ApproximationReason)`.
  `HeadCountKv` has one entry per layer (a scalar is expanded); layers with 0 KV heads (recurrent/SSM) contribute nothing.
- `record FitInput(long WeightBytes, GgufKvShape? Shape, int ContextSize, string KvType, long? VramBytes, long? RamBytes)`.
- `enum FitLevel { Unknown, Fits, Partial, DoesNotFit }`.
- `record FitResult(FitLevel Level, long WeightBytes, long? KvBytes, long ComputeBytes, long ReserveBytes, long TotalBytes, int? GpuLayers, bool ContextAware, bool Approximate)`.
- Formula:
  - `KvBytes = ContextSize × Σ_l HeadCountKv[l] × (KeyLength + ValueLength) × bytesPerElement(KvType)` with
    `f16`/`bf16` = 2, `q8_0` = 34/32, `q4_0` = 18/32, `q4_1` = 20/32, `q5_0` = 22/32, `q5_1` = 24/32, `f32` = 4.
  - `ComputeBytes = (VocabularySize ?? 32000) × 512 × 4 + 512 MiB`.
  - `ReserveBytes = 1024 MiB`.
  - `GpuNeed = Weights + Kv + Compute + Reserve`.
  - `Fits` if `GpuNeed ≤ Vram`. `Partial` if not, but `Weights + Kv + Compute ≤ Vram + max(0, Ram − 8 GiB)`; then
    `GpuLayers = clamp(floor((Vram − Reserve − Compute) / ((Weights + Kv) / Layers)), 0, Layers − 1)` (each layer's KV
    cache is allocated on the same device as the layer, so a GPU layer costs its weights plus its KV). `DoesNotFit` otherwise.
    `Unknown` when `VramBytes` is null.
  - With no shape (header unreadable): `Kv = null`, `ContextAware = false`, `GpuLayers = null`; the same thresholds are
    applied to `Weights + Compute + Reserve`.
- `Approximate` is set for sliding-window architectures (`*.attention.sliding_window` present: the full cache is an
  upper bound), for MLA (`*.attention.kv_lora_rank` present: when `*.rope.dimension_count` is also present, KV is
  `ContextSize × Layers × (kv_lora_rank + rope_dimension_count) × bytesPerElement`; otherwise the generic formula, an
  upper bound), and for hybrid/recurrent models (`*.ssm.*` present). `GgufKvShape` therefore also carries
  `int? KvLoraRank` and `int? RopeDimension`.

### Infrastructure

**`HuggingFaceClient(HttpClient http)`**
- `SearchAsync(query, sort, string? nextPageUrl, token)` → `(IReadOnlyList<HfModelSummary> Models, string? NextPageUrl)`.
  The next-page URL must be an `https://huggingface.co/api/models?` URL; anything else is ignored.
- `GetDetailsAsync(repoId, token)` → `HfModelDetails`. Uses `blobs=true` and the `sha` commit.
- Static `ParseSearch(json)` / `ParseDetails(json)` for tests. Responses are buffered with an 8 MiB limit (same as
  `ServerReleaseClient`). Malformed entries are skipped; files without `lfs.sha256` or with `size ≤ 0` are not offered.
- 429/403 → `HttpRequestException("Hugging Face refused the request (rate limit). Try again later.")`. Timeouts: 45 s
  per call, linked to the caller's token. User-Agent `LLamaModelLoader/1.0`.

**`HttpRangeStream`** (read-only, seekable `Stream`)
- `static OpenAsync(HttpClient, Uri, long maxBytes, token)` probes `Range: bytes=0-0` to learn the total length from
  `Content-Range`; fails if the server does not answer `206`.
- Reads fetch aligned 1 MiB blocks on demand, cache the last few blocks, and throw `InvalidDataException` beyond
  `maxBytes` (64 MiB). Forward `Seek` is free (no fetch until the next read).

**`GgufReader` refactor**
- `HeaderReader` takes `Stream` instead of `FileStream`. Local behaviour and limits are unchanged.
- New `GgufReader.ReadKvShapeAsync(Stream, token)` that reads only the metadata section (not tensor descriptors) of the
  first shard and returns `GgufKvShape`. Additionally retained keys: `<arch>.attention.head_count`,
  `<arch>.attention.head_count_kv` (scalar or per-layer `uint/int` array — arrays up to 4096 elements are kept),
  `<arch>.attention.key_length`, `<arch>.attention.value_length`, `<arch>.embedding_length`,
  `<arch>.attention.sliding_window`, `<arch>.attention.kv_lora_rank`, `<arch>.rope.dimension_count`, any
  `<arch>.ssm.*` presence, and the token count.
  Missing `key_length`/`value_length` fall back to `embedding_length / head_count`; missing `head_count_kv` falls back
  to `head_count`.
- `RemoteGguf.ReadKvShapeAsync(HttpClient, repoId, sha, firstPath, token)` combines the two. Called once per opened
  repository (the KV shape is identical for every quantization of a repo), using the smallest quantization's first part.

**`GpuInfo`**
- Reuses `StatisticsClient.ReadGpusAsync`. Returns the largest device's `memory.total` and `memory.used`, plus total
  physical RAM from `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`. Estimates use total VRAM; used VRAM is shown as
  information only.

**`ModelDownloader(string modelsDirectory, HttpClient http)`**
- `GetState(repoId, HfQuantization)` → `NotDownloaded | Partial(bytes) | Downloaded | Conflict` (a file exists at the
  destination with a different size).
- `DownloadAsync(repoId, sha, HfQuantization, IProgress<DownloadProgress>, token)` → path of the first part.
  - Destination `{models}\{owner}\{repo}\{path}`; each segment validated; the full path must stay under the models
    folder; no reparse points anywhere in the path (same check as `ServerInstaller.CheckPath`).
  - URL `https://huggingface.co/{repo}/resolve/{sha}/{path}` (path segments URL-escaped). Redirects are followed by
    `HttpClient`; a redirect to a non-https scheme fails.
  - Free space: `remaining bytes + 256 MiB ≤ DriveInfo.AvailableFreeSpace`, otherwise `IOException` before any request.
  - Each part is written to `<file>.part`. If a `.part` exists, its bytes are hashed first, then the request uses
    `Range: bytes=<n>-`. `206` appends; `200` restarts from zero; a `.part` larger than the expected size is discarded.
  - Size and SHA-256 are verified; then `File.Move(part, final)`. Mismatch deletes the `.part` and throws
    `InvalidDataException("The downloaded file is corrupted (size or SHA-256 mismatch). Download it again.")`.
  - Progress is reported at most every 150 ms with bytes done/total, current part, and speed.
  - Already-complete parts with the correct size are skipped (not re-hashed).
- `DeletePartial(repoId, HfQuantization)` removes only that quantization's `.part` files.

### Desktop

**`DownloadQueue`** (owned by `MainViewModel`, observable)
- Items: repo, quantization, state (`Queued | Downloading | Paused | Failed | Completed`), progress, speed, ETA,
  message, final path, and the `FitResult`/context used when it was queued.
- One download at a time. Cancel pauses the item and keeps the `.part`; **Resume** re-queues; **Remove** deletes the
  `.part` after confirmation. Exit while downloading asks for confirmation; app exit cancels and awaits the active
  download (the `.part` remains).
- On completion: `Notice = "Downloaded <repo> <quant>."` and the item shows **Create profile**.

**`DiscoverView`** (new sidebar entry **⇩ Discover** between Models and Chat)
- Search box (Enter or **Search**), sort selector, results list with **Load more**. Each row: `owner/repo`,
  downloads, likes, updated date, `gated` / `vision` badges. Gated rows are disabled for download and offer
  **Open on Hugging Face**.
- Details panel for the selected repo: architecture, parameters, max context, licence; **Context** selector
  (4096, 8192, 16384, 32768, 65536, 131072, model max — capped at the model max, default `min(16384, max)`);
  **KV cache type** (f16, q8_0, q4_0); GPU line `NVIDIA GeForce RTX 5080 · 16.0 GiB (2.1 GiB in use)` and a
  **Manual VRAM (GiB)** field that overrides detection.
- Quantization table: label, size, fit badge (green **Fits in VRAM**, amber **Partial offload · N layers**, red
  **Does not fit**, grey **Unknown**), breakdown `weights 4.7 + KV 2.3 + buffers 0.8 + reserve 1.0 = 8.8 GiB of 16.0`,
  `≈` marker when approximate, “size-only estimate” while the header is loading or if it failed, and a
  **Download** / **Downloaded** / **Resume** button. Changing context or KV type recomputes locally.
- Download queue card at the bottom of the page.
- **Create profile** navigates to the existing `Editor` with a new `ModelProfile`: `Name = "<repo>-<quant>"` (commas
  removed), `ModelPath` = first part, `ContextSize` = selected context, `CacheTypeK/V` = selected type when not f16,
  `GpuLayers` = `all` for green or `N` for amber. The editor is marked dirty so leaving prompts.
- Without a configured models folder, Download is disabled with “Set a models folder in Settings first.”

## Error handling

| Situation | Behaviour |
| --- | --- |
| Network failure / timeout | Inline status: “Hugging Face is unavailable. Check the connection and try again.” |
| 429 / 403 | Rate-limit message; existing results stay visible |
| Malformed JSON entries | Skipped; a fully malformed response shows “Unrecognized response from Hugging Face.” |
| GGUF header unreadable / over 64 MiB / no 206 | Size-only estimate with an explanatory note; download still allowed |
| Interrupted download | Item becomes **Paused**; `.part` kept for resume |
| SHA-256 / size mismatch | `.part` deleted; item **Failed** with the corruption message |
| Insufficient disk space | Item **Failed** before any request, showing required and available space |
| Destination conflict | Download disabled; “A different file already exists at <path>.” |

## Testing

All tests run without network or GPU, using a fake `HttpMessageHandler`.

- `HuggingFaceTests`: search/details fixtures; `Link` pagination; split shards in subfolders; incomplete shard sets;
  `mmproj` exclusion; gated flag; quantization label parsing; repo-id/path validation (including `..`, `CON`, absolute
  paths, backslashes, non-https next-page URLs).
- `FitEstimatorTests`: Fits/Partial/DoesNotFit boundaries; per-layer `head_count_kv` with zero layers; GQA; KV types;
  partial-offload layer count; no-shape fallback; Unknown without VRAM.
- `RemoteGgufTests`: a synthetic GGUF served through ranged responses; block caching; 64 MiB limit; missing 206.
- `ModelDownloaderTests`: success; resume with 206; restart on 200; oversize `.part`; size mismatch; SHA mismatch;
  cancellation keeps `.part`; insufficient space; path escape attempts; already-downloaded skip.
- `SmokeChecks`: render Discover with fixture data (results, a selected repo, all four badges, a queue item) and save
  `docs/images/discover.png`.
- README: new **Discover models** section; remove “Model downloads … are not implemented” from Current scope.
