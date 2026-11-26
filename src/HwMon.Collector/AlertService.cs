using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HwMon.Core;
using HwMon.Core.Alerts;

namespace HwMon.Collector;

/// <summary>
/// Alertas da máquina: a cada 5 s avalia as regras sobre o snapshot, grava o histórico e avisa pelos canais
/// configurados (ntfy, Discord, Telegram). Os alertas abertos vão em cada snapshot, para os painéis pareados mostrarem
/// a notificação do Windows. Funciona com o painel fechado.
/// Arquivos em %ProgramData%\Nodekeeper: alertas.json (regras e canais) e alertas-historico.json (últimos 300).
/// </summary>
public sealed class AlertService(EngineHost host, DataStore store, ILogger<AlertService> log) : BackgroundService
{
    private const int HistoryLimit = 300;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(DataStore.Json) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly object _lock = new();
    private AlertSettings _settings = new();
    private List<AlertEvent> _history = new();
    private AlertEvaluator _evaluator = new();

    private string SettingsPath => Path.Combine(store.Directory, "alertas.json");
    private string HistoryPath => Path.Combine(store.Directory, "alertas-historico.json");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        Load();
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(Tick, stop); }
            catch (OperationCanceledException) { break; }
            try { Round(); }
            catch (Exception ex) { log.LogWarning(ex, "Alertas: falha ao avaliar"); }
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            _settings = SafeFile.ReadJson<AlertSettings>(SettingsPath, Json) ?? new AlertSettings();
            _settings.Normalize();
            _history = SafeFile.ReadJson<List<AlertEvent>>(HistoryPath, Json) ?? new();
            _evaluator = new AlertEvaluator(_history.Count > 0 ? _history.Max(e => e.Id) : 0);
            _evaluator.Restore(_history.Where(e => e.End is null));
        }
    }

    private void Round()
    {
        if (host.Engine is not { } engine) return;
        var snapshot = engine.Snapshot();
        var components = engine.Descriptor().Components;
        List<AlertEvent> started, ended;
        lock (_lock)
        {
            (started, ended) = _evaluator.Evaluate(DateTimeOffset.Now, snapshot.Values, components, _settings.Rules);
            foreach (var e in started) _history.Add(e);
            if (started.Count + ended.Count > 0)
            {
                if (_history.Count > HistoryLimit) _history.RemoveRange(0, _history.Count - HistoryLimit);
                SaveHistory();
            }
            // Cópias: o avaliador continua mexendo nos objetos (pior valor) enquanto o snapshot é serializado.
            engine.ActiveAlerts = _evaluator.Active.Select(Clone).ToList();
        }
        var machine = engine.HostName;
        foreach (var e in started)
        {
            log.LogWarning("Alerta: {Title} — {Body}", AlertText.Title(e, machine), AlertText.Body(e));
            _ = SendAsync(Clone(e), machine);
        }
        foreach (var e in ended)
        {
            log.LogInformation("Alerta resolvido: {Title}", AlertText.Title(e, machine));
            if (_settings.Channels.NotifyResolved) _ = SendAsync(Clone(e), machine);
        }
    }

    private static AlertEvent Clone(AlertEvent e) => new()
    {
        Id = e.Id, Kind = e.Kind, Component = e.Component, Subject = e.Subject, Value = e.Value, Threshold = e.Threshold,
        Note = e.Note, Start = e.Start, End = e.End,
    };

    private void SaveHistory()
    {
        try { SafeFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history, Json)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Alertas: não foi possível gravar o histórico"); }
    }

    private void SaveSettings()
    {
        SafeFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, Json));
    }

    // ------------------------------------------------------------------ API

    public AlertsResponse Get()
    {
        lock (_lock)
        {
            var c = _settings.Channels;
            return new AlertsResponse
            {
                Rules = _settings.Rules.Select(r => new AlertRule { Kind = r.Kind, Enabled = r.Enabled, Threshold = r.Threshold, Minutes = r.Minutes }).ToList(),
                Channels = new AlertChannels
                {
                    NtfyUrl = c.NtfyUrl,
                    DiscordWebhook = Mask(c.DiscordWebhook),
                    TelegramToken = Mask(c.TelegramToken),
                    TelegramChatId = c.TelegramChatId,
                    NotifyResolved = c.NotifyResolved,
                },
                Active = _evaluator.Active.Select(Clone).ToList(),
                History = _history.AsEnumerable().Reverse().Take(100).Select(Clone).ToList(),
            };
        }
    }

    /// <summary>O painel mostra só se o segredo existe; quem quer trocar digita de novo.</summary>
    public const string Masked = "•••";
    private static string? Mask(string? secret) => string.IsNullOrEmpty(secret) ? null : Masked;

    public void SetRules(List<AlertRule> rules)
    {
        lock (_lock)
        {
            _settings.Rules = rules;
            _settings.Normalize();
            SaveSettings();
        }
    }

    /// <summary>Campos nulos ficam como estão; vazio apaga; "•••" (o valor mascarado devolvido) mantém o segredo.</summary>
    public async Task<string?> SetChannelsAsync(AlertChannels update)
    {
        if (Invalid(update.NtfyUrl) || Invalid(update.DiscordWebhook)) return "URL inválida (use https://...).";
        lock (_lock)
        {
            var c = _settings.Channels;
            c.NtfyUrl = Merge(c.NtfyUrl, update.NtfyUrl);
            c.DiscordWebhook = Merge(c.DiscordWebhook, update.DiscordWebhook);
            c.TelegramToken = Merge(c.TelegramToken, update.TelegramToken);
            c.TelegramChatId = Merge(c.TelegramChatId, update.TelegramChatId);
            c.NotifyResolved = update.NotifyResolved;
            SaveSettings();
        }
        await FindTelegramChatAsync();
        return null;

        static string? Merge(string? current, string? incoming) => incoming switch
        {
            null or Masked => current,
            _ when string.IsNullOrWhiteSpace(incoming) => null,
            _ => incoming.Trim(),
        };
        static bool Invalid(string? url) => !string.IsNullOrWhiteSpace(url) && url != Masked
            && !(Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http");
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.RemoveAll(e => e.End is not null);
            SaveHistory();
        }
    }

    public async Task<List<AlertTestResult>> TestAsync(string machine)
    {
        await FindTelegramChatAsync();
        AlertChannels c;
        lock (_lock) c = _settings.Channels;
        var title = "Nodekeeper";
        var body = AlertText.Test(machine);
        var results = new List<AlertTestResult>();
        foreach (var (name, send) in Channels(c))
        {
            try
            {
                await send(title, body);
                results.Add(new AlertTestResult { Channel = name, Ok = true });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                results.Add(new AlertTestResult { Channel = name, Ok = false, Error = ex is TaskCanceledException ? "sem resposta" : ex.Message });
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ envio

    private async Task SendAsync(AlertEvent e, string machine)
    {
        AlertChannels c;
        lock (_lock) c = _settings.Channels;
        var title = AlertText.Title(e, machine);
        var body = AlertText.Body(e);
        foreach (var (name, send) in Channels(c))
        {
            try { await send(title, body); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                log.LogWarning("Alertas: envio pelo {Channel} falhou: {Error}", name, ex.Message);
            }
        }
    }

    private static IEnumerable<(string Name, Func<string, string, Task> Send)> Channels(AlertChannels c)
    {
        if (!string.IsNullOrWhiteSpace(c.NtfyUrl)) yield return ("ntfy", (t, b) => Ntfy(c.NtfyUrl!, t, b));
        if (!string.IsNullOrWhiteSpace(c.DiscordWebhook)) yield return ("Discord", (t, b) => Discord(c.DiscordWebhook!, t, b));
        if (!string.IsNullOrWhiteSpace(c.TelegramToken))
            yield return ("Telegram", (t, b) => Telegram(c.TelegramToken!, c.TelegramChatId, t, b));
    }

    private static async Task Ntfy(string url, string title, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        // Cabeçalho HTTP só aceita ASCII: o título vai pelo formato RFC 2047 que o ntfy entende.
        request.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title)) + "?=");
        request.Headers.TryAddWithoutValidation("Tags", "warning");
        (await Http.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task Discord(string webhook, string title, string body)
        => (await Http.PostAsJsonAsync(webhook, new { content = $"**{title}**\n{body}" })).EnsureSuccessStatusCode();

    private static async Task Telegram(string token, string? chatId, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(chatId)) throw new InvalidOperationException(TelegramNoChat);
        (await Http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new { chat_id = chatId, text = $"{title}\n{body}" })).EnsureSuccessStatusCode();
    }

    private const string TelegramNoChat = "abra o bot no Telegram, toque em Iniciar (/start), mande uma mensagem e teste de novo";

    /// <summary>
    /// Com o token e sem o ID do chat, pergunta ao Telegram (getUpdates) quem escreveu ao bot por último: é o chat de
    /// quem tocou em Iniciar. Assim basta colar o token. Só lê as mensagens enviadas ao próprio bot.
    /// </summary>
    private async Task FindTelegramChatAsync()
    {
        string? token;
        lock (_lock)
        {
            var c = _settings.Channels;
            if (string.IsNullOrWhiteSpace(c.TelegramToken) || !string.IsNullOrWhiteSpace(c.TelegramChatId)) return;
            token = c.TelegramToken;
        }
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates"));
            string? chat = null;
            if (doc.RootElement.TryGetProperty("result", out var updates) && updates.ValueKind == JsonValueKind.Array)
                foreach (var u in updates.EnumerateArray())
                    foreach (var kind in new[] { "message", "my_chat_member", "channel_post" })
                        if (u.TryGetProperty(kind, out var m) && m.TryGetProperty("chat", out var ch) && ch.TryGetProperty("id", out var id))
                            chat = id.GetRawText();
            if (chat is null) return;
            lock (_lock)
            {
                if (_settings.Channels.TelegramToken != token) return;
                _settings.Channels.TelegramChatId = chat;
                SaveSettings();
            }
            log.LogInformation("Alertas: chat do Telegram encontrado ({Chat})", chat);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning("Alertas: não foi possível achar o chat do Telegram: {Error}", ex.Message);
        }
    }
}
