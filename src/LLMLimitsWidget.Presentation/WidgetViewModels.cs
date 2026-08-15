using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Presentation;

public sealed class WidgetViewModel : INotifyPropertyChanged
{
    public WidgetViewModel()
    {
        Codex = new ProviderRowViewModel(ProviderId.Codex);
        Claude = new ProviderRowViewModel(ProviderId.Claude);
    }

    public ProviderRowViewModel Codex { get; }
    public ProviderRowViewModel Claude { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(AppState state, DateTimeOffset nowUtc)
    {
        Codex.Apply(state.Providers[ProviderId.Codex], nowUtc);
        Claude.Apply(state.Providers[ProviderId.Claude], nowUtc);
    }

    internal void Notify(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));
}

public sealed class ProviderRowViewModel : INotifyPropertyChanged
{
    private readonly LimitWindowViewModel _fiveHours = new(LimitPeriod.FiveHours);
    private readonly LimitWindowViewModel _sevenDays = new(LimitPeriod.SevenDays);
    private bool _hasData;
    private DataFreshness _freshness = DataFreshness.Missing;
    private ProviderHealth _health = ProviderHealth.Unknown;

    public ProviderRowViewModel(ProviderId provider)
    {
        Provider = provider;
    }

    public ProviderId Provider { get; }
    public bool HasData => _hasData;
    public DataFreshness Freshness => _freshness;
    public ProviderHealth Health => _health;
    public LimitWindowViewModel FiveHours => _fiveHours;
    public LimitWindowViewModel SevenDays => _sevenDays;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ProviderState state, DateTimeOffset nowUtc)
    {
        Set(ref _hasData, state.LastKnownGood is not null, nameof(HasData));
        Set(ref _freshness, state.Freshness, nameof(Freshness));
        Set(ref _health, state.AggregateHealth, nameof(Health));

        LimitWindow? fiveHours = null;
        LimitWindow? sevenDays = null;
        if (state.LastKnownGood is { } limits)
        {
            limits.Windows.TryGetValue(LimitPeriod.FiveHours, out fiveHours);
            limits.Windows.TryGetValue(LimitPeriod.SevenDays, out sevenDays);
        }
        _fiveHours.Apply(fiveHours, nowUtc);
        _sevenDays.Apply(sevenDays, nowUtc);
    }

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

public sealed class LimitWindowViewModel : INotifyPropertyChanged
{
    private bool _isVisible;
    private string _percentText = string.Empty;
    private string _countdownText = string.Empty;
    private DateTimeOffset? _resetAtUtc;

    public LimitWindowViewModel(LimitPeriod period)
    {
        Period = period;
    }

    public LimitPeriod Period { get; }
    public bool IsVisible => _isVisible;
    public string PercentText => _percentText;
    public string CountdownText => _countdownText;
    public DateTimeOffset? ResetAtUtc => _resetAtUtc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(LimitWindow? window, DateTimeOffset nowUtc)
    {
        var visible = window is not null;
        Set(ref _isVisible, visible, nameof(IsVisible));
        var percent = window is null
            ? string.Empty
            : $"{window.Remaining.Value.ToString("0.##", CultureInfo.InvariantCulture)}%";
        Set(ref _percentText, percent, nameof(PercentText));
        Set(ref _resetAtUtc, window?.ResetAtUtc, nameof(ResetAtUtc));
        Set(ref _countdownText, CountdownTextFormatter.Format(window?.ResetAtUtc, nowUtc), nameof(CountdownText));
    }

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
