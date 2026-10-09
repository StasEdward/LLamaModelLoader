# llama-server: options and integration

Review date: October 9, 2026. Purpose: plan a Windows application using .NET 10 and Avalonia to manage a local `llama-server.exe`.

Official documentation and source code on `master` were reviewed. This branch changes over time and is not a contract for every installed build. Upstream facts and proposed application decisions are distinguished below. The local executable was not inspected as part of this original research.

## Options to expose in the editor

Names and semantics were checked against the [official common/arg.cpp parser](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/arg.cpp).

| Field | CLI | Meaning |
|---|---|---|
| Model | `--model` | GGUF path |
| API name | `--alias` | Model alias |
| Context | `--ctx-size` | Tokens; 0 uses the model value |
| CPU: generation | `--threads` | Thread count |
| CPU: prompt processing | `--threads-batch` | Thread count |
| GPU layers | `--gpu-layers` | Number / `auto` / `all` |
| Devices | `--device` | List; `none` disables offload |
| Device discovery | `--list-devices` | Lists available devices |
| Flash Attention | `--flash-attn` | `auto` / `on` / `off` |
| K/V cache | `--cache-type-k`, `--cache-type-v` | Types including `f16`, `q8_0`, `q4_0` |
| KV on GPU | `--kv-offload`, `--no-kv-offload` | Toggle |
| Batch | `--batch-size`, `--ubatch-size` | Logical / physical maximum |
| Memory fitting | `--fit` | `on` / `off`; adjusts unspecified settings |
| Multiple GPUs | `--split-mode`, `--tensor-split`, `--main-gpu` | Strategy, proportions, primary GPU |
| Memory loading | `--load-mode` | `auto`, `none`, `mmap`, `mlock`, `mmap+mlock`, `dio` |
| MoE | `--cpu-moe`, `--n-cpu-moe` | Experts on CPU |

An explicit `--ctx-size 0` prevents fit from reducing the context. Therefore, an omitted value differs from `0`. `--split-mode tensor` is experimental. See the [argument parser](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/arg.cpp).

Values in the reviewed [common/common.h](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/common.h): batch 2048, microbatch 512; sampling temperature 0.8, top-k 40, top-p 0.95, min-p 0.05, repeat penalty 1.0, repeat-last-n 64, presence/frequency penalty 0. These describe the source; server initialization and individual builds can change them.

Proposed sampling fields: `--temp`, `--top-k`, `--top-p`, `--min-p`, `--repeat-penalty`, `--repeat-last-n`, `--presence-penalty`, `--frequency-penalty`, `--seed`, `--n-predict`. Later additions: `--samplers`, DRY, Mirostat. See [common_params_sampling](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/common/common.h) and the [server reference](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md#sampling-params).

## Networking, chat, and readiness

In the reviewed documentation, `--host` defaults to `127.0.0.1` and `--port` to `9931`; `--parallel` sets the slot count (`-1` means auto). `--chat-template-file` overrides the template; otherwise model metadata is used. `--jinja` controls Jinja. `--reasoning` accepts on/off/auto; `--reasoning-format` controls representation and `--reasoning-budget` controls the budget. Support depends on the template. Sampling options set defaults that client requests can override. See the [server README](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).

`GET /health`: HTTP 503 indicates loading; HTTP 200 with `status: ok` indicates readiness. `GET /props` returns properties including `model_path`, `total_slots`, the template, generation defaults, and `is_sleeping`; reading it does not require `--props`. Health/props checks do not wake a sleeping model. Router mode supports multiple models and `/models/load`, `/models/unload`. See [server API and modes](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md).

## Multimodal support and files

A local multimodal profile needs a projector matching the model; a typical launch uses `--mmproj`. Upstream explicitly warns that multimodal support is actively evolving and can introduce breaking changes. See the [mtmd documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/mtmd/README.md).

Split GGUF loads from its first file: the loader checks metadata index 0, locates other parts, and validates their count and indices. See [llama-model-loader.cpp](https://raw.githubusercontent.com/ggml-org/llama.cpp/master/src/llama-model-loader.cpp). Proposal: display the set as one entry and check that all parts exist before launch; do not offer a second shard as an independent model. A projector should not automatically appear as an ordinary catalog model either.

GGUF defines `general.name`, `general.architecture`, `general.description`, `general.size_label`, `general.file_type`, architecture-specific `context_length`, and `tokenizer.chat_template`. Metadata can be incomplete; quantization version is distinct from quantization scheme. See the [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md). MVP proposal: user-entered name/description/type, path, and actual file size. Reading GGUF metadata is a separate improvement; do not invent properties based on filenames.

## Application decisions

These are design recommendations from the research, not llama.cpp requirements.

1. Treat the user-selected executable as authoritative. On change, read `--version`, `--help`, and `--list-devices` with timeouts; cache results by path, size, and modification time. Launch the file directly through process APIs, without a shell. Incomplete help parsing should produce a diagnostic, not imply universal support.
2. Maintain a limited verified option set. Use help for capability checks and hints, not to generate the entire editor: help is not a stable data schema.
3. Every profile option supports the server default by omitting its argument. Preserve explicit `0`, `false`, and `auto` separately. Do not persist all current defaults or convert `auto` to an arbitrary layer count.
4. Choose explicit MVP networking defaults: `127.0.0.1:8080`, one slot (`--parallel 1`). These are application defaults, not claims about llama.cpp defaults. The executable must confirm flag support. Configure large contexts deliberately; do not replace Auto with explicit `--ctx-size 0`.
5. Before launch, check executable/model/projector/part existence, numeric ranges, and port occupancy. Port checks are preliminary; process startup determines the final result.
6. Manage one process and one active model. Proposed states: Stopped → Starting → Loading → Running → Stopping, plus Failed with exit code and recent logs. Serialize Start/Stop/Restart and allow loading cancellation.
7. Read stdout/stderr asynchronously. Poll `/health` after launch; associate the endpoint with the owned process, a preflight port check, and the expected model in `/props` when available. HTTP 200 from another server is insufficient. Stop polling immediately on exit.
8. Bound the loading wait while supporting large models and a clear continue-waiting action. Do not invent progress percentages from logs. On timeout, show the reason, latest error, and a stop action.
9. Implement Stop in a Windows adapter: choose and verify a graceful console shutdown method, then use a timeout and terminate only the owned process/tree. Verify no orphan process remains after the GUI closes. Do not assume a universal HTTP shutdown endpoint exists.
10. Changes to a running profile require Restart. Save changes for the next launch and describe the active process using a separate settings snapshot. Client sampling can still differ from profile defaults.
11. Store expert arguments as a token array. Reject duplicates of managed options, show the final command, and detect conflicts. Do not pass a command string to cmd.exe or PowerShell. Control inherited `LLAMA_ARG_*` in the child environment or clearly expose their effects.
12. Keep router mode, simultaneous models, downloads, automatic VRAM tuning, LoRA/speculative/RoPE, embeddings/reranking, and detailed multimodal settings outside the first MVP. Keep profiles extensible.

## Checks on a real build before implementation

- Actual syntax/defaults for GPU, Flash Attention, memory loading, parallel, fit, and chat/reasoning.
- Backend behavior with K/V cache types and Flash Attention; having a GPU does not guarantee every combination works.
- Actual context capacity across multiple slots and KV modes. Do not promise a universal total-context/parallel formula for all builds.
- `/health` during loading/failure, `/props` contents, stop during loading, port conflicts, and termination when the GUI exits.
- Paths containing spaces/Unicode, missing DLLs/backends, incomplete split GGUF, and incompatible projectors.
