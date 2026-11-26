using HwMon.Core.Alerts;
using HwMon.Core.Model;
using HwMon.Core.Telemetry;
using Xunit;

namespace HwMon.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);

    private static readonly List<ComponentDescriptor> Components =
    [
        new() { Id = "cpu:0", Kind = ComponentKind.Cpu, Name = "Ryzen" },
        new() { Id = "volume:C", Kind = ComponentKind.Volume, Name = "C:" },
        new() { Id = "disk:1", Kind = ComponentKind.Disk, Name = "HDD" },
        new() { Id = "peripheral:g733", Kind = ComponentKind.Peripheral, Name = "G733" },
    ];

    private static TelemetryValue V(string component, string metric, double? value, string? note = null)
        => new() { Component = component, Metric = metric, Value = value, State = ReadingState.Ok, Note = note };

    private static List<AlertRule> Rules() { var s = new AlertSettings(); s.Normalize(); return s.Rules; }

    [Fact]
    public void Cpu_temperature_must_last_the_rule_time_and_resolves_after_a_minute_below_the_margin()
    {
        var e = new AlertEvaluator();
        var rules = Rules(); // CPU: 95 °C por 2 min
        Assert.Empty(e.Evaluate(T0, [V("cpu:0", "temp", 97)], Components, rules).Started);
        Assert.Empty(e.Evaluate(T0.AddSeconds(90), [V("cpu:0", "temp", 98)], Components, rules).Started);
        var started = e.Evaluate(T0.AddMinutes(2), [V("cpu:0", "temp", 96)], Components, rules).Started;
        var alert = Assert.Single(started);
        Assert.Equal(AlertKind.CpuTemp, alert.Kind);
        Assert.Equal(1, alert.Id);

        // Na folga (93 °C, limite − 3) não fecha; abaixo dela fecha depois de 1 minuto.
        Assert.Empty(e.Evaluate(T0.AddMinutes(3), [V("cpu:0", "temp", 93)], Components, rules).Ended);
        Assert.Empty(e.Evaluate(T0.AddMinutes(10), [V("cpu:0", "temp", 93)], Components, rules).Ended);
        Assert.Empty(e.Evaluate(T0.AddMinutes(11), [V("cpu:0", "temp", 70)], Components, rules).Ended);
        var ended = Assert.Single(e.Evaluate(T0.AddMinutes(12), [V("cpu:0", "temp", 70)], Components, rules).Ended);
        Assert.Equal(98, ended.Value); // o pior valor enquanto durou
        Assert.NotNull(ended.End);
        Assert.Empty(e.Active);
    }

    [Fact]
    public void A_short_spike_does_not_alert()
    {
        var e = new AlertEvaluator();
        var rules = Rules();
        e.Evaluate(T0, [V("cpu:0", "temp", 99)], Components, rules);
        e.Evaluate(T0.AddSeconds(30), [V("cpu:0", "temp", 60)], Components, rules);
        Assert.Empty(e.Evaluate(T0.AddMinutes(3), [V("cpu:0", "temp", 99)], Components, rules).Started);
    }

    [Fact]
    public void Volume_disk_and_battery()
    {
        var e = new AlertEvaluator();
        var rules = Rules();
        var values = new List<TelemetryValue>
        {
            V("volume:C", "used", 97), V("volume:C", "total", 100),
            V("disk:1", "health", 1, "Atenção"),
            V("peripheral:g733", "battery", 12, "descarregando"),
        };
        var first = e.Evaluate(T0, values, Components, rules).Started;
        // Disco e bateria na hora; volume só depois de 5 minutos.
        Assert.Equal([AlertKind.DiskHealth, AlertKind.BatteryLow], first.Select(a => a.Kind).ToList());
        Assert.Equal("Atenção", first[0].Note);
        var later = e.Evaluate(T0.AddMinutes(5), values, Components, rules).Started;
        Assert.Equal(AlertKind.VolumeFull, Assert.Single(later).Kind);
    }

    [Fact]
    public void Laptop_battery_low_charged_and_health()
    {
        var e = new AlertEvaluator();
        List<ComponentDescriptor> laptop = [new() { Id = "battery:0", Kind = ComponentKind.Battery, Name = "Samsung" }];
        var rules = Rules();
        rules.Single(r => r.Kind == AlertKind.LaptopCharged).Enabled = true; // desligado por padrão

        // Na bateria, 18 %: fraca. Saúde 65 % (limite 70): avisa.
        var started = e.Evaluate(T0, [V("battery:0", "charge", 18, "na bateria"), V("battery:0", "health", 65)], laptop, rules).Started;
        Assert.Equal([AlertKind.LaptopBatteryLow, AlertKind.LaptopBatteryHealth], started.Select(a => a.Kind).OrderBy(k => k).ToList());

        // Ligou na tomada: "fraca" passa depois de 1 minuto; ao chegar a 80 % avisa para tirar.
        e.Evaluate(T0.AddMinutes(1), [V("battery:0", "charge", 19, "carregando"), V("battery:0", "health", 65)], laptop, rules);
        var ended = e.Evaluate(T0.AddMinutes(2), [V("battery:0", "charge", 25, "carregando"), V("battery:0", "health", 65)], laptop, rules).Ended;
        Assert.Equal(AlertKind.LaptopBatteryLow, Assert.Single(ended).Kind);
        var full = e.Evaluate(T0.AddMinutes(60), [V("battery:0", "charge", 81, "carregando"), V("battery:0", "health", 65)], laptop, rules).Started;
        Assert.Equal(AlertKind.LaptopCharged, Assert.Single(full).Kind);
    }

    [Fact]
    public void Volume_without_a_letter_does_not_alert()
    {
        var e = new AlertEvaluator();
        List<ComponentDescriptor> components = [new() { Id = "volume:rec", Kind = ComponentKind.Volume, Name = "SAMSUNG_REC2" }];
        var values = new List<TelemetryValue> { V("volume:rec", "used", 99), V("volume:rec", "total", 100) };
        e.Evaluate(T0, values, components, Rules());
        Assert.Empty(e.Evaluate(T0.AddMinutes(10), values, components, Rules()).Started);
        Assert.True(ComponentKind.HasDriveLetter("C: Sistema"));
        Assert.False(ComponentKind.HasDriveLetter("SYSTEM"));
    }

    [Fact]
    public void Charging_battery_and_unknown_disk_health_do_not_alert()
    {
        var e = new AlertEvaluator();
        var values = new List<TelemetryValue>
        {
            V("disk:1", "health", 5, "Desconhecida"),
            V("peripheral:g733", "battery", 5, "carregando"),
        };
        Assert.Empty(e.Evaluate(T0, values, Components, Rules()).Started);
    }

    [Fact]
    public void Disabled_rule_closes_its_open_alert()
    {
        var e = new AlertEvaluator();
        var rules = Rules();
        e.Evaluate(T0, [V("disk:1", "health", 2)], Components, rules);
        rules.First(r => r.Kind == AlertKind.DiskHealth).Enabled = false;
        Assert.Single(e.Evaluate(T0.AddSeconds(5), [V("disk:1", "health", 2)], Components, rules).Ended);
    }

    [Fact]
    public void Disappeared_peripheral_resolves_and_restored_alerts_keep_their_id()
    {
        var e = new AlertEvaluator();
        var rules = Rules();
        var alert = Assert.Single(e.Evaluate(T0, [V("peripheral:g733", "battery", 10)], Components, rules).Started);
        var noHeadset = Components.Where(c => c.Kind != ComponentKind.Peripheral).ToList();
        Assert.Empty(e.Evaluate(T0.AddSeconds(10), [], noHeadset, rules).Ended);
        Assert.Single(e.Evaluate(T0.AddMinutes(2), [], noHeadset, rules).Ended);

        var restarted = new AlertEvaluator();
        restarted.Restore([new AlertEvent { Id = 7, Kind = AlertKind.CpuTemp, Component = "cpu:0", Start = T0 }]);
        Assert.Empty(restarted.Evaluate(T0.AddMinutes(1), [V("cpu:0", "temp", 99)], Components, rules).Started);
        Assert.Equal(8, Assert.Single(restarted.Evaluate(T0.AddMinutes(1), [V("disk:1", "health", 2), V("cpu:0", "temp", 99)], Components, rules).Started).Id);
        Assert.NotEqual(0, alert.Id);
    }

    [Fact]
    public void Normalize_restores_missing_rules_and_clamps()
    {
        var s = new AlertSettings { Rules = [new AlertRule { Kind = AlertKind.CpuTemp, Threshold = 500, Minutes = -3 }] };
        s.Normalize();
        Assert.Equal(Enum.GetValues<AlertKind>().Length, s.Rules.Count);
        Assert.Equal(120, s.Rules[0].Threshold);
        Assert.Equal(0, s.Rules[0].Minutes);
    }
}

[Collection("Idioma")]
public class AlertTextTests
{
    [Fact]
    public void Alert_texts_in_english()
    {
        HwMon.Core.L.Use("en");
        try
        {
            var volume = new AlertEvent { Kind = AlertKind.VolumeFull, Subject = "D:", Value = 96.4, Threshold = 95 };
            Assert.Equal("NAS: Volume almost full", AlertText.Title(volume, "NAS"));
            Assert.Equal("D: is 96 % full (limit 95 %).", AlertText.Body(volume));
            var disk = new AlertEvent { Kind = AlertKind.DiskHealth, Subject = "WDC", Note = "Atenção", End = DateTimeOffset.Now };
            Assert.Equal("Resolved: WDC: Windows reports \"Warning\". Back up and check the disk.", AlertText.Body(disk));
        }
        finally
        {
            HwMon.Core.L.Use("pt");
        }
    }
}
