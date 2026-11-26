using System.Management;
using System.Runtime.InteropServices;
using HwMon.Core.Model;

namespace HwMon.Core.Collection;

/// <summary>
/// Bateria do notebook, pelo próprio Windows (sem driver): carga e tomada pelo GetSystemPowerStatus (o mesmo número
/// da barra de tarefas); potência, capacidades e ciclos pelas classes de bateria do WMI (root\wmi, precisam de
/// administrador: o serviço roda como sistema). Também o modo de energia do Windows (eficiência, equilibrado,
/// desempenho). Num PC sem bateria, <see cref="Detect"/> devolve nulo e nada disso aparece.
/// </summary>
public sealed class LaptopBattery
{
    private const string Source = "battery";
    private readonly List<string> _problems;
    private DateTimeOffset _staticRead = DateTimeOffset.MinValue;
    private double? _designWh, _fullWh;
    private int? _cycles;

    private LaptopBattery(List<string> problems, string name, string? chemistry)
    {
        _problems = problems;
        Name = name;
        Chemistry = chemistry;
    }

    public string Name { get; }
    public string? Chemistry { get; }
    public double? DesignWh => _designWh;

    /// <summary>
    /// Bateria presente? Pelo WMI (Win32_Battery) ou pelo GetSystemPowerStatus (flag 128 = sem bateria). No serviço
    /// (sessão 0) o GetSystemPowerStatus pode responder "desconhecido" (255): então vale o WMI.
    /// </summary>
    public static LaptopBattery? Detect(List<string> problems)
    {
        var power = GetSystemPowerStatus(out var s) && s.BatteryFlag is not 128 and not 255;
        if (!power && Win32Battery() is null) return null;
        string name = "Bateria";
        string? chemistry = null;
        try
        {
            if (Wmi.Query(@"root\wmi", "SELECT DeviceName, ManufactureName, Chemistry FROM BatteryStaticData").FirstOrDefault() is { } st)
            {
                var device = Wmi.Str(st, "DeviceName")?.Trim();
                var maker = Wmi.Str(st, "ManufactureName")?.Trim();
                name = string.Join(" ", new[] { maker, device }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } n ? n : name;
                chemistry = ChemistryName(Wmi.Get<uint?>(st, "Chemistry"));
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            problems.Add($"Bateria: dados do fabricante indisponíveis: {ex.Message}");
        }
        var battery = new LaptopBattery(problems, name, chemistry);
        battery.ReadStatic();
        return battery;
    }

    /// <summary>"LION" etc. vêm como 4 letras ASCII num inteiro (BatteryStaticData.Chemistry).</summary>
    private static string? ChemistryName(uint? raw)
    {
        if (raw is not { } v || v == 0) return null;
        var text = new string([(char)(v & 0xFF), (char)((v >> 8) & 0xFF), (char)((v >> 16) & 0xFF), (char)((v >> 24) & 0xFF)]).Trim('\0', ' ').ToUpperInvariant();
        return text switch { "LION" or "LI-I" => "Li-ion", "LIP" or "LIPO" => "Li-Po", "NIMH" => "NiMH", "" => null, _ => text };
    }

    /// <summary>Capacidade de fábrica, atual e ciclos mudam devagar: relê a cada 10 min.</summary>
    private void ReadStatic()
    {
        _staticRead = DateTimeOffset.Now;
        try
        {
            var design = Wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData").Sum(o => (double)(Wmi.Get<uint?>(o, "DesignedCapacity") ?? 0));
            var full = Wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Sum(o => (double)(Wmi.Get<uint?>(o, "FullChargedCapacity") ?? 0));
            _designWh = design > 0 ? design / 1000 : null;
            _fullWh = full > 0 ? full / 1000 : null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Problem($"Bateria: capacidade indisponível: {ex.Message}");
        }
        try
        {
            // Nem todo notebook informa ciclos (a classe existe e devolve 0, ou nem existe).
            var cycles = Wmi.Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount").Select(o => (int)(Wmi.Get<uint?>(o, "CycleCount") ?? 0)).DefaultIfEmpty(0).Max();
            _cycles = cycles > 0 ? cycles : null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            _cycles = null;
        }
    }

    private void Problem(string message)
    {
        if (!_problems.Contains(message)) _problems.Add(message);
    }

    public IEnumerable<Reading> Sample()
    {
        if (DateTimeOffset.Now - _staticRead >= TimeSpan.FromMinutes(10)) ReadStatic();
        var readings = new List<Reading>();
        // Carga e tomada: GetSystemPowerStatus; se ele não souber (255, comum no serviço), o Win32_Battery.
        var known = GetSystemPowerStatus(out var s) && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;
        if (!known && Win32Battery() is { } w)
        {
            s.BatteryLifePercent = (byte)Math.Clamp(w.Charge ?? 255, 0, 255);
            s.ACLineStatus = (byte)(w.Status is 2 or 3 or 6 or 7 or 8 or 9 or 11 ? 1 : 0); // na tomada, carregada ou carregando
            s.BatteryFlag = (byte)(w.Status is 6 or 7 or 8 or 9 or 11 ? 8 : 0);
            s.BatteryLifeTime = w.RunMinutes is > 0 and < 71582788 ? (uint)(w.RunMinutes.Value * 60) : 0xFFFFFFFF;
        }

        // Potência e capacidade restante (mWh, mW). ChargeRate/DischargeRate valem 0 quando não se aplicam.
        double? remainingWh = null, chargeW = null, dischargeW = null;
        bool? charging = null, online = null;
        try
        {
            foreach (var b in Wmi.Query(@"root\wmi", "SELECT RemainingCapacity, ChargeRate, DischargeRate, PowerOnline, Charging, Discharging FROM BatteryStatus"))
            {
                remainingWh = (remainingWh ?? 0) + (Wmi.Get<uint?>(b, "RemainingCapacity") ?? 0) / 1000.0;
                var c = (int)(Wmi.Get<uint?>(b, "ChargeRate") ?? 0);
                var d = (int)(Wmi.Get<uint?>(b, "DischargeRate") ?? 0);
                if (c > 0) chargeW = (chargeW ?? 0) + c / 1000.0;
                if (d > 0) dischargeW = (dischargeW ?? 0) + d / 1000.0;
                charging = (charging ?? false) || Wmi.Get<bool?>(b, "Charging") == true;
                online = (online ?? false) || Wmi.Get<bool?>(b, "PowerOnline") == true;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Problem($"Bateria: potência indisponível: {ex.Message}");
        }

        var plugged = s.ACLineStatus == 1 || online == true;
        var isCharging = (s.BatteryFlag & 8) != 0 || charging == true;
        var state = isCharging ? "carregando" : plugged ? "na tomada" : "na bateria";

        if (s.BatteryLifePercent <= 100)
            readings.Add(Reading.Ok("battery", "0", "charge", s.BatteryLifePercent, "%", Source, state));
        else
            readings.Add(Reading.Unavailable("battery", "0", "charge", "%", Source, "o Windows não informou a carga"));
        // 1 = na tomada; vira "tempo na tomada/na bateria por dia" no histórico de uso.
        readings.Add(Reading.Ok("battery", "0", "plugged", plugged ? 1 : 0, "", Source, state));

        if (!plugged && dischargeW is { } dw) readings.Add(Reading.Ok("battery", "0", "discharge", dw, "W", Source, "consumo tirado da bateria"));
        else readings.Add(Reading.Ok("battery", "0", "discharge", 0, "W", Source, plugged ? "na tomada" : "o Windows não informou"));
        readings.Add(Reading.Ok("battery", "0", "charge_rate", isCharging && chargeW is { } cw ? cw : 0, "W", Source, isCharging ? "potência de carga" : "não está carregando"));

        // Tempo restante: o do Windows (segundos) quando houver; senão capacidade ÷ potência.
        if (!plugged)
        {
            double? minutes = s.BatteryLifeTime is > 0 and < 0xFFFFFFFF ? s.BatteryLifeTime / 60.0
                : remainingWh is { } rw && dischargeW is > 0.5 ? rw / dischargeW.Value * 60 : null;
            readings.Add(minutes is { } m
                ? Reading.Ok("battery", "0", "runtime", m, "min", Source, "estimativa do Windows pelo consumo atual")
                : Reading.Unavailable("battery", "0", "runtime", "min", Source, "calculando"));
        }
        else
        {
            readings.Add(Reading.Unavailable("battery", "0", "runtime", "min", Source, "na tomada"));
        }
        if (isCharging && remainingWh is { } r2 && _fullWh is { } f2 && chargeW is > 0.5)
            readings.Add(Reading.Ok("battery", "0", "charge_eta", Math.Max(0, f2 - r2) / chargeW.Value * 60, "min", Source, "pela potência de carga atual"));
        else
            readings.Add(Reading.Unavailable("battery", "0", "charge_eta", "min", Source, isCharging ? "calculando" : state));

        if (_fullWh is { } full)
        {
            readings.Add(Reading.Ok("battery", "0", "capacity", full, "Wh", Source, "capacidade com carga completa hoje"));
            if (_designWh is { } design && design > 0)
                readings.Add(Reading.Ok("battery", "0", "health", Math.Min(100, full / design * 100), "%", Source,
                    $"{full:0.#} de {design:0.#} Wh de fábrica"));
        }
        if (_designWh is { } d2) readings.Add(Reading.Ok("battery", "0", "design", d2, "Wh", Source, "capacidade de fábrica"));
        if (remainingWh is { } rem) readings.Add(Reading.Ok("battery", "0", "remaining", rem, "Wh", Source));
        if (_cycles is { } cy) readings.Add(Reading.Ok("battery", "0", "cycles", cy, "", Source, "ciclos de carga informados pela bateria"));

        if (PowerMode() is { } mode) readings.Add(Reading.Ok("battery", "0", "power_mode", mode.Value, "", Source, mode.Name));
        return readings;
    }

    /// <summary>Win32_Battery: carga (%), BatteryStatus (1 na bateria, 2 na tomada, 3 carregada, 6–9/11 carregando) e minutos.</summary>
    private static (int? Charge, int? Status, int? RunMinutes)? Win32Battery()
    {
        try
        {
            var b = Wmi.Query(Wmi.Cimv2, "SELECT EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime FROM Win32_Battery").FirstOrDefault();
            if (b is null) return null;
            return (Wmi.Get<int?>(b, "EstimatedChargeRemaining"), Wmi.Get<int?>(b, "BatteryStatus"), Wmi.Get<int?>(b, "EstimatedRunTime"));
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Texto para "Nodekeeper.Service.exe bateria": o que cada fonte responde (diagnóstico em notebooks).</summary>
    public static string Diagnose()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(GetSystemPowerStatus(out var s)
            ? $"GetSystemPowerStatus: tomada={s.ACLineStatus} flag={s.BatteryFlag} carga={s.BatteryLifePercent} segundos={s.BatteryLifeTime}"
            : "GetSystemPowerStatus: falhou");
        sb.AppendLine(Win32Battery() is { } w ? $"Win32_Battery: carga={w.Charge} estado={w.Status} minutos={w.RunMinutes}" : "Win32_Battery: nenhuma");
        foreach (var cls in new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount" })
        {
            try
            {
                var rows = Wmi.Query(@"rootwmi", $"SELECT * FROM {cls}");
                sb.AppendLine($"{cls}: {rows.Count} linha(s)");
                foreach (var r in rows)
                    foreach (var p in r.Properties)
                        if (p.Value is not null && p.Name is not ("InstanceName" or "Tag" or "Active"))
                            sb.AppendLine($"  {p.Name} = {(p.Value is Array a ? string.Join(",", a.Cast<object>()) : p.Value)}");
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                sb.AppendLine($"{cls}: erro {ex.Message}");
            }
        }
        var problems = new List<string>();
        var battery = Detect(problems);
        sb.AppendLine(battery is null ? "Detectada: não" : $"Detectada: sim ({battery.Name}, {battery.Chemistry}, {battery.DesignWh} Wh)");
        if (battery is not null)
            foreach (var r in battery.Sample()) sb.AppendLine($"  {r.Metric} = {r.Value} {r.Unit} [{r.State}] {r.Note}");
        foreach (var p in problems) sb.AppendLine("(!) " + p);
        return sb.ToString();
    }

    /// <summary>Modo de energia do Windows 10/11 (Configurações › Energia): 0 eficiência, 1 equilibrado, 2 desempenho.</summary>
    public static (int Value, string Name)? PowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return null;
            return g.ToString().ToLowerInvariant() switch
            {
                "961cc777-2547-4f9d-8174-7d86181b8a7a" => (0, "Melhor eficiência de energia"),
                "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => (0, "Economia de bateria"),
                "ded574b5-45a0-4f42-8737-46345c09c238" => (2, "Melhor desempenho"),
                _ => (1, "Equilibrado"),
            };
        }
        catch (EntryPointNotFoundException)
        {
            return null; // Windows sem modos de energia
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
}
