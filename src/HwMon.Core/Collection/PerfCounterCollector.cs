using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Lê categorias inteiras de contadores do Windows de uma vez (ReadCategory) e calcula as taxas entre
/// duas amostras. Os nomes de categoria/contador em inglês funcionam também em Windows localizado.
/// </summary>
public sealed partial class PerfCounterCollector
{
    private const string Source = "perf";
    private static readonly string[] Categories = ["Processor Information", "PhysicalDisk", "GPU Engine", "GPU Adapter Memory", "Memory"];

    private Dictionary<string, CategorySnapshot>? _previous;
    private readonly Dictionary<string, int> _gpuIndexByLuid;
    private readonly List<string> _problems;

    public PerfCounterCollector(IEnumerable<GpuInfo> gpus, List<string> problems)
    {
        _gpuIndexByLuid = gpus.SelectMany(g => g.AliasLuids.Prepend(g.Luid).Select(l => (Luid: l, g.Index)))
            .ToDictionary(x => x.Luid, x => x.Index, StringComparer.OrdinalIgnoreCase);
        _problems = problems;
    }

    /// <summary>Primeira amostra: não produz leituras (taxas precisam de duas amostras).</summary>
    public void Prime() => _previous = Snapshot();

    public IEnumerable<Reading> Sample()
    {
        var current = Snapshot();
        var previous = _previous;
        _previous = current;
        if (previous is null) yield break;

        foreach (var r in Cpu(previous, current)) yield return r;
        foreach (var r in Disks(previous, current)) yield return r;
        foreach (var r in Gpus(previous, current)) yield return r;
        Detail = BuildDetail(previous, current);
    }

    private Dictionary<string, CategorySnapshot> Snapshot()
    {
        var result = new Dictionary<string, CategorySnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Categories)
        {
            try
            {
                var data = new CategorySnapshot();
                var collection = new PerformanceCounterCategory(name).ReadCategory();
                foreach (InstanceDataCollection counter in collection.Values)
                foreach (InstanceData instance in counter.Values)
                {
                    if (!data.TryGetValue(instance.InstanceName, out var counters))
                        data[instance.InstanceName] = counters = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);
                    counters[counter.CounterName] = instance.Sample;
                }
                result[name] = data;
            }
            catch (Exception ex)
            {
                var msg = $"Contadores '{name}': {ex.Message}";
                if (!_problems.Contains(msg)) _problems.Add(msg);
            }
        }
        return result;
    }

    private static double? Calc(Dictionary<string, CategorySnapshot> prev, Dictionary<string, CategorySnapshot> cur, string category, string instance, string counter)
    {
        if (!prev.TryGetValue(category, out var p) || !cur.TryGetValue(category, out var c)) return null;
        if (!p.TryGetValue(instance, out var pi) || !c.TryGetValue(instance, out var ci)) return null;
        if (!pi.TryGetValue(counter, out var ps) || !ci.TryGetValue(counter, out var cs)) return null;
        return CounterSample.Calculate(ps, cs);
    }

    private static IEnumerable<Reading> Cpu(Dictionary<string, CategorySnapshot> prev, Dictionary<string, CategorySnapshot> cur)
    {
        const string cat = "Processor Information";
        // "% Processor Utility" é o que o Gerenciador de Tarefas usa; pode passar de 100 com turbo.
        var utility = Calc(prev, cur, cat, "_Total", "% Processor Utility");
        yield return utility is { } u
            ? Reading.Ok("cpu", "0", "usage", Math.Clamp(u, 0, 100), "%", Source)
            : Reading.Unavailable("cpu", "0", "usage", "%", Source, "contador ausente");

        // Clock efetivo = frequência nominal × % de desempenho (mesmo método do Gerenciador de Tarefas).
        var nominal = Calc(prev, cur, cat, "_Total", "Processor Frequency");
        var perf = Calc(prev, cur, cat, "_Total", "% Processor Performance");
        if (nominal is { } n && perf is { } pf && n > 0)
            yield return Reading.Ok("cpu", "0", "clock", n * pf / 100.0, "MHz", Source, "nominal × % desempenho");
        else
            yield return Reading.Unavailable("cpu", "0", "clock", "MHz", Source, "contador ausente");
    }

    private static IEnumerable<Reading> Disks(Dictionary<string, CategorySnapshot> prev, Dictionary<string, CategorySnapshot> cur)
    {
        const string cat = "PhysicalDisk";
        if (!cur.TryGetValue(cat, out var instances)) yield break;
        foreach (var instance in instances.Keys)
        {
            // Instâncias têm a forma "0 C: D:"; o número inicial é o número do disco.
            var space = instance.IndexOf(' ');
            var head = space > 0 ? instance[..space] : instance;
            if (!int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
            var id = number.ToString(CultureInfo.InvariantCulture);

            if (Calc(prev, cur, cat, instance, "% Idle Time") is { } idle)
                yield return Reading.Ok("disk", id, "active", Math.Clamp(100 - idle, 0, 100), "%", Source);
            if (Calc(prev, cur, cat, instance, "Disk Read Bytes/sec") is { } read)
                yield return Reading.Ok("disk", id, "read", read / 1_000_000.0, "MB/s", Source);
            if (Calc(prev, cur, cat, instance, "Disk Write Bytes/sec") is { } write)
                yield return Reading.Ok("disk", id, "write", write / 1_000_000.0, "MB/s", Source);
        }
    }

    [GeneratedRegex(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_\d+(?:_eng_\d+_engtype_(.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LuidRegex();

    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();

    /// <summary>
    /// Uso da engine 3D de cada processo na última amostra (%, todas as GPUs somadas). Separa jogo de programa que
    /// só redesenha a interface (Dropbox, VPN, apps em Electron): estes ficam perto de 0 %.
    /// </summary>
    public IReadOnlyDictionary<int, double> Gpu3dByProcess { get; private set; } = new Dictionary<int, double>();

    private IEnumerable<Reading> Gpus(Dictionary<string, CategorySnapshot> prev, Dictionary<string, CategorySnapshot> cur)
    {
        // Uso da GPU como no Gerenciador de Tarefas: soma por tipo de engine (todos os processos), maior tipo vence.
        var byLuid = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var byPid = new Dictionary<int, double>();
        if (cur.TryGetValue("GPU Engine", out var engines))
        {
            foreach (var instance in engines.Keys)
            {
                var m = LuidRegex().Match(instance);
                if (!m.Success) continue;
                var value = Calc(prev, cur, "GPU Engine", instance, "Utilization Percentage");
                if (value is null) continue; // processo novo ou encerrado entre as amostras
                var luid = m.Groups[1].Value;
                var engType = m.Groups[2].Value;
                if (!byLuid.TryGetValue(luid, out var types)) byLuid[luid] = types = new(StringComparer.OrdinalIgnoreCase);
                types[engType] = types.GetValueOrDefault(engType) + value.Value;
                if (engType.Equals("3D", StringComparison.OrdinalIgnoreCase)
                    && PidRegex().Match(instance) is { Success: true } p && int.TryParse(p.Groups[1].Value, out var pid))
                    byPid[pid] = byPid.GetValueOrDefault(pid) + value.Value;
            }
        }
        Gpu3dByProcess = byPid;

        // Uma GPU pode ter vários LUIDs (apelidos); junta tudo por índice.
        var engines2 = new Dictionary<int, Dictionary<string, double>>();
        _lastEngines = engines2;
        foreach (var group in _gpuIndexByLuid.GroupBy(kv => kv.Value))
        {
            var id = group.Key.ToString(CultureInfo.InvariantCulture);
            var types = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (luid, _) in group)
                if (byLuid.TryGetValue(luid, out var t))
                    foreach (var (type, value) in t)
                        types[type] = Math.Max(types.GetValueOrDefault(type), value);
            engines2[group.Key] = types.ToDictionary(kv => kv.Key, kv => Math.Round(Math.Clamp(kv.Value, 0, 100), 1), StringComparer.OrdinalIgnoreCase);

            if (engines is null)
                yield return Reading.Unavailable("gpu", id, "usage", "%", Source, "categoria 'GPU Engine' ausente");
            else if (types.Count > 0)
                yield return Reading.Ok("gpu", id, "usage", Math.Clamp(types.Values.Max(), 0, 100), "%", Source,
                    "engine mais ocupada: " + types.MaxBy(kv => kv.Value).Key);
            else
                yield return Reading.Ok("gpu", id, "usage", 0, "%", Source, "nenhuma engine ativa");
        }

        if (cur.TryGetValue("GPU Adapter Memory", out var memory))
        {
            var dedicatedByIndex = new Dictionary<int, double>();
            foreach (var (instance, counters) in memory)
            {
                var m = LuidRegex().Match(instance);
                if (!m.Success || !_gpuIndexByLuid.TryGetValue(m.Groups[1].Value, out var index)) continue;
                if (counters.TryGetValue("Dedicated Usage", out var dedicated))
                    dedicatedByIndex[index] = Math.Max(dedicatedByIndex.GetValueOrDefault(index), dedicated.RawValue / 1_073_741_824.0);
            }
            foreach (var (index, gb) in dedicatedByIndex)
                yield return Reading.Ok("gpu", index.ToString(CultureInfo.InvariantCulture), "vram_used", gb, "GB", Source);
        }
    }

    // ------------------------------------------------------------------ aba Desempenho

    /// <summary>Detalhes da última amostra para a aba Desempenho (por número do disco e índice da GPU).</summary>
    public RawPerfDetail? Detail { get; private set; }

    public sealed class RawPerfDetail
    {
        public List<double> Cores { get; } = new();
        public double? BaseMhz { get; set; }
        public Dictionary<string, double> DiskResponseMs { get; } = new();
        public Dictionary<int, Dictionary<string, double>> GpuEngines { get; } = new();
        public Dictionary<int, double> GpuSharedGb { get; } = new();
        public double? ModifiedBytes { get; set; }
        public double? StandbyBytes { get; set; }
        public double? FreeBytes { get; set; }
    }

    private RawPerfDetail BuildDetail(Dictionary<string, CategorySnapshot> prev, Dictionary<string, CategorySnapshot> cur)
    {
        var d = new RawPerfDetail();

        // Núcleos: instâncias "grupo,número" ("0,0", "0,1"...); "_Total" e "0,_Total" são somas.
        const string cpu = "Processor Information";
        if (cur.TryGetValue(cpu, out var procs))
        {
            var cores = new List<(int Group, int Number, double Value)>();
            foreach (var instance in procs.Keys)
            {
                var comma = instance.IndexOf(',');
                if (comma <= 0
                    || !int.TryParse(instance[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
                    || !int.TryParse(instance[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                if (Calc(prev, cur, cpu, instance, "% Processor Utility") is { } u) cores.Add((group, number, Math.Clamp(u, 0, 100)));
            }
            d.Cores.AddRange(cores.OrderBy(c => c.Group).ThenBy(c => c.Number).Select(c => Math.Round(c.Value, 1)));
            d.BaseMhz = Calc(prev, cur, cpu, "_Total", "Processor Frequency");
        }

        // Tempo médio de resposta do disco (s → ms).
        if (cur.TryGetValue("PhysicalDisk", out var disks))
            foreach (var instance in disks.Keys)
            {
                var space = instance.IndexOf(' ');
                var head = space > 0 ? instance[..space] : instance;
                if (!int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
                if (Calc(prev, cur, "PhysicalDisk", instance, "Avg. Disk sec/Transfer") is { } sec)
                    d.DiskResponseMs[number.ToString(CultureInfo.InvariantCulture)] = Math.Round(sec * 1000, 2);
            }

        d.GpuEngines.Clear();
        foreach (var (index, types) in _lastEngines) d.GpuEngines[index] = types;

        if (cur.TryGetValue("GPU Adapter Memory", out var memory))
            foreach (var (instance, counters) in memory)
            {
                var m = LuidRegex().Match(instance);
                if (!m.Success || !_gpuIndexByLuid.TryGetValue(m.Groups[1].Value, out var index)) continue;
                if (counters.TryGetValue("Shared Usage", out var shared))
                    d.GpuSharedGb[index] = Math.Max(d.GpuSharedGb.GetValueOrDefault(index), shared.RawValue / 1_073_741_824.0);
            }

        // Composição da memória (bytes, valores instantâneos).
        if (cur.TryGetValue("Memory", out var mem) && mem.Values.FirstOrDefault() is { } counters2)
        {
            double? Raw(string name) => counters2.TryGetValue(name, out var c) ? c.RawValue : null;
            d.ModifiedBytes = Raw("Modified Page List Bytes");
            var standby = new[] { "Standby Cache Core Bytes", "Standby Cache Normal Priority Bytes", "Standby Cache Reserve Bytes" }.Select(Raw).ToList();
            d.StandbyBytes = standby.All(v => v is null) ? null : standby.Sum(v => v ?? 0);
            d.FreeBytes = Raw("Free & Zero Page List Bytes");
        }
        return d;
    }

    /// <summary>Uso por tipo de motor de cada GPU na última amostra (preenchido em <see cref="Gpus"/>).</summary>
    private Dictionary<int, Dictionary<string, double>> _lastEngines = new();

    private sealed class CategorySnapshot : Dictionary<string, Dictionary<string, CounterSample>>
    {
        public CategorySnapshot() : base(StringComparer.OrdinalIgnoreCase) { }
    }
}
