using System.Collections.Immutable;
using System.Text.Json;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Infrastructure.Windows;

/// <summary>
/// Local, provider-scoped v2 cache. It stores only normalized limit metadata,
/// never credentials or raw provider output, and writes through a temporary
/// file so interrupted writes leave the previous valid snapshot intact.
/// </summary>
public sealed class JsonProviderCache : IProviderCache
{
    private const int SchemaVersion = 1;
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public JsonProviderCache(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMLimitsWidget",
            "cache-v2");
    }

    public async Task<ProviderLimits?> LoadAsync(ProviderId provider, CancellationToken cancellationToken = default)
    {
        var path = GetPath(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var cached = await JsonSerializer.DeserializeAsync<CacheEnvelope>(stream, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return ToDomain(provider, cached);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Provider cache is not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(ProviderId provider, ProviderLimits limits, CancellationToken cancellationToken = default)
    {
        if (limits.Provider != provider)
        {
            throw new InvalidDataException("Provider cache cannot store another provider's limits.");
        }

        Directory.CreateDirectory(_directory);
        var path = GetPath(provider);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, FromDomain(limits), _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(ProviderId provider) =>
        Path.Combine(_directory, $"{provider.ToString().ToLowerInvariant()}-limits.json");

    private static CacheEnvelope FromDomain(ProviderLimits limits) => new(
        SchemaVersion,
        limits.Provider,
        limits.ObservedAtUtc,
        limits.Windows.Values.Select(window => new CacheWindow(
            window.Period,
            window.Remaining.Value,
            window.ResetAtUtc,
            window.Cursor.Generation,
            window.Cursor.Sequence,
            window.Cursor.CapturedAtUtc,
            window.Cursor.SourceRevision,
            window.Provenance.Transport,
            window.Provenance.SourceRevision)).ToArray());

    private static ProviderLimits ToDomain(ProviderId expectedProvider, CacheEnvelope? cached)
    {
        if (cached is null
            || cached.SchemaVersion != SchemaVersion
            || cached.Provider != expectedProvider
            || cached.Windows is null
            || cached.Windows.Length == 0)
        {
            throw new InvalidDataException("Provider cache has an unsupported schema.");
        }

        var windows = ImmutableDictionary.CreateBuilder<LimitPeriod, LimitWindow>();
        foreach (var item in cached.Windows)
        {
            if (!Enum.IsDefined(item.Period)
                || !Enum.IsDefined(item.Transport)
                || windows.ContainsKey(item.Period)
                || item.ResetAtUtc < item.CapturedAtUtc.AddMinutes(-1))
            {
                throw new InvalidDataException("Provider cache contains an invalid limit window.");
            }

            var remaining = RemainingPercent.Create(
                item.RemainingPercent,
                expectedProvider,
                item.Transport,
                item.CapturedAtUtc);
            if (!remaining.IsSuccess)
            {
                throw new InvalidDataException("Provider cache contains an invalid remaining percentage.");
            }

            windows[item.Period] = new LimitWindow(
                item.Period,
                remaining.Value!,
                item.ResetAtUtc,
                new ObservationCursor(
                    item.Generation,
                    item.Sequence,
                    item.CapturedAtUtc,
                    item.SourceRevision),
                new DataProvenance(item.Transport, item.CapturedAtUtc, item.ProvenanceRevision));
        }

        return new ProviderLimits(expectedProvider, ObservationId.New(), cached.ObservedAtUtc, windows.ToImmutable());
    }

    private sealed record CacheEnvelope(
        int SchemaVersion,
        ProviderId Provider,
        DateTimeOffset ObservedAtUtc,
        CacheWindow[] Windows);

    private sealed record CacheWindow(
        LimitPeriod Period,
        decimal RemainingPercent,
        DateTimeOffset ResetAtUtc,
        long Generation,
        long Sequence,
        DateTimeOffset CapturedAtUtc,
        string? SourceRevision,
        TransportId Transport,
        string? ProvenanceRevision);
}
