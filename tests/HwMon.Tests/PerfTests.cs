using System.Text.Json;
using HwMon.Core.Collection;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;
using Xunit;

namespace HwMon.Tests;

/// <summary>Aba Desempenho: contadores do Windows desta máquina e o formato de /v1/perf.</summary>
public class PerfTests
{
    [Fact]
    public void Collector_reads_one_value_per_logical_processor_and_memory_lists()
    {
        var perf = new PerfCounterCollector(Array.Empty<GpuInfo>(), new List<string>());
        perf.Prime();
        Thread.Sleep(600);
        _ = perf.Sample().ToList();

        var d = perf.Detail;
        Assert.NotNull(d);
        Assert.Equal(Environment.ProcessorCount, d.Cores.Count);
        Assert.All(d.Cores, c => Assert.InRange(c, 0, 100));
        Assert.True(d.BaseMhz > 0);
        Assert.True(d.StandbyBytes >= 0);
        Assert.True(d.FreeBytes >= 0);
    }

    [Fact]
    public void Detail_round_trips_through_json()
    {
        var detail = new PerfDetail
        {
            Time = DateTimeOffset.Now,
            Cpu = { Cores = [12.5, 3], BaseMhz = 4300, Processes = 300, Threads = 5000, Handles = 150000, UptimeSeconds = 3600 },
            Memory = new PerfMemory { TotalGb = 96, AvailableGb = 80, InUseGb = 15.5, StandbyGb = 40, FreeGb = 40, ModifiedGb = 0.5 },
            Disks = [new PerfDisk { Id = "disk:abc", ResponseMs = 0.3 }],
            Gpus = [new PerfGpu { Id = "gpu:10de", Engines = new() { ["3D"] = 4, ["VideoDecode"] = 0 }, SharedGb = 0.2, SharedLimitGb = 48 }],
            Networks = [new PerfNet { Id = "network:x", IPv4 = ["192.168.0.10"] }],
        };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var back = JsonSerializer.Deserialize<PerfDetail>(JsonSerializer.Serialize(detail, web), web)!;
        Assert.Equal([12.5, 3], back.Cpu.Cores);
        Assert.Equal(15.5, back.Memory!.InUseGb);
        Assert.Equal(4, back.Gpus[0].Engines["3D"]);
        Assert.Equal("192.168.0.10", back.Networks[0].IPv4[0]);
    }
}
