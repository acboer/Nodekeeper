namespace HwMon.Core.Telemetry;

/// <summary>
/// Detalhes para a aba Desempenho (como a do Gerenciador de Tarefas), que não cabem no snapshot: uso por núcleo,
/// memória detalhada, motores da GPU, tempo de resposta dos discos, contagens do sistema. Serviço 0.16+ (/v1/perf).
/// Os ids são os mesmos dos componentes do descritor.
/// </summary>
public sealed class PerfDetail
{
    public DateTimeOffset Time { get; set; }
    public PerfCpu Cpu { get; set; } = new();
    public PerfMemory? Memory { get; set; }
    public List<PerfDisk> Disks { get; set; } = new();
    public List<PerfGpu> Gpus { get; set; } = new();
    public List<PerfNet> Networks { get; set; } = new();
}

public sealed class PerfCpu
{
    /// <summary>Uso (%) de cada processador lógico, na ordem do Windows.</summary>
    public List<double> Cores { get; set; } = new();
    /// <summary>Velocidade de base (MHz), a "Velocidade base" do Gerenciador de Tarefas.</summary>
    public double? BaseMhz { get; set; }
    public int? Processes { get; set; }
    public int? Threads { get; set; }
    public int? Handles { get; set; }
    public double UptimeSeconds { get; set; }
}

/// <summary>Memória em GB. Composição como no Gerenciador de Tarefas: em uso + modificada + em espera + livre = total.</summary>
public sealed class PerfMemory
{
    public double TotalGb { get; set; }
    public double AvailableGb { get; set; }
    public double InUseGb { get; set; }
    public double? ModifiedGb { get; set; }
    public double? StandbyGb { get; set; }
    public double? FreeGb { get; set; }
    public double? CommittedGb { get; set; }
    public double? CommitLimitGb { get; set; }
    /// <summary>Em cache = em espera + modificada.</summary>
    public double? CachedGb { get; set; }
    public double? PagedPoolGb { get; set; }
    public double? NonPagedPoolGb { get; set; }
}

public sealed class PerfDisk
{
    public string Id { get; set; } = "";
    /// <summary>Tempo médio de resposta (ms).</summary>
    public double? ResponseMs { get; set; }
}

public sealed class PerfGpu
{
    public string Id { get; set; } = "";
    /// <summary>Uso (%) por tipo de motor: "3D", "Copy", "VideoDecode", "VideoEncode", "Compute_0"...</summary>
    public Dictionary<string, double> Engines { get; set; } = new();
    public double? DedicatedGb { get; set; }
    public double? SharedGb { get; set; }
    /// <summary>Limite da memória compartilhada (metade da RAM, como no Gerenciador de Tarefas).</summary>
    public double? SharedLimitGb { get; set; }
}

public sealed class PerfNet
{
    public string Id { get; set; } = "";
    public string? Type { get; set; }
    public double? SpeedMbps { get; set; }
    public List<string> IPv4 { get; set; } = new();
    public List<string> IPv6 { get; set; } = new();
}
