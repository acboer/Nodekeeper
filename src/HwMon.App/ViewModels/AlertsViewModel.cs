using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using HwMon.App.Infrastructure;
using HwMon.App.Services;
using HwMon.Client;
using HwMon.Core;
using HwMon.Core.Alerts;
using HwMon.Core.Telemetry;

namespace HwMon.App.ViewModels;

/// <summary>Uma linha da lista de alertas (abertos ou do histórico).</summary>
public sealed class AlertItem
{
    public string Machine { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string When { get; init; } = "";
    public bool Active { get; init; }
}

/// <summary>Uma regra na tela, com o limite e o tempo como texto editável.</summary>
public sealed class AlertRuleItem : ObservableObject
{
    private bool _enabled;
    private string _threshold;
    private string _minutes;

    public AlertRuleItem(AlertRule rule)
    {
        Kind = rule.Kind;
        _enabled = rule.Enabled;
        _threshold = rule.Threshold.ToString("0", L.Culture);
        _minutes = rule.Minutes.ToString(CultureInfo.InvariantCulture);
    }

    public AlertKind Kind { get; }
    public string Name => AlertText.RuleName(Kind);
    public bool HasThreshold => Kind != AlertKind.DiskHealth;
    public string Unit => Kind is AlertKind.CpuTemp or AlertKind.GpuTemp ? "°C" : "%";
    /// <summary>"acima de" (temperatura, volume) ou "abaixo de" (bateria).</summary>
    public string Comparison => L.T(Kind is AlertKind.BatteryLow or AlertKind.LaptopBatteryLow or AlertKind.LaptopBatteryHealth ? "abaixo de" : "acima de");
    public string Description => L.T(Kind switch
    {
        AlertKind.DiskHealth => "Avisa quando o Windows marcar um disco como \"Atenção\" ou \"Com problema\" (SMART).",
        AlertKind.BatteryLow => "Mouse, headset e controles fora do carregador.",
        AlertKind.VolumeFull => "Cada volume com letra (C:, D:...).",
        AlertKind.LaptopBatteryLow => "Notebook fora da tomada.",
        AlertKind.LaptopCharged => "Notebook na tomada: avisa para tirar e poupar a bateria.",
        AlertKind.LaptopBatteryHealth => "Capacidade de hoje comparada à de fábrica.",
        _ => "",
    });
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Threshold { get => _threshold; set => Set(ref _threshold, value); }
    public string Minutes { get => _minutes; set => Set(ref _minutes, value); }

    public AlertRule ToRule() => new()
    {
        Kind = Kind,
        Enabled = Enabled,
        Threshold = double.TryParse(Threshold.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0,
        Minutes = int.TryParse(Minutes, out var m) ? m : 0,
    };
}

/// <summary>
/// Página Alertas: o que está acontecendo agora em todas as máquinas, o histórico, e as regras e canais (ntfy,
/// Discord, Telegram) de cada máquina. As regras ficam no serviço de cada PC; o aviso de "sem resposta" e as
/// notificações do Windows são deste painel.
/// </summary>
public sealed class AlertsViewModel : ObservableObject
{
    private readonly ObservableCollection<MachineViewModel> _machines;
    private readonly SettingsStore _settings;
    private readonly HashSet<MachineViewModel> _seenOnline = new();
    private readonly HashSet<MachineViewModel> _offlineNotified = new();
    private MachineViewModel? _selected;
    private bool _isLoading;
    private string? _status;
    private string? _historyStatus;
    private string? _error;
    private bool _supported = true;
    private string? _ntfyUrl, _discordWebhook, _telegramToken, _telegramChatId;
    private bool _notifyResolved = true;

    public AlertsViewModel(ObservableCollection<MachineViewModel> machines, SettingsStore settings)
    {
        _machines = machines;
        _settings = settings;
        machines.CollectionChanged += (_, _) =>
        {
            Raise(nameof(Machines));
            if (_selected is null || !machines.Contains(_selected)) Selected = machines.FirstOrDefault();
            RefreshActive();
        };
        foreach (var m in machines) Watch(m);
        machines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { } added)
                foreach (MachineViewModel m in added) Watch(m);
        };
        _selected = machines.FirstOrDefault();
        SaveRulesCommand = new RelayCommand(() => _ = SaveRulesAsync());
        SaveChannelsCommand = new RelayCommand(() => _ = SaveChannelsAsync());
        TestCommand = new RelayCommand(() => _ = TestAsync());
        ClearHistoryCommand = new RelayCommand(() => _ = ClearHistoryAsync());
        ReloadCommand = new RelayCommand(() => _ = LoadAsync());
        RefreshActive();
    }

    private void Watch(MachineViewModel m) => m.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(MachineViewModel.ActiveAlerts) or nameof(MachineViewModel.Status)) RefreshActive();
    };

    public IEnumerable<MachineViewModel> Machines => _machines;

    // ------------------------------------------------------------------ deste painel

    public bool Toasts
    {
        get => _settings.Current.Alerts.Toasts;
        set { _settings.Current.Alerts.Toasts = value; _settings.SaveSoon(); Raise(); }
    }

    public bool OfflineAlerts
    {
        get => _settings.Current.Alerts.OfflineMinutes > 0;
        set { _settings.Current.Alerts.OfflineMinutes = value ? 5 : 0; _settings.SaveSoon(); Raise(); }
    }

    /// <summary>Notificação pedida (título, texto); o App mostra pelo ícone da bandeja.</summary>
    public event Action<string, string>? Notify;

    /// <summary>A cada segundo (relógio do painel): máquina que estava conectada e parou de responder há N minutos.</summary>
    public void Tick()
    {
        var minutes = _settings.Current.Alerts.OfflineMinutes;
        foreach (var m in _machines)
        {
            if (m.IsConnected)
            {
                _seenOnline.Add(m);
                if (_offlineNotified.Remove(m))
                {
                    if (Toasts) Notify?.Invoke(L.T("{0}: voltou", m.Name), L.T("A máquina voltou a responder."));
                    RefreshActive();
                }
                continue;
            }
            // Só quem esteve conectado desde que o painel abriu: um PC desligado de propósito não vira alerta.
            if (minutes <= 0 || !_seenOnline.Contains(m) || m.OfflineSince is not { } since) continue;
            if (DateTimeOffset.Now - since < TimeSpan.FromMinutes(minutes) || !_offlineNotified.Add(m)) continue;
            if (Toasts) Notify?.Invoke(L.T("{0}: sem resposta", m.Name), L.T("A máquina não responde há {0} minutos.", minutes));
            RefreshActive();
        }
    }

    // ------------------------------------------------------------------ agora

    public ObservableCollection<AlertItem> Active { get; } = new();
    public bool HasActive => Active.Count > 0;
    public int ActiveCount => Active.Count;

    private void RefreshActive()
    {
        Active.Clear();
        foreach (var m in _machines)
        {
            if (_offlineNotified.Contains(m) && m.OfflineSince is { } since)
                Active.Add(new AlertItem { Machine = m.Name, Title = L.T("Sem resposta"), Body = L.T("Não responde desde {0}.", Format.When(since)), When = Format.When(since), Active = true });
            foreach (var a in m.ActiveAlerts)
                Active.Add(new AlertItem { Machine = m.Name, Title = AlertText.RuleName(a.Kind), Body = AlertText.Body(a), When = Format.When(a.Start), Active = true });
        }
        Raise(nameof(HasActive));
        Raise(nameof(ActiveCount));
    }

    // ------------------------------------------------------------------ regras e canais da máquina escolhida

    public MachineViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _ = LoadAsync();
        }
    }

    public ObservableCollection<AlertRuleItem> Rules { get; } = new();
    public ObservableCollection<AlertItem> History { get; } = new();
    public bool HasHistory => History.Count > 0;

    public string? NtfyUrl { get => _ntfyUrl; set => Set(ref _ntfyUrl, value); }
    public string? DiscordWebhook { get => _discordWebhook; set => Set(ref _discordWebhook, value); }
    public string? TelegramToken { get => _telegramToken; set => Set(ref _telegramToken, value); }
    public string? TelegramChatId { get => _telegramChatId; set => Set(ref _telegramChatId, value); }
    public bool NotifyResolved { get => _notifyResolved; set => Set(ref _notifyResolved, value); }

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    /// <summary>Resultado da última ação ("Regras salvas", "ntfy: ok · Discord: falhou").</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Resultado de "Limpar histórico", mostrado junto do histórico.</summary>
    public string? HistoryStatus { get => _historyStatus; private set => Set(ref _historyStatus, value); }
    public string? Error { get => _error; private set { Set(ref _error, value); Raise(nameof(HasError)); } }
    public bool HasError => _error is not null;
    /// <summary>Serviço da máquina tem alertas (0.15+).</summary>
    public bool Supported { get => _supported; private set => Set(ref _supported, value); }

    public RelayCommand SaveRulesCommand { get; }
    public RelayCommand SaveChannelsCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ReloadCommand { get; }

    /// <summary>Página aberta: recarrega a máquina escolhida.</summary>
    public void Activate()
    {
        if (_selected is null) Selected = _machines.FirstOrDefault();
        else _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_selected is not { } m) return;
        IsLoading = true;
        Status = null;
        HistoryStatus = null;
        Error = null;
        try
        {
            using var client = CollectorClient.For(m.Machine);
            Show(m, await client.AlertsAsync());
            Supported = true;
        }
        catch (CollectorException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            Supported = false;
            Rules.Clear();
            History.Clear();
            Raise(nameof(HasHistory));
            Error = L.T("O serviço desta máquina é anterior à 0.15 e não tem alertas. Instale a versão nova nela.");
        }
        catch (Exception ex) when (ex is CollectorException or HttpRequestException or TaskCanceledException or IOException)
        {
            Error = L.T("Não foi possível ler os alertas de {0}: {1}", m.Name, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Show(MachineViewModel m, AlertsResponse r)
    {
        Rules.Clear();
        // Regras do notebook só em máquina com bateria (as escondidas voltam com o padrão do serviço ao salvar).
        var laptop = m.Descriptor?.Components.Any(c => c.Kind == ComponentKind.Battery) == true;
        foreach (var rule in r.Rules)
            if (laptop || rule.Kind is not (AlertKind.LaptopBatteryLow or AlertKind.LaptopCharged or AlertKind.LaptopBatteryHealth))
                Rules.Add(new AlertRuleItem(rule));
        NtfyUrl = r.Channels.NtfyUrl;
        DiscordWebhook = r.Channels.DiscordWebhook;
        TelegramToken = r.Channels.TelegramToken;
        TelegramChatId = r.Channels.TelegramChatId;
        NotifyResolved = r.Channels.NotifyResolved;
        History.Clear();
        foreach (var e in r.History)
        {
            History.Add(new AlertItem
            {
                Machine = m.Name,
                Title = AlertText.RuleName(e.Kind),
                Body = AlertText.Body(new AlertEvent { Kind = e.Kind, Subject = e.Subject, Value = e.Value, Threshold = e.Threshold, Note = e.Note }),
                When = e.End is { } end ? L.T("de {0} até {1}", Format.When(e.Start), Format.When(end)) : L.T("em andamento desde {0}", Format.When(e.Start)),
                Active = e.End is null,
            });
        }
        Raise(nameof(HasHistory));
    }

    private async Task Run(Func<CollectorClient, Task> action)
    {
        if (_selected is not { } m || IsLoading) return;
        IsLoading = true;
        Status = null;
        HistoryStatus = null;
        Error = null;
        try
        {
            using var client = CollectorClient.For(m.Machine);
            await action(client);
        }
        catch (Exception ex) when (ex is CollectorException or HttpRequestException or TaskCanceledException or IOException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task SaveRulesAsync() => Run(async client =>
    {
        Show(_selected!, await client.SetAlertRulesAsync(Rules.Select(r => r.ToRule()).ToList()));
        Status = L.T("Regras salvas em {0}.", _selected!.Name);
    });

    private Task SaveChannelsAsync() => Run(async client =>
    {
        var channels = new AlertChannels
        {
            NtfyUrl = NtfyUrl ?? "",
            DiscordWebhook = DiscordWebhook ?? "",
            TelegramToken = TelegramToken ?? "",
            TelegramChatId = TelegramChatId ?? "",
            NotifyResolved = NotifyResolved,
        };
        Show(_selected!, await client.SetAlertChannelsAsync(channels));
        Status = L.T("Canais salvos em {0}.", _selected!.Name);
    });

    private Task TestAsync() => Run(async client =>
    {
        var results = await client.TestAlertsAsync();
        Status = results.Count == 0
            ? L.T("Nenhum canal configurado. Preencha e salve um canal antes de testar.")
            : string.Join(" · ", results.Select(r => r.Ok ? $"{r.Channel}: {L.T("enviado")}" : $"{r.Channel}: {L.T("falhou")} ({L.T(r.Error ?? "")})"));
    });

    private Task ClearHistoryAsync() => Run(async client =>
    {
        await client.ClearAlertHistoryAsync();
        Show(_selected!, await client.AlertsAsync());
        var open = History.Count(h => h.Active);
        HistoryStatus = open == 0
            ? L.T("Histórico apagado.")
            : L.T("Histórico apagado. Alertas em andamento ({0}) ficam até terminar.", open);
    });
}
