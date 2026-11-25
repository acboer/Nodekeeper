using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HwMon.App.Infrastructure;
using HwMon.Client;
using HwMon.Core;

namespace HwMon.App.Services;

/// <summary>
/// Aviso de versão nova: uma vez por dia o painel lê a lista de releases do GitHub (o único acesso do painel à internet;
/// desliga em Configurações). Não baixa nem instala nada: mostra "versão X disponível" com o link da página.
/// A requisição não leva nada do usuário além do nome genérico do programa (User-Agent "Nodekeeper").
/// </summary>
public sealed class UpdateChecker : ObservableObject
{
    public const string ReleasesPage = "https://github.com/acboer/Nodekeeper/releases";
    private const string ReleasesApi = "https://api.github.com/repos/acboer/Nodekeeper/releases?per_page=20";
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _timer;
    private bool _checking;
    private string? _error;

    public UpdateChecker(SettingsStore settings)
    {
        _settings = settings;
        CheckNowCommand = new RelayCommand(() => _ = CheckAsync(force: true));
        // Primeira olhada pouco depois de abrir (sem atrasar a abertura); depois confere de hora em hora se já passou um dia.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            await CheckAsync(force: false);
        };
        _timer.Start();
    }

    /// <summary>Versão deste painel (ex.: 0.15.0).</summary>
    public static Version Current { get; } = Parse(Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    public bool Enabled
    {
        get => _settings.Current.Updates.Check;
        set
        {
            if (value == Enabled) return;
            _settings.Current.Updates.Check = value;
            _settings.SaveSoon();
            Raise();
            Raise(nameof(IsAvailable));
            Raise(nameof(StatusText));
            if (value) _ = CheckAsync(force: true);
        }
    }

    /// <summary>A versão mais nova publicada (da última consulta), se for mais nova que esta.</summary>
    public Version? Latest => Parse(_settings.Current.Updates.Latest) is { } v && v > Current ? v : null;

    public bool IsAvailable => Enabled && Latest is not null;

    public string? AvailableText => Latest is { } v ? L.T("Versão {0} disponível", v.ToString(3)) : null;

    public string StatusText
    {
        get
        {
            if (!Enabled) return L.T("Desligado: o painel não consulta o GitHub.");
            if (_checking) return L.T("Verificando...");
            if (_error is not null) return L.T("Não foi possível verificar: {0}", _error);
            if (Latest is { } v) return L.T("Versão {0} disponível.", v.ToString(3));
            return _settings.Current.Updates.LastCheck is { } at
                ? L.T("Você está na versão mais recente (verificado em {0}).", Format.When(at))
                : L.T("Ainda não verificado.");
        }
    }

    public RelayCommand CheckNowCommand { get; }
    public RelayCommand OpenCommand { get; } = new(Open);

    public static void Open()
    {
        try { Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true }); }
        catch (Exception) { /* sem navegador padrão: nada a fazer */ }
    }

    public async Task CheckAsync(bool force)
    {
        var u = _settings.Current.Updates;
        if (!u.Check || _checking) return;
        if (!force && u.LastCheck is { } last && DateTimeOffset.Now - last < Interval) return;
        _checking = true;
        _error = null;
        Raise(nameof(StatusText));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nodekeeper/{Current.ToString(3)}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApi));
            Version? best = null;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (Parse(r.GetProperty("tag_name").GetString()) is { } v && (best is null || v > best)) best = v;
            }
            u.Latest = best?.ToString(3);
            u.LastCheck = DateTimeOffset.Now;
            _settings.SaveSoon();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _error = ex is TaskCanceledException ? L.T("sem resposta") : ex.Message;
        }
        finally
        {
            _checking = false;
            Raise(nameof(Latest));
            Raise(nameof(IsAvailable));
            Raise(nameof(AvailableText));
            Raise(nameof(StatusText));
        }
    }

    /// <summary>"v0.15.0", "0.15.0+abc", "0.15.0-preview" → 0.15.0.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-', ' ')[0];
        if (!Version.TryParse(core, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }
}
