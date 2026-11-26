namespace HwMon.Core.Alerts;

/// <summary>
/// Textos de um alerta, no idioma atual (<see cref="L"/>): o serviço usa para ntfy/Discord/Telegram (no idioma do
/// Windows dele) e o painel para a notificação e a lista (no idioma escolhido no painel).
/// </summary>
public static class AlertText
{
    /// <summary>Nome da regra ("Temperatura da CPU").</summary>
    public static string RuleName(AlertKind kind) => L.T(kind switch
    {
        AlertKind.CpuTemp => "Temperatura da CPU",
        AlertKind.GpuTemp => "Temperatura da placa de vídeo",
        AlertKind.DiskHealth => "Saúde do disco",
        AlertKind.VolumeFull => "Volume quase cheio",
        AlertKind.BatteryLow => "Bateria fraca",
        AlertKind.LaptopBatteryLow => "Bateria do notebook fraca",
        AlertKind.LaptopCharged => "Notebook carregado",
        AlertKind.LaptopBatteryHealth => "Saúde da bateria do notebook",
        _ => kind.ToString(),
    });

    /// <summary>"NAS: Volume quase cheio".</summary>
    public static string Title(AlertEvent e, string machine) => $"{machine}: {RuleName(e.Kind)}";

    /// <summary>"D: está 96 % cheio (limite 95 %)." — o que aconteceu, com o valor.</summary>
    public static string Body(AlertEvent e)
    {
        var v = e.Value is { } x ? x.ToString("0", L.Culture) : "?";
        var limit = e.Threshold.ToString("0", L.Culture);
        var text = e.Kind switch
        {
            AlertKind.CpuTemp or AlertKind.GpuTemp => L.T("{0} chegou a {1} °C (limite {2} °C).", e.Subject, v, limit),
            AlertKind.DiskHealth => L.T("{0}: o Windows informa \"{1}\". Faça backup e confira o disco.", e.Subject, L.T(e.Note ?? "Com problema")),
            AlertKind.VolumeFull => L.T("{0} está {1} % cheio (limite {2} %).", e.Subject, v, limit),
            AlertKind.BatteryLow or AlertKind.LaptopBatteryLow => L.T("{0} está com {1} % de bateria.", e.Subject, v),
            AlertKind.LaptopCharged => L.T("A bateria chegou a {0} % na tomada (limite {1} %). Já pode tirar da tomada.", v, limit),
            AlertKind.LaptopBatteryHealth => L.T("A bateria guarda {0} % da capacidade de fábrica (limite {1} %). Pense em trocá-la.", v, limit),
            _ => e.Subject,
        };
        return e.End is null ? text : L.T("Resolvido: {0}", text);
    }

    /// <summary>Linha do envio de teste.</summary>
    public static string Test(string machine) => L.T("Teste do Nodekeeper em {0}: os avisos chegam aqui.", machine);
}
