using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLamaModelLoader.Core;

namespace LLamaModelLoader.Infrastructure;

// One instance per process launch. Independent of the bounded, clearable UI log.
public sealed partial class MemoryLogParser
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(int Context, MemoryCategory Category, string Device), double> _buffers = [];
    private int _context;
    private bool _mtp;
    private bool _estimate;
    private bool _finished;
    private MemoryBreakdown _snapshot = MemoryBreakdown.Empty;
    public MemoryBreakdown Snapshot { get { lock (_gate) return _snapshot; } }

    public void Add(string line)
    {
        if (line.Length > 4000) return;
        lock (_gate)
        {
            if (_finished) return;
            // --log-jsonl uses the same message text, with JSON escaping.
            if (line.StartsWith('{'))
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (!json.RootElement.TryGetProperty("msg", out var message) || message.ValueKind != JsonValueKind.String) return;
                    line = message.GetString()!;
                }
                catch (JsonException) { return; }
            }
            line = Ansi().Replace(line, "");
            if (line.Contains("creating MTP draft context", StringComparison.OrdinalIgnoreCase))
            {
                _mtp = true;
                _context++;
            }
            if (line.Contains("constructing llama_context", StringComparison.Ordinal)) _context++;
            var noAlloc = NoAlloc().Match(line);
            if (noAlloc.Success) _estimate = noAlloc.Groups[1].Value is "1" or "true";
            // A new tensor load supersedes memory-fit dry runs or a failed load attempt.
            if (line.Contains("loading model tensors", StringComparison.Ordinal) && !_mtp)
            {
                _buffers.Clear();
                _context = 0;
                _snapshot = MemoryBreakdown.Empty;
            }
            if (_estimate) return;
            if (line.Contains("srv", StringComparison.Ordinal) &&
                (line.Contains(": model loaded", StringComparison.Ordinal) || line.Contains(": listening on", StringComparison.Ordinal)))
            { _finished = true; return; }
            var match = Buffer().Match(line);
            if (!match.Success || _context > 64 || _buffers.Count >= 1024) return;
            var device = match.Groups["device"].Value;
            if (!IsHost(device) && !GpuDevice().IsMatch(device)) return;
            if (!double.TryParse(match.Groups["size"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size) || !double.IsFinite(size)) return;
            size *= match.Groups["unit"].Value switch { "GiB" => 1024, "KiB" => 1d / 1024, _ => 1 };
            var category = match.Groups["kind"].Value switch
            {
                "model" => MemoryCategory.Weights,
                "KV" => _mtp ? MemoryCategory.MtpKvCache : MemoryCategory.MainKvCache,
                "RS" => MemoryCategory.RecurrentState,
                "compute" => MemoryCategory.Compute,
                _ => MemoryCategory.Output
            };
            var key = (category == MemoryCategory.Weights ? 0 : _context, category, device);
            // Weight shards and SWA/full-attention cache buffers can share a backend.
            // Graph reservation can repeat; the latest size replaces that context's reservation.
            if (category == MemoryCategory.Compute) _buffers[key] = size;
            else _buffers[key] = _buffers.GetValueOrDefault(key) + size;
            MemoryAmount Amount(MemoryCategory kind)
            {
                var entries = _buffers.Where(b => b.Key.Category == kind).ToArray();
                double? Sum(bool host)
                {
                    var selected = entries.Where(b => IsHost(b.Key.Device) == host).ToArray();
                    return selected.Length == 0 ? null : selected.Sum(b => b.Value);
                }
                return new(Sum(false), Sum(true));
            }
            _snapshot = new(Amount(MemoryCategory.Weights), Amount(MemoryCategory.MainKvCache),
                Amount(MemoryCategory.RecurrentState), Amount(MemoryCategory.MtpKvCache),
                Amount(MemoryCategory.Compute), Amount(MemoryCategory.Output));
        }
    }

    private static bool IsHost(string device) => device.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) ||
        device.Contains("Host", StringComparison.OrdinalIgnoreCase) || device.Contains("Mapped", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
    [GeneratedRegex(@"no_alloc\s*=\s*(0|1|true|false)\b")]
    private static partial Regex NoAlloc();
    [GeneratedRegex(@"^(?:CUDA|ROCm|HIP|Vulkan|Metal|SYCL|OpenCL|Kompute|WebGPU)[\w:.-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex GpuDevice();
    [GeneratedRegex(@"(?:load_tensors|llm_load_tensors|llama_[\w]+|graph_reserve):\s+(?<device>[\w:.-]+)\s+(?<kind>model|KV|RS|compute|output) buffer size\s*=\s*(?<size>\d+(?:\.\d+)?)\s+(?<unit>MiB|GiB|KiB)\b")]
    private static partial Regex Buffer();
}
