using System.Text.Json.Serialization;

namespace HwMon.Core.Alerts;

/// <summary>O que a regra vigia.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertKind
{
    /// <summary>Temperatura da CPU acima do limite (°C) por N minutos.</summary>
    CpuTemp,
    /// <summary>Temperatura de uma placa de vídeo acima do limite (°C) por N minutos.</summary>
    GpuTemp,
    /// <summary>Disco com saúde diferente de "Saudável" (SMART / Windows).</summary>
    DiskHealth,
    /// <summary>Volume com mais que o limite (%) ocupado.</summary>
    VolumeFull,
    /// <summary>Periférico com bateria abaixo do limite (%), fora do carregador.</summary>
    BatteryLow,
}

/// <summary>Uma regra. Avaliada no serviço de cada máquina, mesmo com o painel fechado.</summary>
public sealed class AlertRule
{
    public AlertKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>°C (temperaturas) ou % (volume cheio, bateria). Sem uso na saúde do disco.</summary>
    public double Threshold { get; set; }
    /// <summary>Por quanto tempo a condição precisa durar antes de avisar (0 = na hora).</summary>
    public int Minutes { get; set; }
}

/// <summary>Para onde o serviço manda os avisos, além do painel. Tudo opcional; vazio = desligado.</summary>
public sealed class AlertChannels
{
    /// <summary>Tópico do ntfy (https://ntfy.sh/meu-topico ou servidor próprio).</summary>
    public string? NtfyUrl { get; set; }
    /// <summary>URL do webhook de um canal do Discord.</summary>
    public string? DiscordWebhook { get; set; }
    public string? TelegramToken { get; set; }
    public string? TelegramChatId { get; set; }
    /// <summary>Também avisar quando o problema passar.</summary>
    public bool NotifyResolved { get; set; } = true;
}

public sealed class AlertSettings
{
    public List<AlertRule> Rules { get; set; } = Defaults();
    public AlertChannels Channels { get; set; } = new();

    public static List<AlertRule> Defaults() =>
    [
        new() { Kind = AlertKind.CpuTemp, Threshold = 95, Minutes = 2 },
        new() { Kind = AlertKind.GpuTemp, Threshold = 90, Minutes = 2 },
        new() { Kind = AlertKind.DiskHealth, Minutes = 0 },
        new() { Kind = AlertKind.VolumeFull, Threshold = 95, Minutes = 5 },
        new() { Kind = AlertKind.BatteryLow, Threshold = 15, Minutes = 0 },
    ];

    /// <summary>Uma regra de cada tipo, nesta ordem; tipos que faltam (arquivo antigo) voltam com o padrão.</summary>
    public void Normalize()
    {
        var byKind = Rules.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.First());
        Rules = Defaults().Select(d => byKind.TryGetValue(d.Kind, out var r) ? r : d).ToList();
        foreach (var r in Rules)
        {
            r.Minutes = Math.Clamp(r.Minutes, 0, 120);
            r.Threshold = r.Kind switch
            {
                AlertKind.CpuTemp or AlertKind.GpuTemp => Math.Clamp(r.Threshold, 40, 120),
                AlertKind.VolumeFull => Math.Clamp(r.Threshold, 50, 100),
                AlertKind.BatteryLow => Math.Clamp(r.Threshold, 1, 90),
                _ => 0,
            };
        }
        Channels ??= new();
    }
}

/// <summary>Um alerta: começou, e talvez já tenha passado.</summary>
public sealed class AlertEvent
{
    /// <summary>Crescente na máquina (o painel avisa só os que ainda não viu).</summary>
    public long Id { get; set; }
    public AlertKind Kind { get; set; }
    /// <summary>Id do componente (cpu:0, volume:C, disk:…).</summary>
    public string Component { get; set; } = "";
    /// <summary>Nome do componente no momento do alerta ("AMD Ryzen 9 9950X", "C:", "G733").</summary>
    public string Subject { get; set; } = "";
    /// <summary>Valor que disparou (pior valor enquanto durou) e o limite da regra.</summary>
    public double? Value { get; set; }
    public double Threshold { get; set; }
    /// <summary>Saúde do disco: o texto do Windows ("Atenção", "Com problema").</summary>
    public string? Note { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset? End { get; set; }
    [JsonIgnore] public bool Active => End is null;
}

/// <summary>Resposta de GET /v1/alerts.</summary>
public sealed class AlertsResponse
{
    public List<AlertRule> Rules { get; set; } = new();
    /// <summary>Canais com os segredos trocados por "•••" (o painel só mostra se está configurado).</summary>
    public AlertChannels Channels { get; set; } = new();
    public List<AlertEvent> Active { get; set; } = new();
    public List<AlertEvent> History { get; set; } = new();
}

/// <summary>Resultado do envio de teste para cada canal configurado.</summary>
public sealed class AlertTestResult
{
    public string Channel { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
