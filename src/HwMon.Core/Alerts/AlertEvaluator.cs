using HwMon.Core.Model;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Alerts;

/// <summary>
/// Avalia as regras sobre uma leitura completa (snapshot) da máquina. Guarda, para cada regra e componente, desde
/// quando a condição vale: o alerta começa quando ela dura o tempo da regra e termina quando fica 1 minuto fora
/// (com uma folga no limite, para um valor oscilando em volta dele não abrir e fechar alertas sem parar).
/// Sem rede nem disco: o serviço chama <see cref="Evaluate"/> e cuida de gravar e avisar.
/// </summary>
public sealed class AlertEvaluator
{
    public static readonly TimeSpan ResolveAfter = TimeSpan.FromMinutes(1);

    private sealed class Track
    {
        public DateTimeOffset? Since;
        public DateTimeOffset? ClearSince;
        /// <summary>Pior valor desde que a condição começou (antes de o alerta abrir também conta).</summary>
        public double? Peak;
        public AlertEvent? Event;
    }

    private readonly Dictionary<(AlertKind, string), Track> _tracks = new();
    private long _nextId;

    public AlertEvaluator(long lastId = 0) => _nextId = lastId;

    /// <summary>Alertas em andamento agora.</summary>
    public List<AlertEvent> Active => _tracks.Values.Select(t => t.Event).OfType<AlertEvent>().OrderBy(e => e.Id).ToList();

    /// <summary>Retoma alertas que estavam abertos quando o serviço parou (continuam valendo até a condição passar).</summary>
    public void Restore(IEnumerable<AlertEvent> active)
    {
        foreach (var e in active)
        {
            _tracks[(e.Kind, e.Component)] = new Track { Since = e.Start, Event = e };
            _nextId = Math.Max(_nextId, e.Id);
        }
    }

    /// <summary>Uma rodada. Devolve os alertas que começaram e os que terminaram nela.</summary>
    public (List<AlertEvent> Started, List<AlertEvent> Ended) Evaluate(
        DateTimeOffset now, IReadOnlyCollection<TelemetryValue> values, IReadOnlyCollection<ComponentDescriptor> components, IReadOnlyList<AlertRule> rules)
    {
        var started = new List<AlertEvent>();
        var ended = new List<AlertEvent>();
        var index = values.GroupBy(v => (v.Component, v.Metric)).ToDictionary(g => g.Key, g => g.First());
        TelemetryValue? Ok(string component, string metric)
            => index.TryGetValue((component, metric), out var v) && v.State == ReadingState.Ok && v.Value is not null ? v : null;

        var seen = new HashSet<(AlertKind, string)>();
        foreach (var rule in rules)
        {
            foreach (var c in components)
            {
                if (Check(rule, c, Ok) is not { } check) continue;
                var key = (rule.Kind, c.Id);
                if (!rule.Enabled)
                {
                    // Regra desligada com alerta aberto: encerra na hora.
                    if (_tracks.Remove(key, out var off) && off.Event is { } e0) { e0.End = now; ended.Add(e0); }
                    continue;
                }
                seen.Add(key);
                if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new Track();

                if (check.Firing)
                {
                    t.ClearSince = null;
                    t.Since ??= now;
                    t.Peak = Worse(rule.Kind, t.Peak, check.Value);
                    if (t.Event is { } open)
                    {
                        open.Value = Worse(rule.Kind, open.Value, check.Value);
                        open.Note = check.Note ?? open.Note;
                    }
                    else if (now - t.Since >= TimeSpan.FromMinutes(rule.Minutes))
                    {
                        t.Event = new AlertEvent
                        {
                            Id = ++_nextId, Kind = rule.Kind, Component = c.Id, Subject = c.Name, Value = t.Peak,
                            Threshold = rule.Threshold, Note = check.Note, Start = now,
                        };
                        started.Add(t.Event);
                    }
                }
                else if (!check.Holding)
                {
                    t.Since = null;
                    if (t.Event is null) t.Peak = null;
                    if (t.Event is { } open)
                    {
                        t.ClearSince ??= now;
                        if (now - t.ClearSince >= ResolveAfter)
                        {
                            open.End = now;
                            ended.Add(open);
                            t.Event = null;
                            t.ClearSince = null;
                        }
                    }
                }
            }
        }

        // Componente que sumiu (periférico desligado, disco removido) ou regra que deixou de valer para ele.
        foreach (var key in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var t = _tracks[key];
            if (t.Event is null) { _tracks.Remove(key); continue; }
            t.ClearSince ??= now;
            if (now - t.ClearSince < ResolveAfter) continue;
            t.Event.End = now;
            ended.Add(t.Event);
            _tracks.Remove(key);
        }
        return (started, ended);
    }

    /// <summary>
    /// Firing: a condição vale. Holding: está na folga (não abre alerta novo, mas não fecha o aberto).
    /// Nulo: a regra não se aplica a este componente.
    /// </summary>
    private readonly record struct Condition(bool Firing, bool Holding, double? Value, string? Note);

    private static Condition? Check(AlertRule rule, ComponentDescriptor c, Func<string, string, TelemetryValue?> ok)
    {
        switch (rule.Kind)
        {
            case AlertKind.CpuTemp when c.Kind == ComponentKind.Cpu:
            case AlertKind.GpuTemp when c.Kind == ComponentKind.Gpu:
            {
                var t = ok(c.Id, "temp")?.Value;
                if (t is null) return new Condition(false, false, null, null);
                return new Condition(t >= rule.Threshold, t >= rule.Threshold - 3, t, null);
            }
            case AlertKind.DiskHealth when c.Kind == ComponentKind.Disk:
            {
                var h = ok(c.Id, "health");
                // 0 = Saudável, 5 = Desconhecida (sem SMART): não avisa.
                var bad = h?.Value is { } code && code is not 0 and not 5;
                return new Condition(bad, false, h?.Value, h?.Note);
            }
            // Só volumes com letra: partição de recuperação vive cheia e não é para o usuário mexer.
            case AlertKind.VolumeFull when c.Kind == ComponentKind.Volume && ComponentKind.HasDriveLetter(c.Name):
            {
                var used = ok(c.Id, "used")?.Value;
                var total = ok(c.Id, "total")?.Value;
                if (used is null || total is not > 0) return new Condition(false, false, null, null);
                var pct = Math.Round(used.Value / total.Value * 100, 1);
                return new Condition(pct >= rule.Threshold, pct >= rule.Threshold - 1, pct, null);
            }
            case AlertKind.BatteryLow when c.Kind == ComponentKind.Peripheral:
            {
                var b = ok(c.Id, "battery");
                if (b?.Value is not { } pct) return new Condition(false, false, null, null);
                var charging = b.Note?.StartsWith("carreg", StringComparison.OrdinalIgnoreCase) == true
                    || b.Note?.StartsWith("charg", StringComparison.OrdinalIgnoreCase) == true;
                if (charging) return new Condition(false, false, pct, null);
                return new Condition(pct <= rule.Threshold, pct <= rule.Threshold + 5, pct, null);
            }
            default:
                return null;
        }
    }

    private static double? Worse(AlertKind kind, double? a, double? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return kind == AlertKind.BatteryLow ? Math.Min(a.Value, b.Value) : Math.Max(a.Value, b.Value);
    }
}
