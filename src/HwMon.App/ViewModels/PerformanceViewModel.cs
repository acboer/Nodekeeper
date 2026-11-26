using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Últimos 60 valores; <see cref="Values"/> troca de instância a cada ponto para o gráfico redesenhar.</summary>
public sealed class PerfSeries
{
    public const int Length = 60;
    private readonly List<double> _points = new(Length + 1);

    public IReadOnlyList<double> Values { get; private set; } = Array.Empty<double>();
    public double Last => _points.Count > 0 ? _points[^1] : 0;
    public double Max => _points.Count > 0 ? _points.Max() : 0;

    public void Push(double? value)
    {
        _points.Add(value is { } v && double.IsFinite(v) ? Math.Max(0, v) : 0);
        if (_points.Count > Length) _points.RemoveAt(0);
        Values = _points.ToArray();
    }
}

/// <summary>Um número do quadro embaixo do gráfico ("Velocidade 4,85 GHz").</summary>
public sealed class PerfStat(string label) : ObservableObject
{
    private string _value = "—";
    public string Label { get; } = L.T(label);
    public string Value { get => _value; set => Set(ref _value, value); }
}

/// <summary>Gráfico menor dentro do detalhe: um núcleo, um motor da GPU, a memória da GPU.</summary>
public sealed class PerfMiniChart(string kind, string title) : ObservableObject
{
    private string _title = title;
    private string _valueText = "";
    private double _maximum = 100;
    public string Kind { get; } = kind;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }
    public PerfSeries Series { get; } = new();
    public IReadOnlyList<double> Values => Series.Values;

    public void Push(double? value, string text)
    {
        Series.Push(value);
        ValueText = text;
        Raise(nameof(Values));
    }
}

/// <summary>Segmento da barra de composição da memória.</summary>
public sealed record MemorySegment(string Label, string Value, GridLength Width, double Opacity);

/// <summary>Um item da lista à esquerda (CPU, Memória, um disco, uma placa de rede, uma GPU) e o seu detalhe.</summary>
public sealed class PerfDevice : ObservableObject
{
    private string _subtitle = "";
    private string _scaleText = "100 %";
    private string _scale2Text = "";
    private double _maximum = 100;
    private double _maximum2 = 1;
    private bool _isSelected;

    public PerfDevice(string kind, string id, string title, string model, string? detail)
    {
        Kind = kind;
        Id = id;
        Title = title;
        Model = model;
        Detail = detail;
    }

    /// <summary>cpu, memory, disk, net, gpu (escolhe a cor e o que o detalhe mostra).</summary>
    public string Kind { get; }
    public string Id { get; }
    public string Title { get; }
    public string Model { get; }
    public string? Detail { get; }
    public bool IsCpu => Kind == "cpu";
    public bool IsMemory => Kind == "memory";
    public bool IsDisk => Kind == "disk";
    public bool IsNet => Kind == "net";
    public bool IsGpu => Kind == "gpu";

    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Série principal (lista e gráfico grande) e segunda série tracejada (gravação, envio).</summary>
    public PerfSeries Main { get; } = new();
    public PerfSeries? Second { get; init; }
    public IReadOnlyList<double> Values => Main.Values;
    public IReadOnlyList<double>? Values2 => Second?.Values;

    public string ChartTitle { get; init; } = "";
    public string? Chart2Title { get; init; }
    /// <summary>Disco: o gráfico grande é o tempo ativo; este é a taxa de transferência (leitura e gravação).</summary>
    public PerfSeries? Rate { get; init; }
    public PerfSeries? Rate2 { get; init; }
    public IReadOnlyList<double>? RateValues => Rate?.Values;
    public IReadOnlyList<double>? RateValues2 => Rate2?.Values;

    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }
    public string ScaleText { get => _scaleText; set => Set(ref _scaleText, value); }
    public double Maximum2 { get => _maximum2; set => Set(ref _maximum2, value); }
    public string Scale2Text { get => _scale2Text; set => Set(ref _scale2Text, value); }

    public ObservableCollection<PerfStat> Stats { get; } = new();
    /// <summary>CPU: um gráfico por processador lógico. GPU: motores e memória.</summary>
    public ObservableCollection<PerfMiniChart> Minis { get; } = new();
    public ObservableCollection<MemorySegment> Segments { get; } = new();
    private bool _hasSegments;
    public bool HasSegments { get => _hasSegments; set => Set(ref _hasSegments, value); }
    /// <summary>Colunas da grade de gráficos por núcleo (32 threads → 8 × 4).</summary>
    public int MiniColumns => IsGpu ? 2 : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Math.Max(1, Minis.Count) * 2)));
    public bool HasMinis => Minis.Count > 0;

    private bool _perCore;
    /// <summary>CPU: um gráfico por processador lógico em vez do geral.</summary>
    public bool PerCore
    {
        get => _perCore;
        set
        {
            if (!Set(ref _perCore, value)) return;
            Raise(nameof(Overall));
            Raise(nameof(ShowMain));
            Raise(nameof(ShowMinis));
        }
    }
    public bool Overall { get => !_perCore; set => PerCore = !value; }
    public bool ShowMain => !(IsCpu && _perCore && _live);
    public bool ShowMinis => _live && HasMinis && (IsGpu || (IsCpu && _perCore));

    public PerfStat Stat(string label)
    {
        var text = L.T(label);
        var s = Stats.FirstOrDefault(x => x.Label == text);
        if (s is null) Stats.Add(s = new PerfStat(label));
        return s;
    }

    // ------------------------------------------------------------------ temperatura sobreposta

    /// <summary>Temperatura (CPU, GPU, disco), desenhada por cima do gráfico grande com escala própria até 100 °C.</summary>
    public PerfSeries Overlay { get; } = new();
    public bool HasOverlay => Overlay.Max > 0 || (_history?.Overlay is { Count: > 0 });
    public double OverlayMaximum => 100;

    // ------------------------------------------------------------------ janela de tempo

    /// <summary>Séries de uma janela longa (10 min, 1 h, 24 h), lidas do histórico do serviço.</summary>
    public sealed record HistoryView(IReadOnlyList<double> Main, IReadOnlyList<double>? Second, IReadOnlyList<double>? Overlay,
        IReadOnlyList<double>? Rate, IReadOnlyList<double>? Rate2, int Capacity, double Maximum, string ScaleText, double Maximum2, string Scale2Text);

    private HistoryView? _history;
    private bool _live = true;
    private string _rangeLabel = L.T("60 segundos");

    /// <summary>Janela de 60 s ao vivo (padrão) ou longa, lida do histórico.</summary>
    public bool Live => _live;
    public string RangeLabel { get => _rangeLabel; private set => Set(ref _rangeLabel, value); }

    public void ShowLive()
    {
        _live = true;
        _history = null;
        RangeLabel = L.T("60 segundos");
        Changed();
    }

    public void ShowHistory(HistoryView? view, string label)
    {
        _live = false;
        _history = view;
        RangeLabel = label;
        Changed();
    }

    private static readonly IReadOnlyList<double> Empty = Array.Empty<double>();
    public IReadOnlyList<double> ChartValues => _live ? Values : _history?.Main ?? Empty;
    public IReadOnlyList<double>? ChartValues2 => _live ? Values2 : _history?.Second;
    public IReadOnlyList<double>? ChartOverlay => !HasOverlay ? null : _live ? Overlay.Values : _history?.Overlay;
    public IReadOnlyList<double>? ChartRate => _live ? RateValues : _history?.Rate;
    public IReadOnlyList<double>? ChartRate2 => _live ? RateValues2 : _history?.Rate2;
    public int ChartCapacity => _live ? PerfSeries.Length : Math.Max(2, _history?.Capacity ?? 2);
    public double ChartMaximum => _live ? Maximum : _history?.Maximum ?? Maximum;
    public string ChartScaleText => _live ? ScaleText : _history?.ScaleText ?? ScaleText;
    public double ChartMaximum2 => _live ? Maximum2 : _history?.Maximum2 ?? Maximum2;
    public string ChartScale2Text => _live ? Scale2Text : _history?.Scale2Text ?? Scale2Text;

    public void Changed()
    {
        Raise(nameof(HasOverlay));
        Raise(nameof(Live));
        Raise(nameof(ChartValues));
        Raise(nameof(ChartValues2));
        Raise(nameof(ChartOverlay));
        Raise(nameof(ChartRate));
        Raise(nameof(ChartRate2));
        Raise(nameof(ChartCapacity));
        Raise(nameof(ChartMaximum));
        Raise(nameof(ChartScaleText));
        Raise(nameof(ChartMaximum2));
        Raise(nameof(ChartScale2Text));
        Raise(nameof(ShowMain));
        Raise(nameof(MiniColumns));
        Raise(nameof(HasMinis));
        Raise(nameof(ShowMinis));
        Raise(nameof(Values));
        Raise(nameof(Values2));
        Raise(nameof(RateValues));
        Raise(nameof(RateValues2));
    }
}

/// <summary>
/// Aba Desempenho da página de uma máquina (como a do Gerenciador de Tarefas): lista à esquerda, gráficos dos
/// últimos 60 s à direita. Só trabalha com a aba aberta: a cada 1 s lê os valores do snapshot e pede ao serviço os
/// detalhes (/v1/perf: núcleos, motores da GPU, memória detalhada, resposta dos discos). Serviço anterior à 0.16
/// mostra só o que vem no snapshot.
/// </summary>
public sealed class PerformanceViewModel : ObservableObject
{
    private readonly MachineViewModel _machine;
    private readonly DispatcherTimer _timer;
    private CollectorClient? _client;
    private PerfDevice? _selected;
    private bool _busy;
    private bool _detailSupported = true;
    private string _layoutKey = "";

    public PerformanceViewModel(MachineViewModel machine)
    {
        _machine = machine;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public ObservableCollection<PerfDevice> Devices { get; } = new();

    public PerfDevice? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            foreach (var d in Devices) d.IsSelected = d == value;
            if (_range != 60) _ = LoadHistoryAsync(force: true);
        }
    }


    // ------------------------------------------------------------------ janela de tempo

    private int _range = 60;
    private DateTimeOffset _historyLoaded = DateTimeOffset.MinValue;
    private string? _historyFor;
    private string? _historyError;

    /// <summary>Janela dos gráficos em segundos: 60 (ao vivo), 600, 3600 ou 86400 (do histórico do serviço).</summary>
    public int Range
    {
        get => _range;
        set
        {
            if (!Set(ref _range, value)) return;
            Raise(nameof(Range60));
            Raise(nameof(Range10m));
            Raise(nameof(Range1h));
            Raise(nameof(Range24h));
            HistoryError = null;
            if (value == 60)
            {
                foreach (var d in Devices) d.ShowLive();
                return;
            }
            foreach (var d in Devices) d.ShowHistory(null, RangeText(value));
            _ = LoadHistoryAsync(force: true);
        }
    }

    public bool Range60 { get => _range == 60; set { if (value) Range = 60; } }
    public bool Range10m { get => _range == 600; set { if (value) Range = 600; } }
    public bool Range1h { get => _range == 3600; set { if (value) Range = 3600; } }
    public bool Range24h { get => _range == 86400; set { if (value) Range = 86400; } }
    public string? HistoryError { get => _historyError; private set => Set(ref _historyError, value); }

    private static string RangeText(int seconds) => seconds switch
    {
        600 => L.T("10 minutos"),
        3600 => L.T("1 hora"),
        _ => L.T("24 horas"),
    };

    /// <summary>Quanto esperar para reler o histórico: a resolução dele (10 s, 1 min, 1 h) torna mais que isso inútil.</summary>
    private static TimeSpan RefreshEvery(int seconds) => seconds switch
    {
        600 => TimeSpan.FromSeconds(10),
        3600 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Janela longa: lê do histórico do serviço só o dispositivo escolhido (as séries do gráfico grande). Cada ponto
    /// vai para a posição do seu horário; intervalo sem dados (PC desligado) fica em zero.
    /// </summary>
    private async Task LoadHistoryAsync(bool force)
    {
        if (_range == 60 || _selected is not { } d) return;
        if (!force && _historyFor == d.Id && DateTimeOffset.Now - _historyLoaded < RefreshEvery(_range)) return;
        _historyFor = d.Id;
        _historyLoaded = DateTimeOffset.Now;
        var range = _range;
        var label = RangeText(range);
        var to = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var from = to - range;
        try
        {
            _client ??= CollectorClient.For(_machine.Machine);
            var client = _client;
            async Task<(double[] Values, int Capacity)?> Load(string metric)
            {
                var r = await client.HistoryAsync(d.Id, metric, from, to, 240);
                if (r.Points.Count == 0) return null;
                var step = Math.Max(1, r.StepSeconds);
                var count = (int)Math.Clamp((to - from) / step, 2, 2000);
                var values = new double[count];
                foreach (var p in r.Points)
                {
                    var i = (int)Math.Clamp((p.T - from) / step, 0, count - 1);
                    values[i] = Math.Max(0, p.Avg);
                }
                return (values, count);
            }

            string main1 = d.Kind switch { "memory" => "used", "disk" => "active", "net" => "rx", _ => "usage" };
            var main = await Load(main1);
            var second = d.IsNet ? await Load("tx") : null;
            var overlay = d.Kind is "cpu" or "gpu" or "disk" ? await Load("temp") : null;
            var rate = d.IsDisk ? await Load("read") : null;
            var rate2 = d.IsDisk ? await Load("write") : null;
            if (_range != range || _selected != d) return; // trocou de janela ou de dispositivo enquanto lia

            var max = d.Maximum;
            var scale = d.ScaleText;
            if (d.IsNet)
            {
                max = NiceMax(Math.Max(main?.Values.Max() ?? 0, second?.Values.Max() ?? 0), 0.1);
                scale = N(max, "Mbit/s");
            }
            var max2 = d.Maximum2;
            var scale2 = d.Scale2Text;
            if (d.IsDisk)
            {
                max2 = NiceMax(Math.Max(rate?.Values.Max() ?? 0, rate2?.Values.Max() ?? 0), 1);
                scale2 = N(max2, "MB/s");
            }
            d.ShowHistory(new PerfDevice.HistoryView(main?.Values ?? [], second?.Values, overlay?.Values, rate?.Values, rate2?.Values,
                main?.Capacity ?? 2, max, scale, max2, scale2), label);
            HistoryError = main is null ? L.T("Ainda não há histórico deste dispositivo nesse período.") : null;
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            HistoryError = L.T("O histórico está desligado nesta máquina.");
            d.ShowHistory(null, label);
        }
        catch (Exception ex) when (ex is CollectorException or HttpRequestException or TaskCanceledException or IOException)
        {
            HistoryError = L.T("Não foi possível ler o histórico: {0}", ex.Message);
        }
    }

    /// <summary>Serviço 0.16+: tem núcleos, motores da GPU, memória detalhada e resposta dos discos.</summary>
    public bool DetailSupported { get => _detailSupported; private set { if (Set(ref _detailSupported, value)) Raise(nameof(DetailHint)); } }
    public string? DetailHint => _detailSupported ? null
        : L.T("O serviço desta máquina é anterior à 0.16: núcleos, motores da GPU e memória detalhada aparecem depois de atualizar.");

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _client?.Dispose();
        _client = null;
    }

    // ------------------------------------------------------------------ lista

    private void EnsureDevices()
    {
        var components = _machine.Descriptor?.Components ?? [];
        // Rede: só os adaptadores com tráfego medido (ligados); os outros ficam de fora como no Gerenciador.
        var nets = components.Where(c => c.Kind == ComponentKind.Network && _machine.Values.ContainsKey((c.Id, "rx"))).ToList();
        var key = string.Join("|", components.Select(c => c.Id)) + "#" + string.Join("|", nets.Select(n => n.Id));
        if (key == _layoutKey) return;
        _layoutKey = key;
        var selectedId = _selected?.Id;
        Devices.Clear();

        foreach (var c in components.Where(c => c.Kind == ComponentKind.Cpu).Take(1))
            Devices.Add(new PerfDevice("cpu", c.Id, "CPU", c.Name, c.Detail) { ChartTitle = L.T("% de utilização") });
        foreach (var c in components.Where(c => c.Kind == ComponentKind.Ram).Take(1))
            Devices.Add(new PerfDevice("memory", c.Id, L.T("Memória"), c.Detail ?? "", null) { ChartTitle = L.T("Uso de memória") });

        var disks = components.Where(c => c.Kind == ComponentKind.Disk).ToList();
        for (var i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            var letters = components.Where(v => v.Kind == ComponentKind.Volume && v.Parents.Contains(d.Id))
                .Select(v => v.Name.Split(' ')[0]).Where(n => n.EndsWith(':')).ToList();
            var title = L.T("Disco {0}", i) + (letters.Count > 0 ? $" ({string.Join(" ", letters)})" : "");
            Devices.Add(new PerfDevice("disk", d.Id, title, d.Name, d.Detail)
            {
                ChartTitle = L.T("Tempo ativo"),
                Chart2Title = L.T("Taxa de transferência do disco"),
                Rate = new PerfSeries(),
                Rate2 = new PerfSeries(),
            });
        }

        foreach (var n in nets)
            Devices.Add(new PerfDevice("net", n.Id, n.Name, n.Detail?.Split(" · ")[0] ?? "", n.Detail)
            {
                ChartTitle = L.T("Taxa de transferência"),
                Second = new PerfSeries(),
            });

        var gpus = components.Where(c => c.Kind == ComponentKind.Gpu).ToList();
        for (var i = 0; i < gpus.Count; i++)
            Devices.Add(new PerfDevice("gpu", gpus[i].Id, $"GPU {i}", gpus[i].Name, gpus[i].Detail) { ChartTitle = L.T("% de utilização") });

        Selected = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
        if (_range != 60)
        {
            foreach (var d in Devices) d.ShowHistory(null, RangeText(_range));
            _ = LoadHistoryAsync(force: true);
        }
    }

    // ------------------------------------------------------------------ atualização

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            EnsureDevices();
            PerfDetail? detail = null;
            if (_machine.IsConnected && DetailSupported)
            {
                try
                {
                    _client ??= CollectorClient.For(_machine.Machine);
                    detail = await _client.PerfAsync();
                }
                catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
                {
                    DetailSupported = false;
                }
                catch (Exception ex) when (ex is CollectorException or HttpRequestException or TaskCanceledException or IOException)
                {
                    // Falha passageira: este segundo fica só com o snapshot.
                }
            }
            if (!_timer.IsEnabled) return;
            foreach (var d in Devices)
            {
                switch (d.Kind)
                {
                    case "cpu": UpdateCpu(d, detail); break;
                    case "memory": UpdateMemory(d, detail); break;
                    case "disk": UpdateDisk(d, detail); break;
                    case "net": UpdateNet(d, detail); break;
                    case "gpu": UpdateGpu(d, detail); break;
                }
                d.Changed();
            }
            if (_range != 60) await LoadHistoryAsync(force: false);
        }
        finally
        {
            _busy = false;
        }
    }

    private double? V(string component, string metric)
        => _machine.Values.TryGetValue((component, metric), out var v) && v.State is HwMon.Core.Model.ReadingState.Ok or HwMon.Core.Model.ReadingState.Stale ? v.Value : null;

    private static string N(double? value, string unit) => value is { } v ? Format.Number(v, unit) : "—";

    private static string Gb(double? value) => value is { } v ? v.ToString(v >= 100 ? "N0" : "0.0", L.Culture) + " GB" : "—";

    private void UpdateCpu(PerfDevice d, PerfDetail? detail)
    {
        var usage = V(d.Id, "usage");
        var clock = V(d.Id, "clock");
        d.Main.Push(usage);
        d.Subtitle = $"{N(usage, "%")}  {N(clock, "MHz")}";
        d.Stat("Utilização").Value = N(usage, "%");
        d.Stat("Velocidade").Value = N(clock, "MHz");
        var cpuTemp = V(d.Id, "temp");
        d.Overlay.Push(cpuTemp);
        if (cpuTemp is not null) d.Stat("Temperatura").Value = N(cpuTemp, "°C");
        if (V(d.Id, "power") is { } power) d.Stat("Consumo").Value = N(power, "W");
        if (V(d.Id, "clock_max") is { } clockMax) d.Stat("Núcleo mais rápido").Value = N(clockMax, "MHz");
        if (detail is null) return;

        var cpu = detail.Cpu;
        d.Stat("Velocidade base").Value = N(cpu.BaseMhz, "MHz");
        d.Stat("Processos").Value = cpu.Processes?.ToString("N0", L.Culture) ?? "—";
        d.Stat("Threads").Value = cpu.Threads?.ToString("N0", L.Culture) ?? "—";
        d.Stat("Identificadores").Value = cpu.Handles?.ToString("N0", L.Culture) ?? "—";
        var up = TimeSpan.FromSeconds(cpu.UptimeSeconds);
        d.Stat("Tempo ativo do sistema").Value = $"{(int)up.TotalDays}:{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";

        while (d.Minis.Count < cpu.Cores.Count) d.Minis.Add(new PerfMiniChart("cpu", L.T("CPU {0}", d.Minis.Count)));
        while (d.Minis.Count > cpu.Cores.Count) d.Minis.RemoveAt(d.Minis.Count - 1);
        for (var i = 0; i < cpu.Cores.Count; i++) d.Minis[i].Push(cpu.Cores[i], N(cpu.Cores[i], "%"));
    }

    private void UpdateMemory(PerfDevice d, PerfDetail? detail)
    {
        var used = V(d.Id, "used");
        var total = V(d.Id, "total");
        var available = V(d.Id, "available");
        var mem = detail?.Memory;
        var inUse = mem?.InUseGb ?? used;
        var totalGb = mem?.TotalGb ?? total ?? 0;
        d.Main.Push(inUse);
        if (totalGb > 0)
        {
            d.Maximum = totalGb;
            d.ScaleText = Gb(totalGb);
        }
        var pct = inUse is { } u && totalGb > 0 ? u / totalGb * 100 : (double?)null;
        d.Subtitle = $"{Gb(inUse).Replace(" GB", "")}/{Gb(totalGb)} ({N(pct, "%")})";
        d.Stat("Em uso").Value = Gb(inUse);
        d.Stat("Disponível").Value = Gb(mem?.AvailableGb ?? available);
        if (mem is null) return;

        d.Stat("Confirmada").Value = $"{Gb(mem.CommittedGb).Replace(" GB", "")}/{Gb(mem.CommitLimitGb)}";
        d.Stat("Em cache").Value = Gb(mem.CachedGb);
        d.Stat("Pool paginado").Value = Gb(mem.PagedPoolGb);
        d.Stat("Pool não paginado").Value = Gb(mem.NonPagedPoolGb);

        // Barra de composição: em uso, modificada, em espera, livre.
        var parts = new (string Label, double? Value, double Opacity)[]
        {
            ("Em uso", mem.InUseGb, 0.85), ("Modificada", mem.ModifiedGb, 0.55), ("Em espera", mem.StandbyGb, 0.3), ("Livre", mem.FreeGb, 0.0),
        };
        // Sempre os quatro (a barra liga cada coluna a Segments[i]); parte sem valor fica com largura zero.
        d.Segments.Clear();
        foreach (var (label, value, opacity) in parts)
            d.Segments.Add(new MemorySegment(L.T(label), Gb(value), new GridLength(Math.Max(0, value ?? 0), GridUnitType.Star), opacity));
        d.HasSegments = true;
    }

    private void UpdateDisk(PerfDevice d, PerfDetail? detail)
    {
        var active = V(d.Id, "active");
        var read = V(d.Id, "read");
        var write = V(d.Id, "write");
        d.Main.Push(active);
        d.Overlay.Push(V(d.Id, "temp"));
        d.Rate!.Push(read);
        d.Rate2!.Push(write);
        var top = Math.Max(d.Rate.Max, d.Rate2.Max);
        d.Maximum2 = NiceMax(top, 1);
        d.Scale2Text = N(d.Maximum2, "MB/s");
        d.Subtitle = N(active, "%");
        d.Stat("Tempo ativo").Value = N(active, "%");
        d.Stat("Velocidade de leitura").Value = N(read, "MB/s");
        d.Stat("Velocidade de gravação").Value = N(write, "MB/s");
        if (detail?.Disks.FirstOrDefault(x => x.Id == d.Id)?.ResponseMs is { } ms)
            d.Stat("Tempo médio de resposta").Value = ms.ToString(ms >= 10 ? "N0" : "0.0", L.Culture) + " ms";
        if (V(d.Id, "temp") is { } temp) d.Stat("Temperatura").Value = N(temp, "°C");
        if (d.Detail is { } detailText) d.Stat("Tipo").Value = detailText;
    }

    private void UpdateNet(PerfDevice d, PerfDetail? detail)
    {
        var rx = V(d.Id, "rx");
        var tx = V(d.Id, "tx");
        d.Main.Push(rx);
        d.Second!.Push(tx);
        d.Maximum = NiceMax(Math.Max(d.Main.Max, d.Second.Max), 0.1);
        d.ScaleText = N(d.Maximum, "Mbit/s");
        d.Subtitle = $"↓ {N(rx, "Mbit/s")}  ↑ {N(tx, "Mbit/s")}";
        d.Stat("Recebimento").Value = N(rx, "Mbit/s");
        d.Stat("Envio").Value = N(tx, "Mbit/s");
        if (detail?.Networks.FirstOrDefault(x => x.Id == d.Id) is { } net)
        {
            if (net.SpeedMbps is { } speed) d.Stat("Velocidade do link").Value = N(speed, "Mbit/s");
            if (net.IPv4.Count > 0) d.Stat("Endereço IPv4").Value = string.Join(", ", net.IPv4);
            var v6 = net.IPv6.Where(a => !a.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)).ToList();
            if (v6.Count > 0) d.Stat("Endereço IPv6").Value = v6[0];
        }
    }

    private void UpdateGpu(PerfDevice d, PerfDetail? detail)
    {
        var usage = V(d.Id, "usage");
        var temp = V(d.Id, "temp");
        var vram = V(d.Id, "vram_used");
        d.Main.Push(usage);
        d.Subtitle = $"{N(usage, "%")}" + (temp is { } t ? $"  ({N(t, "°C")})" : "");
        d.Stat("Utilização").Value = N(usage, "%");
        d.Overlay.Push(temp);
        if (temp is not null) d.Stat("Temperatura").Value = N(temp, "°C");
        if (V(d.Id, "temp_hotspot") is { } hot) d.Stat("Hot spot").Value = N(hot, "°C");
        if (V(d.Id, "clock") is { } gclock) d.Stat("Velocidade").Value = N(gclock, "MHz");
        if (V(d.Id, "power") is { } gpower) d.Stat("Consumo").Value = N(gpower, "W");
        if (V(d.Id, "fan") is { } rpm) d.Stat("Ventoinha").Value = N(rpm, "RPM") + (V(d.Id, "fan_duty") is { } duty ? $" ({N(duty, "%")})" : "");

        var gpu = detail?.Gpus.FirstOrDefault(x => x.Id == d.Id);
        if (gpu is null)
        {
            if (vram is not null) d.Stat("Memória dedicada").Value = Gb(vram);
            return;
        }
        if (gpu.DedicatedGb is { } dedicatedTotal) d.Stat("Memória dedicada").Value = $"{Gb(vram).Replace(" GB", "")}/{Gb(dedicatedTotal)}";
        d.Stat("Memória compartilhada").Value = $"{Gb(gpu.SharedGb).Replace(" GB", "")}/{Gb(gpu.SharedLimitGb)}";

        // Quatro motores como no Gerenciador: 3D, cópia, decodificação e codificação de vídeo (ou computação).
        var groups = Engines(gpu.Engines);
        var shown = new List<(string Name, double Value)>
        {
            ("3D", groups.GetValueOrDefault("3D")),
            (L.T("Cópia"), groups.GetValueOrDefault("Copy")),
            (L.T("Decodificação de vídeo"), groups.GetValueOrDefault("Decode")),
            groups.ContainsKey("Encode") || !groups.ContainsKey("Compute")
                ? (L.T("Codificação de vídeo"), groups.GetValueOrDefault("Encode"))
                : (L.T("Computação"), groups.GetValueOrDefault("Compute")),
        };
        EnsureMinis(d, shown.Count + 2);
        for (var i = 0; i < shown.Count; i++)
        {
            d.Minis[i].Title = shown[i].Name;
            d.Minis[i].Push(shown[i].Value, N(shown[i].Value, "%"));
        }
        var dedicated = d.Minis[shown.Count];
        dedicated.Title = L.T("Uso de memória dedicada");
        dedicated.Maximum = gpu.DedicatedGb is > 0 ? gpu.DedicatedGb.Value : Math.Max(1, dedicated.Series.Max);
        dedicated.Push(vram, Gb(vram));
        var shared = d.Minis[shown.Count + 1];
        shared.Title = L.T("Uso de memória compartilhada");
        shared.Maximum = gpu.SharedLimitGb is > 0 ? gpu.SharedLimitGb.Value : Math.Max(1, shared.Series.Max);
        shared.Push(gpu.SharedGb, Gb(gpu.SharedGb));
    }

    private static void EnsureMinis(PerfDevice d, int count)
    {
        while (d.Minis.Count < count) d.Minis.Add(new PerfMiniChart("gpu", ""));
    }

    /// <summary>
    /// Agrupa os motores pelo nome, que muda de fabricante para fabricante: NVIDIA "VideoDecode", AMD "Video Decode 1",
    /// "Video Codec 0", Intel "VideoProcessing"... Vale o mais ocupado de cada grupo.
    /// </summary>
    public static Dictionary<string, double> Engines(Dictionary<string, double> engines)
    {
        var result = new Dictionary<string, double>();
        foreach (var (name, value) in engines)
        {
            var n = name.ToLowerInvariant();
            var group = n.Contains("3d") ? "3D"
                : n.Contains("copy") ? "Copy"
                : n.Contains("decode") || n.Contains("codec") ? "Decode"
                : n.Contains("encode") ? "Encode"
                : n.Contains("compute") || n.Contains("cuda") ? "Compute"
                : null;
            if (group is not null) result[group] = Math.Max(result.GetValueOrDefault(group), value);
        }
        return result;
    }

    /// <summary>Escala "redonda" para o gráfico: 1, 2, 5 × 10ⁿ acima do maior valor.</summary>
    public static double NiceMax(double value, double floor)
    {
        if (value <= floor) return floor;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var m in new[] { 1.0, 2, 5, 10 })
            if (m * exp >= value) return m * exp;
        return 10 * exp;
    }
}
