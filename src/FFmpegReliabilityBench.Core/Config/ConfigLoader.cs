using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace FFmpegReliabilityBench.Core.Config;

public sealed record ConfigValue(string Key, string Value, string Origin);

public sealed record Preset(string Name, string Description, string Json);

/// <summary>
/// Precedence: built-in defaults &lt; preset &lt; config file(s) &lt; command-line overrides.
/// Records the origin of every effective value so the UI can show where it came from.
/// </summary>
public static class ConfigLoader
{
    public static readonly IReadOnlyList<Preset> Presets = new[]
    {
        new Preset("local-4-streams", "4 independent synthetic sources × 1 consumer, dual-copy, parallel, 10 min (default)", "{}"),
        new Preset("fan-out-4", "1 synthetic source × 4 consumers (FAN-OUT: many readers of ONE source, not 4 cameras)",
            """{"lab":{"publishers":{"count":1},"consumers":{"instancesPerSource":4}}}"""),
        new Preset("mixed-4x3", "4 synthetic sources × 3 consumers each (capacity/contention)",
            """{"lab":{"publishers":{"count":4},"consumers":{"instancesPerSource":3}}}"""),
        new Preset("live-synthetic-4", "4 sources encoded live from a test pattern (measures generator load)",
            """{"lab":{"publishers":{"mode":"live-synthetic"}}}"""),
        new Preset("profile-compare", "original vs dual-copy vs dual-transcode, sequential groups on 4 sources",
            """{"lab":{"consumers":{"profiles":["original","dual-copy","dual-transcode"],"execution":"sequential"}}}"""),
        new Preset("staged-8", "8 sources, staged load 1 → 2 → 4 → 8 active consumers",
            """{"lab":{"publishers":{"count":8},"consumers":{"execution":"staged-load","stages":[1,2,4,8]}}}"""),
        new Preset("cameras", "Real cameras from lab.sources / --source; no relay, no publishers",
            """{"lab":{"relay":{"mode":"none"},"publishers":{"count":0}}}"""),
    };

    public sealed class Result
    {
        public BenchConfig Config { get; init; } = new();
        public IReadOnlyList<ConfigValue> Values { get; init; } = Array.Empty<ConfigValue>();
        public string PresetName { get; init; } = "";
    }

    /// <param name="cliValues">Scalar overrides as configuration keys ("lab:publishers:count" → "8").</param>
    /// <param name="cliLists">List overrides (replace, never merge): "lab:consumers:profiles" → ["a","b"].</param>
    public static Result Load(IEnumerable<string> configFiles, IReadOnlyDictionary<string, string?> cliValues, IReadOnlyDictionary<string, List<string>>? cliLists = null, string? defaultPreset = null)
    {
        var files = configFiles.Select(Path.GetFullPath).ToList();
        cliLists ??= new Dictionary<string, List<string>>();
        // Preset name: CLI > config file > built-in default
        var fileOnly = new ConfigurationBuilder(); foreach (var f in files) fileOnly.AddJsonFile(f, optional: false);
        var presetName = cliValues.GetValueOrDefault("lab:preset") ?? fileOnly.Build()["lab:preset"] ?? defaultPreset ?? "local-4-streams";
        var preset = Presets.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown preset '{presetName}'. Known: {string.Join(", ", Presets.Select(p => p.Name))}");

        var defaultsJson = JsonSerializer.Serialize(new BenchConfig(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var b = new ConfigurationBuilder();
        b.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(defaultsJson)));          // 0: defaults (for origin tracking)
        b.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(preset.Json)));           // 1: preset
        foreach (var f in files) b.AddJsonFile(f, optional: false);                       // 2..: files
        b.AddInMemoryCollection(cliValues);                                               // last: CLI
        var root = b.Build();

        var cfg = new BenchConfig();
        root.Bind(cfg);
        // Lists: later layers replace (IConfiguration would merge arrays index by index).
        var listOrigins = new Dictionary<string, string>();
        void ApplyList(string key, Action<List<string>> set)
        {
            if (cliLists.TryGetValue(key, out var cli)) { set(cli); listOrigins[key] = "command line"; return; }
            for (int i = files.Count - 1; i >= 0; i--)
            {
                var sec = new ConfigurationBuilder().AddJsonFile(files[i]).Build().GetSection(key);
                if (sec.Exists()) { set(sec.GetChildren().Select(c => c.Value ?? "").ToList()); listOrigins[key] = "config: " + Path.GetFileName(files[i]); return; }
            }
            var ps = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(preset.Json))).Build().GetSection(key);
            if (ps.Exists()) { set(ps.GetChildren().Select(c => c.Value ?? "").ToList()); listOrigins[key] = "preset: " + preset.Name; }
        }
        ApplyList("lab:consumers:profiles", l => cfg.Lab.Consumers.Profiles = l);
        ApplyList("lab:consumers:stages", l => cfg.Lab.Consumers.Stages = l.Select(int.Parse).ToList());
        if (cliLists.TryGetValue("lab:sources", out var srcUrls))
        {
            cfg.Lab.Sources = srcUrls.Select((u, i) => new SourceDef { Id = $"cam{i + 1:00}", Kind = "camera", Url = u }).ToList();
            listOrigins["lab:sources"] = "command line";
        }
        Normalize(cfg);
        cfg.Lab.Preset = preset.Name;

        // Origins for every scalar key of the effective configuration
        var providers = root.Providers.ToList();
        string Origin(string key)
        {
            for (int i = providers.Count - 1; i >= 1; i--)
                if (providers[i].TryGet(key, out _))
                    return i == 1 ? "preset: " + preset.Name : i == providers.Count - 1 ? "command line" : "config: " + Path.GetFileName(files[i - 2]);
            return "default";
        }
        var values = new List<ConfigValue>();
        var effective = JsonSerializer.SerializeToElement(cfg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        void Walk(JsonElement e, string prefix)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : prefix + ":" + p.Name);
                    break;
                case JsonValueKind.Array:
                    var listKey = prefix;
                    var text = string.Join(",", e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? "{…}" : x.ToString()));
                    values.Add(new ConfigValue(listKey, "[" + text + "]", listOrigins.GetValueOrDefault(listKey) ?? (e.GetArrayLength() == 0 ? "default" : Origin(listKey + ":0"))));
                    break;
                default:
                    values.Add(new ConfigValue(prefix, e.ToString(), Origin(prefix)));
                    break;
            }
        }
        Walk(effective, "");
        return new Result { Config = cfg, Values = values, PresetName = preset.Name };
    }

    /// <summary>Fills list defaults after binding (the binder appends to pre-populated lists, so lists start empty).</summary>
    public static void Normalize(BenchConfig c)
    {
        if (c.Lab.Consumers.Profiles.Count == 0) c.Lab.Consumers.Profiles = new List<string> { "dual-copy" };
        c.Lab.Consumers.Profiles = c.Lab.Consumers.Profiles.SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (string.IsNullOrWhiteSpace(c.Lab.Consumers.Execution))
            c.Lab.Consumers.Execution = c.Lab.Consumers.Profiles.Distinct().Count() > 1 ? "sequential" : "parallel";
    }
}
