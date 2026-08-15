using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Presentation;

/// <summary>
/// Presentation-only projection of provider health. The diagnostic window uses
/// this projection and never reaches into transport implementations directly.
/// </summary>
public sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private string _updatedAtText = "—";

    public DiagnosticsViewModel()
    {
        Codex = new ProviderDiagnosticsViewModel(ProviderId.Codex);
        Claude = new ProviderDiagnosticsViewModel(ProviderId.Claude);
    }

    public ProviderDiagnosticsViewModel Codex { get; }
    public ProviderDiagnosticsViewModel Claude { get; }
    public string UpdatedAtText => _updatedAtText;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(AppState state, DateTimeOffset nowUtc)
    {
        Codex.Apply(state.Providers[ProviderId.Codex], nowUtc);
        Claude.Apply(state.Providers[ProviderId.Claude], nowUtc);
        Set(ref _updatedAtText, $"Обновлено: {FormatLocal(nowUtc)}", nameof(UpdatedAtText));
    }

    private static string FormatLocal(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture);

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ProviderDiagnosticsViewModel : INotifyPropertyChanged
{
    private string _summary = "Нет данных";
    private string _schedule = "Ожидание запуска";
    private string _windows = "—";
    private string _transports = "—";
    private string _persistence = "—";

    public ProviderDiagnosticsViewModel(ProviderId provider)
    {
        Provider = provider;
    }

    public ProviderId Provider { get; }
    public string Summary => _summary;
    public string Schedule => _schedule;
    public string Windows => _windows;
    public string Transports => _transports;
    public string Persistence => _persistence;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ProviderState state, DateTimeOffset nowUtc)
    {
        Set(ref _summary,
            $"{Format(state.Freshness)} · {Format(state.AggregateHealth)} · данные: {FormatLocal(state.LastSuccessAtUtc)}",
            nameof(Summary));
        Set(ref _schedule,
            $"Пайплайн: {Format(state.Pipeline.Phase)} · следующее: {FormatLocal(state.Pipeline.NextWakeAtUtc)}",
            nameof(Schedule));
        Set(ref _windows,
            state.LastKnownGood is null
                ? "Лимиты: нет корректного снимка"
                : "Лимиты: " + string.Join(" · ", state.LastKnownGood.Windows.Values
                    .OrderBy(window => window.Period)
                    .Select(window => $"{Format(window.Period)} {window.Remaining.Value:0.##}% до {FormatLocal(window.ResetAtUtc)}")),
            nameof(Windows));
        Set(ref _transports,
            string.Join(Environment.NewLine, state.Transports.Values
                .OrderBy(transport => transport.Transport)
                .Select(transport =>
                    $"{Format(transport.Transport)}: {Format(transport.Health)} · успех: {FormatLocal(transport.LastSuccessAtUtc)}"
                    + (transport.LastError is null ? string.Empty : $" · {transport.LastError.Code}"))),
            nameof(Transports));
        Set(ref _persistence,
            $"Кэш: {Format(state.Persistence.Health)} · запись: {FormatLocal(state.Persistence.LastWriteAtUtc)}"
            + (state.Persistence.LastError is null ? string.Empty : $" · {state.Persistence.LastError.Code}"),
            nameof(Persistence));
    }

    private static string FormatLocal(DateTimeOffset? value) => value is null
        ? "—"
        : value.Value.ToLocalTime().ToString("dd MMM HH:mm:ss", CultureInfo.CurrentCulture);

    private static string Format<T>(T value) where T : struct, Enum => value switch
    {
        DataFreshness.Missing => "нет данных",
        DataFreshness.Fresh => "свежие",
        DataFreshness.Aging => "стареют",
        DataFreshness.Stale => "устарели",
        ProviderHealth.Unknown => "неизвестно",
        ProviderHealth.Healthy => "здоров",
        ProviderHealth.Degraded => "деградирован",
        ProviderHealth.ActionRequired => "нужно действие",
        ProviderHealth.Faulted => "ошибка",
        PipelinePhase.Created => "создан",
        PipelinePhase.Starting => "запускается",
        PipelinePhase.Waiting => "ожидание",
        PipelinePhase.Refreshing => "обновление",
        PipelinePhase.BackingOff => "backoff",
        PipelinePhase.ActionRequired => "нужно действие",
        PipelinePhase.HalfOpen => "контрольная попытка",
        PipelinePhase.RuntimeRestartBackoff => "перезапуск после backoff",
        PipelinePhase.Faulted => "остановлен с ошибкой",
        PipelinePhase.Stopping => "останавливается",
        PipelinePhase.Stopped => "остановлен",
        TransportHealth.Unknown => "неизвестно",
        TransportHealth.Healthy => "здоров",
        TransportHealth.Degraded => "деградирован",
        TransportHealth.ActionRequired => "нужно действие",
        PersistenceHealth.Unknown => "неизвестно",
        PersistenceHealth.Healthy => "здоров",
        PersistenceHealth.Degraded => "деградирован",
        LimitPeriod.FiveHours => "5ч",
        LimitPeriod.SevenDays => "7д",
        TransportId.CodexAppServer => "Codex app-server",
        TransportId.ClaudeStatusLine => "Claude statusLine",
        TransportId.ClaudeDirectCli => "Claude direct CLI",
        TransportId.ProviderCache => "кэш",
        TransportId.PipelineRuntime => "runtime",
        _ => value.ToString()
    };

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
