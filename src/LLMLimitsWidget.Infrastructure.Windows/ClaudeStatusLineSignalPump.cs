using System.Threading.Channels;
using LLMLimitsWidget.Application;
using LLMLimitsWidget.Domain;

namespace LLMLimitsWidget.Infrastructure.Windows;

/// <summary>
/// Converts statusLine file changes into coalesced domain observations. The
/// watcher owns only OS resources; publication remains an AppStore command.
/// </summary>
public sealed class ClaudeStatusLineSignalPump : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMinutes(1);
    private readonly string _snapshotPath;
    private readonly ClaudeStatusLineFileReader _reader;
    private readonly IApplicationCommandSink _commands;
    private readonly TimeProvider _clock;
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private FileSystemWatcher? _watcher;
    private Task? _loop;
    private long _sequence;

    public ClaudeStatusLineSignalPump(
        string snapshotPath,
        IApplicationCommandSink commands,
        TimeProvider? clock = null)
    {
        _snapshotPath = Path.GetFullPath(snapshotPath);
        _clock = clock ?? TimeProvider.System;
        _reader = new ClaudeStatusLineFileReader(_snapshotPath, _clock);
        _commands = commands;
    }

    public void Start(CancellationToken applicationStopping = default)
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
            EnsureWatcher();

            _loop = RunAsync(_lifetime.Token);
            _signals.Writer.TryWrite(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? loop;
        lock (_sync)
        {
            lifetime = _lifetime;
            loop = _loop;
            _lifetime = null;
            _loop = null;
            _signals.Writer.TryComplete();
            _watcher?.Dispose();
            _watcher = null;
        }

        if (lifetime is null)
        {
            return;
        }

        lifetime.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        lifetime.Dispose();
    }

    private void OnFileSignal(object? sender, FileSystemEventArgs args) =>
        _signals.Writer.TryWrite(true);

    private void OnWatcherError(object? sender, ErrorEventArgs args)
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _signals.Writer.TryWrite(true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = _signals.Reader.TryRead(out _);
                await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                while (_signals.Reader.TryRead(out _))
                {
                }

                EnsureWatcher();
                var result = await _reader.ReadAsync(
                    generation: 0,
                    sequence: Interlocked.Increment(ref _sequence),
                    EffectId.New(),
                    cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    var observation = result.Observation!;
                    await _commands.DispatchAsync(
                        new ObservationReceivedCommand(
                            ProviderId.Claude,
                            observation,
                            observation.ReceivedAtUtc,
                            Guid.NewGuid()),
                        priority: true,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _commands.DispatchAsync(
                        new TransportObservationFailedCommand(
                            ProviderId.Claude,
                            TransportId.ClaudeStatusLine,
                            result.Error!,
                            _clock.GetUtcNow(),
                            Guid.NewGuid()),
                        priority: false,
                        cancellationToken).ConfigureAwait(false);
                    await Task.Delay(RecoveryDelay, cancellationToken).ConfigureAwait(false);
                    _signals.Writer.TryWrite(true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureWatcher()
    {
        lock (_sync)
        {
            if (_watcher is not null)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_snapshotPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_snapshotPath))
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.FileName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileSignal;
            _watcher.Created += OnFileSignal;
            _watcher.Renamed += OnFileSignal;
            _watcher.Error += OnWatcherError;
        }
    }
}
