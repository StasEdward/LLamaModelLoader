# LLamaModelLoader MVP verification

Verified on October 9, 2026, on Windows x64 with .NET SDK 10.0.401.

## Automated checks

The solution build and self-contained Windows x64 publish complete without warnings. Unit/integration tests cover nullable defaults versus explicit zero, numbers under a Russian locale, conflicting CLI arguments, JSON backup/recovery, unknown schemas, split GGUF files, startup/readiness, duplicate process prevention, restart, stop during loading, load failure, timeout, occupied ports, and process termination when the Job Object closes.

The headless Avalonia check renders Home, Models, the editor, and Settings, saves PNG files in `artifacts/screenshots`, and checks duplication, profile selection, and saving a changed context. Late events from a detached editor do not mark the following page as unsaved.

## Real llama.cpp

Executable: `D:\llama_cpp\llama-server.exe`, version `0.6.0-dev`, build 11526, commit `5e4878e97`. Model: `D:\llama_cpp\Models\Qwen3.5-9B-Q8_0.gguf`, 9,527,502,048 bytes. Options: context 4096, GPU layers `auto`, parallel 1, loopback port 18089.

The cycle passed: start → `/health` ready → `/v1/chat/completions` HTTP 200 → restart → ready → stop. Loading took approximately four seconds in this environment. The API response is saved in `artifacts/real-smoke/completion.json`, with the log alongside it. This verifies functionality; it is not a comparative benchmark.

Process exit notification can precede TCP listener release. The controller waits for the entire Job Object and port release before launching a replacement. A regression test follows Start → Ready → Restart → Ready → Stop.

## Statistics window — October 9, 2026

The Release build completed without warnings; 22 tests passed. Added checks cover Prometheus parsing (locale, exponents, labels, NaN/Inf), different `/slots` structures, multiple GPUs and N/A readings, HTTP 501, malformed responses, and HTTP cancellation on close. The integration test reads metrics/slots from the test server and Job Object resources, checks automatic `--metrics` without duplication, and rejects previous-session resource samples after restart.

Headless UI renders populated and unavailable states to `artifacts/screenshots-statistics/statistics.png` and `statistics-unavailable.png`; both images were visually checked. Values in the populated screenshot are test data. Opening statistics from the main window and reopening the same window were checked.

A read-only `SmokeChecks observe http://127.0.0.1:8080` check confirmed real slot information (context 131072) and RTX 5080 readings through NVIDIA SMI. `/metrics` returned 501 because the active server had been started by the earlier application without the flag. The active server and profile were not changed; the real model was not loaded again. Full metrics were tested on the fixture server; enabling them on the real model requires its next launch from the new build.

## Speculative decoding settings — October 9, 2026

The Release build completed without errors or warnings; 31 tests passed. Checks cover exact arguments `draft-mtp`, `2`, `0.6`, GPU layers `99`, a decimal comma, ranges, omitted flags for empty fields, and rejection of unsupported flags. Legacy additional arguments (including `=` and `--draft-p-min`) are imported without duplication; conflicts remain available for correction. Schema 1 upgrades to schema 2 with a backup.

The headless UI check enters values through the new fields, saves the profile, and rereads JSON. `artifacts/screenshots-speculative/speculative-editor.png` was visually checked. The installed executable's `--help` confirmed the flags and `draft-mtp` method. Real MTP generation was not run; compatibility with these weights and any speedup remain unverified. The running server and user configuration were not changed.

## Alias from the profile name

Release build and self-contained publish completed without warnings; 35 tests passed. The integration test passes a name containing spaces, Unicode, and quotes through an actual Windows test-server process and reads it from `/v1/models`. It then changes the name: the API retains the old name until restart and returns the new name afterward. Tests cover alias override prevention and rejection of commas/control characters. `--alias` support was confirmed using the installed executable's help and upstream documentation. The user's model was not restarted; HTTP name checks used the test server.

## English translation

Application-owned labels, hints, dialogs, tray items, validation messages, status text, and default profile text are in English. Source comments, test descriptions/fixtures, and project documentation were also checked. Unicode process-path and alias coverage remains in the integration test using an explicit Unicode escape. Russian-locale numeric tests remain to verify culture-independent arguments. Existing user profiles are not translated or rewritten.

The Release build and standalone publish completed without warnings; all 35 tests passed. The headless UI check passed with English navigation and button labels, including profile selection and saving speculative settings. Home, Models, Settings, the speculative editor, and populated/unavailable statistics screenshots in `artifacts/screenshots-english` were visually inspected. A source/documentation scan found no remaining Cyrillic text. Historical artifacts and external process output were not rewritten.

## Session generation average

Release build completed without warnings; 41 tests passed. Tests cover cumulative token/time weighting, idle polls with a zero server rate, empty/new session counters, and invalid/nonfinite counters. The headless UI verifies the session-average label during generation, while idle, and when counters become unavailable. The idle screenshot in `artifacts/screenshots-session-average/statistics-idle.png` was visually checked.

A read-only check of the active server returned 38,705 generated tokens, 575.333 seconds of generation time, and a zero server rate. The counters yield approximately 67.3 tokens/s for the session. The active model was not restarted or sent an inference request.

## Loaded model memory

Release build completed without warnings; all 53 tests passed. Parser coverage includes hybrid/MTP allocation separation, multiple GPUs and shards, host mappings, recurrent state, multiple attention caches, repeated graph reservations, fit estimates, unavailable/malformed values, ANSI/JSONL logs, invariant numeric parsing, and session isolation. A process integration test verifies data survives 3,000 subsequent log lines and Clear, resets on restart, enables verbosity 4 once, and preserves explicit verbosity/disabled logging.

The headless UI ran against a local fixture process and checked actual controller-to-view bindings, clearing logs, restarting without memory output, and hiding/resetting memory on Stop. The populated and unavailable screenshots in `artifacts/screenshots-memory` were visually inspected. All memory numbers in these screenshots are fixture values, not measurements of the user's model.

The installed executable's help confirms `--log-verbosity` and trace level 4. Its existing verbosity-3 startup log omits native allocation details. The real server was not restarted, and the user's saved configuration was not changed. Validation against a complete real memory-allocation log awaits the next model launch. The parser reports recognized buffer allocations only; it does not claim to measure total device VRAM or resident host memory.

## Verification limits

Localhost checks ran outside the sandbox because it blocked socket connections and parts of the test runner's IPC. The final publish retrieved dependencies and NuGet audit data outside that restriction.

Windows sign-in with autostart enabled, physical tray interaction, tray restoration after restarting Explorer, and other machines have not been manually tested. Autostart is disabled in the current profile. There is no separate graceful HTTP request drain: stopping terminates the process tree.

GGUF selection checks are limited to file existence, standard shard names, and obvious mmproj filtering. llama.cpp validates weight contents and compatibility. Help output confirms flag availability; actual model loading verifies backend/cache value combinations.
