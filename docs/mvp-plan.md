# LLamaModelLoader MVP plan

A .NET 10 and Avalonia application for selecting local GGUF models, configuring their launch, and managing one `llama-server.exe` process. Main workflow: specify the server and models folder, create a profile, click Start, wait for API readiness, then stop or restart the server.

Plan dated October 9, 2026. LLamaModelLoader is a working name. The initial target is Windows x64. In the MVP, loading a model means loading local weights into llama.cpp. Downloading files is a separate later phase. This document records the original design; subsequent features and verification are described in README.md and verification.md.

## Implementation status — October 10, 2026

The original MVP is implemented. Later additions include server statistics and NVIDIA telemetry, a cumulative session generation rate, speculative/MTP fields, API aliases from profile names, and a startup memory breakdown.

GGUF v2/v3 header inspection is now available on Home and in the profile editor, including split files, tensor-type counts, stored tensor element counts, tokenizer information, and chat templates. It does not load weights or replace llama.cpp validation.

The Optimize window now supports a bounded automatic candidate search and comparison of saved profiles for the same model. Context and slot count stay fixed; KV precision and MTP changes require opt-in. A native streaming benchmark ranks completed measurements, records sampled memory, restores the previous running configuration, and saves chosen results as new profiles. This is the first tuning iteration; combining winning groups, broader search, and quality evaluation remain future work.

Settings now includes a llama.cpp installation manager for official Windows x64 CPU, Vulkan, and CUDA builds. It checks recent releases, pairs CUDA runtime archives, verifies downloads, probes staged executables, and keeps installations side by side. Users explicitly select a build, save Settings, and restart when ready. Background updates, automatic backend selection, driver installation, and old-build cleanup remain outside this iteration.

The sections below retain the original design for context. Current usage and limitations are documented in [README](../README.md); executed checks are recorded in [verification](verification.md).

## MVP scope

Include application settings, a local model catalog, profile creation/editing, server controls, readiness checks, logs, tray integration, and startup options. Only one managed process runs at a time. One GGUF file can have multiple profiles, such as Work 8K and Large context 32K.

After the MVP: resumable Hugging Face downloads, llama.cpp installation/updates, concurrent servers, request routing, GPU/VRAM monitoring, integrated chat, automatic performance tuning, and full GGUF metadata reading. Multimodal, embedding, and reranking profiles belong in later versions. Rare options can be supplied as additional arguments without promising complete UI support for those workflows.

## Application settings

| Setting | Behavior |
| --- | --- |
| llama-server.exe path | File picker, existence check, version/capability check button |
| Models folder | Folder picker, recursive GGUF discovery, manual refresh |
| Start application at Windows sign-in | Independent switch; initially off |
| Automatically start selected model | Independent switch; initially off |
| Start minimized | Minimize to tray; show the window if no tray is available |
| Server address | Fixed loopback 127.0.0.1 for the MVP |
| Server port | Editable application default 8080; always passed explicitly |
| Loading timeout | Proposed default 300 seconds; editable, with elapsed time shown |

Autostart is represented by two independent switches. They serve different purposes and must not enable each other. Invalid configuration cancels model autostart and shows an error. On the first launch, settings must remain accessible even if an imported configuration requests minimization.

Save validates settings. Changes to the executable or port while running are saved for the next launch; Home indicates that a restart is needed. The catalog folder is used for discovery and does not alter saved absolute profile paths.

## Home screen

Sidebar navigation: Home, Models, Settings. The server log opens as a panel on Home.

Home displays the selected profile, name/description, GGUF filename, disk size, configured context, GPU layers, server state, and API address. File size must not be labeled as required RAM or VRAM. Show architecture/quantization only when known from saved information; do not present filename guesses as verified metadata.

Primary actions: Start, Stop, Restart. Additional actions: Edit profile, Copy API address, Open Web UI, Show log. The API address and Web UI become available after readiness checks. If Web UI is disabled, hide or disable its action.

| State | UI and available actions |
| --- | --- |
| Stopped | Profile selection, Start with valid settings |
| Starting | Indeterminate progress, elapsed loading time, log, Stop |
| Ready | Stop, Restart, API address, uptime |
| Stopping | Progress and log; repeated commands blocked |
| Failed | Reason, exit code if available, log, retry Start after process exit |

Changing the selected profile while a process is alive is blocked: Stop, select, then Start. Restart uses the latest saved settings. Failed validation must not stop a healthy process. If the replacement fails to load, show the failure without a hidden rollback.

Editing an active profile is allowed, but changes require restart. Keep a runtime settings snapshot so the UI distinguishes saved settings from the active process configuration.

## Model list and adding models

Models lists saved profiles with name, file, size, description, file availability, and selected/running indicators. Actions: add, edit, duplicate, delete. Search by name and path.

1. Select a GGUF from the catalog or use a system file picker. Files outside the catalog are allowed; copying is unnecessary.
2. Set a name, optional description, and launch options.
3. Review validation messages and the command preview, then save.

Discovered files and saved profiles are separate entities. Scanning does not create profiles. An inaccessible subfolder must not abort the whole scan. Scan asynchronously with cancellation and avoid recursively following junctions/symlinks.

Display a standard split GGUF set as one model. Save the first shard path, check the other parts, and sum the set's size. Do not launch incomplete sets. Obvious mmproj files are auxiliary and must not be offered as ordinary text models; the server performs final compatibility checks.

Delete removes only the profile, preserving weights. An active profile cannot be deleted until stopped. Deleting the selected profile clears selection and prevents model autostart until another selection is made.

## Profile editor

Use a separate page with sections and pinned Save/Cancel buttons. Borrow parameter grouping and command preview organization from LlamaDeck: `LlamaSwapManager.Desktop/Views/MainWindow.axaml`, editor around line 1222, and `LlamaSwapManager/ViewModels/ModelEditItem.cs`.

Store typed fields; the command is a derived output, not the primary storage format. Every optional value supports Server default, which omits the argument. This differs from explicit `auto`, `0`, or `off`.

| Section | MVP fields | llama-server arguments |
| --- | --- | --- |
| General | Name, description, file, context, GPU layers | `--model`, `--ctx-size`, `--gpu-layers` |
| Performance | CPU threads, prompt threads, logical batch, physical microbatch | `--threads`, `--threads-batch`, `--batch-size`, `--ubatch-size` |
| Memory | Flash Attention, K/V cache, memory loading, automatic fit | `--flash-attn`, `--cache-type-k`, `--cache-type-v`, `--load-mode`, `--fit` |
| Generation | Temperature, top-k/p, min-p, repeat/presence/frequency penalties, seed, generation limit | `--temp`, `--top-k`, `--top-p`, `--min-p`, `--repeat-penalty`, `--presence-penalty`, `--frequency-penalty`, `--seed`, `--predict` |
| Chat | Model template by default, optional override, Jinja, supported reasoning mode | `--chat-template`, `--jinja` / `--no-jinja`, `--reasoning` |
| Advanced | Parallel requests, additional arguments, preview | `--parallel`, other supported arguments |

Flags and value semantics were checked against the [official argument parser](https://github.com/ggml-org/llama.cpp/blob/master/common/arg.cpp). Details and future UI candidates are in the [llama-server research](llama-server-research.md).

Initially expose name, path, context, and GPU layers; expand other sections as needed. Each field needs a short explanation, units, CLI name, and a way to reset to the server default. Do not impose one sampling preset on all models.

The reviewed `--gpu-layers` accepts a number, `auto`, or `all`; Flash Attention accepts `auto`, `on`, or `off`. Memory loading uses `--load-mode`; do not copy old mmap/mlock examples from LlamaDeck without checking. Explicit context `0` can interact differently with fit than an omitted option. Available values depend on the build; its own `--help` is authoritative.

New profiles explicitly use `--parallel 1` for a predictable initial workflow. Users may change or clear it. Generation options define server defaults; clients can override supported options per request. In this MVP, changing these defaults also requires restart.

Edit additional arguments as individual tokens so paths and JSON do not require a custom command-line parser. Reject duplicates of UI-managed flags and their short aliases. Users must not bypass `--model`, `--host`, or `--port`, enable router mode, or add alternative model sources through this list. Preview is for reading/copying; launch uses the original token array.

Later editor extensions: devices and multi-GPU (`--device`, `--split-mode`, `--tensor-split`, `--main-gpu`), mmproj, advanced samplers, reasoning budget/format, embeddings, and reranking. Complex features need build/backend/model compatibility checks.

## llama.cpp compatibility

Read `--version` and `--help` with time/output limits when selecting the executable. Cache by path, size, and modification time; recheck after an update. If probing fails, allow saving the path but block launch with diagnostics.

Maintain a small explicit set of known flags and validators. Use help to discover support, not to generate the whole form. An unrecognized help format must not count as successful validation. Reject explicitly configured unsupported options with a suggestion to reset them; never silently discard settings.

Only show a server default when reliably determined for that build; otherwise say the server chooses it. Log the version, applied arguments, and configuration snapshot for reproducibility. Record a specific tested build before release: current master is a reference, not a guarantee for every executable.

## Process management

`ServerController` serializes Start, Stop, and Restart. Repeated Start must not create another process. Associate cancellation and exit events with the session so an old health request cannot affect a new launch.

Before launch, check the executable, model/all shards, argument validity, and port availability. Port preflight is advisory: the real bind can still fail and must appear as a launch error.

Launch directly without cmd.exe or PowerShell. The original proposal used `UseShellExecute=false` and `ProcessStartInfo.ArgumentList`, with the executable directory as working directory and concurrent asynchronous stdout/stderr reads. .NET provides argument escaping through [ArgumentList](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.argumentlist?view=net-10.0). The final Windows implementation uses native suspended process creation so Job Object assignment occurs before execution.

Remove inherited `LLAMA_ARG_*` only from the child environment to prevent silent profile overrides. Show this policy in advanced diagnostics. Do not change the user's system environment. Preserve other backend variables. Apply the same policy to help/version probes.

Poll `GET /health` after launch. HTTP 503 and connection failures before the port opens are expected during loading. HTTP 200 with the expected body means ready, provided the owned child/session is still alive. A response from an unrelated server must not count as success. Recheck process and listener ownership before Ready. See [health semantics](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md#health-get-health).

On loading timeout, stop the owned process and show an error with guidance to increase the timeout. If API availability is lost after Ready, distinguish it from process failure; do not launch duplicate servers or restart indefinitely.

The first Windows experiment should verify console control events for a hidden console process. If graceful shutdown works with the chosen launch mechanism, allow a short grace period before terminating the owned tree. `CloseMainWindow` is not a solution for console llama-server. The MVP guarantees process cleanup on Stop; active HTTP requests may be interrupted.

Assign the child to a Windows Job Object with kill-on-last-handle-close so a GUI crash does not orphan it. Cover the interval between creation and assignment; failed assignment must abort startup and clean up the process. See [Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects). Never kill unrelated processes by name or occupied port.

Logging: bounded UI buffer, size-limited rotating file, clear display, copy errors, and open log folder. Heavy output must not block the UI. Mask known secret arguments in logs and previews.

## Tray and shutdown

Tray menu: Open, Start, Stop, Restart, Exit; command availability matches Home. Closing the window hides it in the tray. Exit stops the owned server and closes the application; label it Exit and stop server. Without an available tray, closing stops and exits.

One instance per user: a second launch activates the existing window without repeating autostart. Implement Windows startup through a per-user service without administrator rights. Update registration only when the switch explicitly changes, using the actual published application path.

Avalonia supports `net10.0`, file dialogs, and the Windows system tray; pin a specific stable package version when creating the solution. See [Avalonia Windows documentation](https://docs.avaloniaui.net/docs/platform-specific-guides/windows/).

## Configuration storage

Use one `%LOCALAPPDATA%\LLamaModelLoader\config.json` for settings, selected profile ID, profiles, and schema version. One file permits atomic updates to profiles and selection together. Logs reside alongside it in `logs`; model weights remain in place.

Serialize with System.Text.Json to a temporary file in the same folder, flush, then replace the main file while retaining the last valid backup. A write failure preserves the previous configuration and unsaved form contents. Recover damaged configuration from backup with a visible message and preserve the original for diagnostics. Never overwrite an unknown newer schema.

Profile IDs are stable GUIDs; renaming does not change selection. Optional numbers and switches are nullable; special modes use enums/typed variants. `null` means omit the option. Serialize JSON/CLI numbers with invariant culture; UI input may accept local numeric formats.

Proposed data types: `AppSettings`, `ModelProfile`, nested `LlamaServerOptions`, `ServerSessionSnapshot`. Profile fields: Id, Name, Description, ModelPath, Options, ExtraArguments. Recompute file size and availability rather than treating them as permanent profile attributes.

## Solution structure

| Project | Responsibility |
| --- | --- |
| LLamaModelLoader.Core | Profile/settings types, validation, arguments, server contracts |
| LLamaModelLoader.Infrastructure | JSON storage, file catalog, executable probes, processes, HTTP health, Windows startup, Job Objects |
| LLamaModelLoader.Desktop | Avalonia, FluentTheme, MVVM, pages, editor, tray, composition root |
| LLamaModelLoader.Tests | Storage, arguments, states, and process checks using a controlled test executable |

Desktop uses Core and Infrastructure; Infrastructure depends on Core. Core has no Avalonia or Windows API dependency. Use CommunityToolkit.Mvvm and CancellationToken for asynchronous operations. Avoid a database, plugins, or a universal settings framework in the first version.

## Implementation phases

| Phase | Work | Verifiable result |
| --- | --- | --- |
| 1. Technical foundation | net10.0 solution, Avalonia, pinned versions; launch/health/stop/Job Object experiment | A small real GGUF loads, stops, and does not remain after exit |
| 2. Settings and storage | Executable/folder/port settings, JSON schema, executable checks | Settings survive restart; damaged files are preserved |
| 3. Catalog and profiles | GGUF discovery, CRUD, duplication, basic/advanced editor, preview | Two profiles for one file independently retain different options |
| 4. Main workflow | Controller, states, Start/Stop/Restart, health, runtime snapshot, logs | Complete lifecycle without UI hangs or duplicate processes |
| 5. Background operation | Tray, single instance, two startup switches, minimization | Verify Windows sign-in, reopening, autostart failure, explicit exit |
| 6. MVP release | Critical tests, manual real CPU/GPU build check, win-x64 publish | Self-contained ZIP and instructions; user supplies llama.cpp and weights |

Start with phase 1: the main technical risk is the Windows console-server lifecycle. Estimate the remaining schedule after validating it rather than assuming process behavior.

## Acceptance criteria

- With a clean configuration, select an executable/model, create a profile, and obtain a ready API.
- At least two saved profiles survive application exit.
- Catalog selection and external paths both support spaces and Unicode.
- Start does not duplicate processes; Stop works during loading/generation; Restart waits for the previous process to exit.
- Port conflicts, missing models, incomplete split GGUF, unknown arguments, and memory failures are visible in logs/UI. Do not invent an out-of-memory explanation unless the server reports it.
- Hiding to tray preserves operation; explicit exit or GUI crash does not leave an owned server behind.
- Corrupt configuration, write failures, and unsupported schemas do not silently lose profiles.
- Unit/integration checks cover storage/recovery, nullable/auto/0, numeric cultures, paths, conflicting arguments, command order, cancellation, and stale health responses. The fixture simulates delayed readiness, heavy stderr, and unexpected exit. Real llama-server receives a separate manual check.

## Initial decisions

Windows x64, one process, local GGUF files, one shared executable/port, two independent autostart switches, tray minimization, and profile-only deletion. The original UI language was Russian; the current application and documentation use English following the user's translation request. These decisions permit implementation without waiting for future download, routing, and multi-server designs.
