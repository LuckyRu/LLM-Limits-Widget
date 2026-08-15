# Architecture v2: системный дизайн устойчивого LLM Limits Widget

Статус: proposed  
Дата: 2026-08-15  
Область: домен лимитов, фоновые provider pipelines, глобальное состояние, presentation/ViewModel, WPF host, persistence и диагностика

## 1. Назначение документа

Документ задаёт целевую архитектуру приложения перед дальнейшим развитием. Главная цель — сделать получение и отображение лимитов предсказуемым, потокобезопасным, самовосстанавливающимся и тестируемым без запуска WPF, CLI-процессов или реальных часов.

Архитектура должна гарантировать:

- домен физически выделен в отдельную сборку и не зависит от WPF, файлов, процессов, JSON, логгера и системных часов;
- всё бизнес-состояние, типизированные ошибки, переходы состояний и интеракторы находятся в домене;
- Codex и Claude работают в независимых управляемых фоновых потоках;
- сбой одного провайдера не останавливает другой провайдер и UI;
- ни один поздний ответ, повторный event или параллельный refresh не может откатить состояние назад;
- UI получает только неизменяемые снимки глобального состояния;
- ViewModel не знает о WPF-контролах, процессах и persistence;
- каждый use case и каждый переход state machine имеет детерминированный unit-тест.

## 2. Текущее состояние и причины изменения

Рабочий прототип уже содержит полезные решения: provider-neutral snapshots, last-known-good cache, single-flight supervisor, backoff, Codex app-server session, Claude hybrid source и оптимизированный countdown. Их необходимо сохранить семантически, но разнести по строгим границам.

Текущие архитектурные проблемы:

1. Все классы собираются в одном WPF-проекте и одном namespace. Граница домена существует только как папка и не защищена компилятором.
2. `LimitDomain.cs` и `ProviderSupervisor.cs` напрямую используют `WidgetLogger`, `ProviderStateStore`, `DateTimeOffset.UtcNow`, `Random.Shared`, `Task.Delay`, `PeriodicTimer`, `IOException` и `JsonException`.
3. `LimitsCoordinator` и `ProviderSupervisor` оба планируют обновления. Это создаёт два уровня polling и неочевидное владение lifecycle.
4. Ошибки классифицируются по типу инфраструктурного exception и тексту сообщения. Такая классификация нестабильна и не является доменным контрактом.
5. `LimitDataStatus` смешивает качество данных, работоспособность pipeline и необходимость действия пользователя.
6. `MainWindow` создаёт инфраструктуру, вызывает use cases, маршалит события, строит presentation-модель, форматирует данные и напрямую обновляет контролы.
7. Тесты представлены одним console-harness, зависят от реального времени и файловой системы и не защищают архитектурные зависимости.
8. Текущий цикл `ProviderSupervisor` оставляет проигравший `WaitAsync` или `Task.Delay` после `Task.WhenAny`. Старый waiter способен поглотить следующий push-signal, поэтому фоновое обновление может не проснуться.
9. Текущий `_breakerOpen` записывает boolean, но не запрещает attempts и не реализует переходы `Open -> HalfOpen -> Closed`; фактически это диагностический флаг, а не circuit breaker.

## 3. Архитектурные принципы

### 3.1. Dependency rule

Зависимости направлены только внутрь:

```text
                       ┌─> Presentation ─> Domain
WPF host/composition ──┤
                       ├─> Application ──> Domain
                       ├─> Provider.Codex/Claude ─> Domain
                       └─> Infrastructure.Windows ─> Application/Domain
```

`Domain` не имеет project references. `Presentation` зависит только от `Domain`. `Application` не знает о WPF и конкретных провайдерах. Только WPF composition root видит все concrete projects и связывает реализации с портами.

### 3.2. Functional core, imperative shell

- Domain reducer, state machines, validation, error policy и selectors — чистые функции.
- Таймеры, процессы, файлы, channels, WPF Dispatcher и логирование — внешняя оболочка.
- Runtime выполняет эффекты, переводит их результат в доменные события и никогда не изменяет доменное состояние напрямую.

### 3.3. Один владелец каждого изменяемого состояния

- `AppStore` — единственный владелец `AppState`.
- Каждый `ProviderPipeline` — единственный владелец своего runtime-состояния и транспорта.
- ViewModel — единственный владелец presentation-состояния конкретного окна.
- WPF View не хранит бизнес-состояние.

### 3.4. Ошибка не уничтожает корректные данные

Последний валидный `ProviderLimits` хранится отдельно от health/error. Неудачная попытка обновляет состояние pipeline и `LastError`, но не заменяет данные прочерками и не меняет их на нули.

## 4. Целевая структура solution

```text
src/
  LLMLimitsWidget.Domain/
    Models/
    Errors/
    State/
    Events/
    Commands/
    Interactors/
    Policies/
    Ports/

  LLMLimitsWidget.Application/
    AppStore/
    PipelineRuntime/
    Lifecycle/
    CompositionContracts/

  LLMLimitsWidget.Provider.Codex/
    Protocol/
    Parsing/
    Mapping/

  LLMLimitsWidget.Provider.Claude/
    StatusLine/
    DirectUsage/
    Selection/
    Parsing/

  LLMLimitsWidget.Infrastructure.Windows/
    Processes/
    Persistence/
    Time/
    Logging/
    Signals/

  LLMLimitsWidget.Presentation/
    ViewModels/
    Selectors/
    Formatting/
    Scheduling/

  LLMLimitsWidget.Wpf/
    Views/
    Controls/
    Tray/
    Overlay/
    CompositionRoot/

tests/
  LLMLimitsWidget.Domain.Tests/
  LLMLimitsWidget.Application.Tests/
  LLMLimitsWidget.Provider.Codex.Tests/
  LLMLimitsWidget.Provider.Claude.Tests/
  LLMLimitsWidget.Presentation.Tests/
  LLMLimitsWidget.Infrastructure.Windows.Tests/
  LLMLimitsWidget.Architecture.Tests/
```

Провайдерные проекты содержат только pure protocol DTO, parsers и mapping во входные domain candidates. Provider-specific required windows, source selection, merge, retry и freshness policies находятся в `Domain/Policies/Providers`. Провайдерные проекты не должны ссылаться на WPF и не должны запускать процессы. Windows infrastructure предоставляет process/session transports, watcher/named-event и файловые реализации портов. Concrete adapters собираются из pure provider protocol и Windows transport в composition root.

## 5. Доменная модель

### 5.1. Значимые типы

```csharp
public enum ProviderId { Codex, Claude }
public enum LimitPeriod { FiveHours, SevenDays }

public readonly record struct RemainingPercent
{
    public decimal Value { get; }
    public static Result<RemainingPercent, DomainError> Create(decimal value);
}

public sealed record LimitWindow(
    LimitPeriod Period,
    RemainingPercent Remaining,
    DateTimeOffset ResetAtUtc,
    ObservationCursor Cursor,
    DataProvenance Provenance);

public sealed record ProviderLimits(
    ProviderId Provider,
    ObservationId ObservationId,
    DateTimeOffset ObservedAtUtc,
    ImmutableDictionary<LimitPeriod, LimitWindow> Windows);

public sealed record ProviderObservationEnvelope(
    ProviderId Provider,
    TransportId Transport,
    long Generation,
    long Sequence,
    string? SourceRevision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    ObservationCompleteness Completeness,
    ImmutableDictionary<LimitPeriod, LimitWindowCandidate> Windows);
```

`LimitWindow` не содержит UI-label, status или error message. `W`, `5h`, локализация даты и countdown — presentation concern.

Для текущего продукта Codex weekly window и Claude weekly window оба используют `SevenDays`; различие задаёт `ProviderId`, а не дублирующий enum `Weekly/SevenDay`.

`CapturedAtUtc` описывает момент данных у источника; `ReceivedAtUtc` — момент получения виджетом и не может делать старый файл «новым». `SourceRevision` используется, если источник предоставляет монотонную ревизию. Каждое принятое окно хранит собственный cursor/provenance, поэтому частичное Claude-наблюдение можно безопасно merge-ить по окнам.

### 5.2. Глобальное состояние

```csharp
public sealed record AppState(
    long Revision,
    AppLifecycleState Lifecycle,
    ImmutableDictionary<ProviderId, ProviderState> Providers);

public sealed record ProviderState(
    ProviderId Provider,
    ProviderLimits? LastKnownGood,
    DataFreshness Freshness,
    ProviderPipelineState Pipeline,
    ImmutableDictionary<TransportId, TransportState> Transports,
    PersistenceState Persistence,
    ProviderHealth AggregateHealth,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    long AcceptedGeneration,
    long AcceptedSequence);

public sealed record TransportState(
    TransportId Transport,
    TransportHealth Health,
    ProviderError? LastError,
    int ConsecutiveFailures,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc);

public sealed record ProviderPipelineState(
    PipelinePhase Phase,
    long Generation,
    long NextSequence,
    AttemptId? ActiveAttempt,
    RefreshReasonSet PendingReasons,
    DateTimeOffset? NextWakeAtUtc,
    ImmutableArray<DateTimeOffset> RuntimeRestartHistory,
    PipelineLifecycleError? LastLifecycleError);
```

`TransportId` различает как минимум `CodexAppServer`, `ClaudeStatusLine` и `ClaudeDirectCli`. Cache и lifecycle имеют отдельные состояния/ошибки и не перезаписывают transport errors. `AggregateHealth` вычисляется доменной policy из freshness, pipeline, transports и persistence; transport не устанавливает его сам.

`AppState` и вся вложенная object graph глубоко неизменяемы: используются immutable collections либо defensive copy на границе. `Revision` увеличивается ровно на один только при фактическом изменении AppState. Idempotent/no-op command revision не меняет; diagnostic-only transition является изменением state.

### 5.3. Разделение data freshness и pipeline health

```csharp
public enum DataFreshness { Missing, Fresh, Aging, Stale, Expired }
public enum PipelinePhase
{
    Created, Starting, Idle, Waiting, Refreshing,
    BackingOff, RuntimeRestartBackoff, CircuitOpen, HalfOpen, Suspended,
    ActionRequired, Stopping, ForceStopping, Stopped, Faulted
}
```

Примеры допустимых сочетаний:

- `Fresh + Waiting`: нормальная работа;
- `Stale + BackingOff`: данные видимы, источник временно восстанавливается;
- `Stale + ActionRequired`: видимы LKG, требуется повторный login;
- `Missing + Refreshing`: первый запуск, UI показывает loading, а не фиктивные значения;
- `Expired + Faulted`: данные слишком старые для доверия, pipeline не может продолжить работу.

### 5.4. Полная доменная таксономия ошибок

В домен не передаются `Exception`, stack trace, raw stdout или секреты. Инфраструктура обязана преобразовать сбой в один из закрытых типов:

```csharp
public abstract record DomainError(
    ProviderId Provider,
    ErrorCode Code,
    ErrorCategory Category,
    RetryDisposition Retry,
    UserAction UserAction,
    string DiagnosticId,
    DateTimeOffset OccurredAtUtc);

public abstract record ProviderError(TransportId Transport, ...) : DomainError(...);
public sealed record TransientProviderError(...) : ProviderError(...);
public sealed record AuthenticationProviderError(...) : ProviderError(...);
public sealed record CompatibilityProviderError(...) : ProviderError(...);
public sealed record InvalidPayloadProviderError(...) : ProviderError(...);
public sealed record ConfigurationProviderError(...) : ProviderError(...);
public sealed record PersistenceError(...) : DomainError(...);
public sealed record PipelineLifecycleError(...) : DomainError(...);
```

Каждый acquisition boundary имеет собственный закрытый error union в Domain: `CodexAcquisitionError`, `ClaudeStatusLineError`, `ClaudeDirectError`. Они используют общие category/code semantics, но не позволяют вернуть невозможную для данного transport ошибку. `FailurePolicy` обрабатывает union исчерпывающе; добавление нового error subtype ломает compile/test contract до добавления явной policy.

Обязательные `ErrorCode`:

- transport: `ExecutableNotFound`, `ProcessStartFailed`, `ProcessExited`, `BrokenPipe`, `RequestTimeout`, `IoUnavailable`;
- authentication: `LoginRequired`, `SessionExpired`, `PermissionDenied`;
- compatibility: `UnsupportedCliVersion`, `CapabilityMissing`, `ProtocolChanged`;
- payload: `MalformedPayload`, `SchemaMismatch`, `NoSupportedWindows`, `InvalidPercentage`, `InvalidResetTime`, `ProviderMismatch`, `OutOfOrderObservation`;
- configuration: `StatusLineNotConfigured`, `InvalidSnapshotPath`, `SignalUnavailable`;
- persistence: `CacheReadFailed`, `CacheWriteFailed`, `CacheCorrupted`, `UnsupportedCacheSchema`;
- lifecycle: `PipelineNotStarted`, `PipelineAlreadyStopped`, `CommandQueueClosed`, `ShutdownTimeout`, `UnexpectedPipelineTermination`.

`RetryDisposition` принимает `Immediate`, `Backoff`, `WaitForSignal`, `WaitForUserAction`, `WaitForVersionChange`, `Never`. Политика вычисляется доменным `FailurePolicy`; transport не выбирает задержку самостоятельно. Acquisition, persistence и lifecycle errors хранятся в разных slots состояния и не затирают друг друга.

Cancellation при штатном stop/suspend не является ошибкой и не попадает в `LastError`.

## 6. Доменные команды, события и интеракторы

### 6.1. Команды

```csharp
StartApplication
StopApplication
StartProviderPipeline(ProviderId)
StopProviderPipeline(ProviderId)
SuspendProviderPipelines
ResumeProviderPipelines
RequestProviderRefresh(ProviderId, RefreshReason)
RequestAllProvidersRefresh(RefreshReason)
AcceptProviderObservation(ProviderObservationEnvelope)
ReportProviderFailure(ProviderFailureEnvelope)
RestoreProviderCache(ProviderCacheEnvelope)
TickFreshness(DateTimeOffset nowUtc)
```

### 6.2. События

```csharp
ApplicationStarted / ApplicationStopping / ApplicationStopped
ProviderPipelineTransitioned
ProviderRefreshScheduled / ProviderRefreshStarted
ProviderObservationAccepted / ProviderObservationRejected
ProviderRefreshFailed / ProviderRecovered
ProviderFreshnessChanged
ProviderCacheRestored / ProviderCacheRejected
```

### 6.3. Интеракторы

Все use-case решения находятся в чистых доменных интеракторах:

- `StartApplicationInteractor` формирует эффекты восстановления cache и запуска pipelines;
- `RequestRefreshInteractor` нормализует manual/timer/push/resume запросы и решает, нужен ли новый attempt;
- `ApplyProviderObservationInteractor` валидирует provider, generation, sequence, временной порядок и значения;
- `HandleProviderFailureInteractor` применяет error policy, рассчитывает phase, retry и user-action;
- `RestoreLastKnownGoodInteractor` принимает только валидный cache и никогда не делает его `Fresh` без проверки возраста;
- `RefreshFreshnessInteractor` вычисляет freshness из `LastKnownGood.ObservedAtUtc` и политики;
- `StopApplicationInteractor` переводит состояние в stopping и формирует эффекты orderly shutdown.
- `ProviderPipelineReducer` является единственным владельцем `ProviderPipelineState`, refresh policy, pending reasons, breaker transitions и решения о следующем effect.

Интерактор возвращает `Transition<AppState>`:

```csharp
public sealed record Transition<TState>(
    TState State,
    IReadOnlyList<DomainEvent> Events,
    IReadOnlyList<DomainEffect> Effects);
```

`DomainEffect` — декларация внешнего действия (`LoadCache`, `RunProviderAttempt`, `SaveCache`, `ScheduleWake`, `CancelPipeline`), а не выполнение IO.

Нормативная цепочка orchestration ровно одна:

```text
external command / runtime completion
  -> AppStore
  -> pure ProviderPipelineReducer
  -> new ProviderState + DomainEvents + ProviderEffects
  -> per-provider PipelineRuntime executes effects
  -> AttemptCompleted/WakeElapsed/RuntimeFault command
  -> AppStore
```

Runtime не выбирает retry, transport priority, merge policy или phase. Reducer не запускает IO и не хранит `Task`, `CTS`, process handle или timer registration.

## 7. AppStore и публикация состояния

`AppStore` реализует один последовательный reducer с двумя bounded ingress lanes:

- priority lane для stop/suspend/runtime completion и ordinary lane для остальных команд;
- один `SingleReader` всегда дренирует priority lane первой; writers могут быть множественными;
- state-changing results, lifecycle и stop-команды используют `BoundedChannelFullMode.Wait` и не теряются;
- частые refresh hints проходят через per-provider conflated ingress: атомарно объединяют reasons и ставят в Store не более одного marker command;
- reducer исполняется строго последовательно;
- после успешного transition новый `AppState` публикуется подписчикам;
- медленный подписчик получает последний snapshot через conflated subscription, но не блокирует store;
- исключение подписчика изолируется адаптером и не останавливает reducer;
- доменный event содержит `Revision`, `CorrelationId` и redacted metadata.

Store не вызывает WPF Dispatcher. WPF adapter подписывается на store и сам переключается на UI thread.

## 8. Provider pipelines

### 8.1. Владение и контракт

На каждый `ProviderId` создаётся ровно один `IProviderPipelineRuntime` — исполнитель effects, принятых доменным reducer:

```csharp
public interface IProviderPipelineRuntime : IAsyncDisposable
{
    ProviderId Provider { get; }
    PipelineRuntimeSnapshot Runtime { get; }
    Task StartAsync(CancellationToken appStopping);
    ValueTask ExecuteAsync(ProviderEffect effect, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
```

Runtime имеет две независимые bounded lanes: control (`CancelAttempt`, `SuspendRuntime`, `StopRuntime`) и ordinary effects (`RunAttempt`, `ScheduleWake`, `DisposeSession`). Control lane имеет фиксированную малую ёмкость, никогда не стоит за refresh storm и всегда проверяется первой. Ordinary lane использует backpressure; reducer не создаёт дублирующие effects.

Actor consumer **не await-ит transport внутри mailbox loop**. `RunAttempt` создаёт tracked task с отдельным CTS и немедленно возвращает consumer к control lane. Завершение task dispatch-ит `AttemptCompleted` в Store. Поэтому stop/suspend может отменить зависший IO, а второй attempt не запускается, пока runtime хранит active attempt handle.

### 8.2. Локальное runtime-состояние pipeline

```csharp
public sealed record PipelineRuntimeSnapshot(
    ProviderId Provider,
    RuntimeLifecycle Lifecycle,
    AttemptId? ActiveAttempt,
    WakeId? ScheduledWake,
    int TrackedTaskCount,
    PipelineLifecycleError? LastRuntimeError);
```

Runtime snapshot отвечает только за техническое исполнение: actor task, active CTS/task, wake registration и transport resource handles. Бизнес-состояние (`ProviderPipelineState`, generation, sequence, pending reasons, failures, phase) принадлежит AppState. `EffectId/AttemptId/WakeId` связывают effect и completion и обеспечивают идемпотентность.

### 8.3. Коалесcing команд

- `ProviderPipelineReducer` объединяет refresh reasons в один `RefreshReasonSet`.
- Во время attempt reducer хранит максимум один pending refresh set.
- Manual refresh не запускает второй process, но повышает приоритет pending attempt и может обойти healthy cooldown.
- Повторные push events схлопываются.
- Shutdown всегда имеет максимальный приоритет и запрещает новые attempts.

### 8.4. Порядок публикации результата

1. Reducer создаёт `RunProviderAttempt` effect с generation, sequence, reason и deadline.
2. Runtime запускает tracked attempt и остаётся отзывчивым к control effects.
3. Adapter возвращает `AcquisitionOutcome<ProviderObservationEnvelope>`; infrastructure exception наружу не выходит.
4. Runtime dispatch-ит `AttemptCompleted(effectId, outcome)` в AppStore.
5. Reducer проверяет effect id, ordering, payload и merge policy и решает принять или отклонить observation.
6. Только `ProviderObservationAccepted` создаёт `SaveCache` effect.
7. Persistence completion обновляет только `PersistenceState`; failure не откатывает принятый LKG.
8. Duplicate/late completion идемпотентно игнорируется или меняет только новую diagnostics-запись по policy.

Нормативный ordering/merge algorithm:

1. Envelope с чужим provider, неизвестным effect id или старой generation отклоняется целиком.
2. Для attempt-based transport sequence должен быть строго больше последнего завершённого sequence этой generation; duplicate completion является no-op.
3. Для source с `SourceRevision` ревизия должна быть строго новее последней принятой ревизии этого transport. Новое время чтения старого statusLine-файла не меняет его revision/captured time.
4. Каждое окно сравнивается отдельно по `CapturedAtUtc`; более старое окно не принимается.
5. При равном `CapturedAtUtc` выигрывает более новая source revision, затем provider-specific confidence priority. Для Claude tie-breaker: validated direct CLI выше statusLine; он применяется только при равном source time, а не делает старые direct-данные новее.
6. `ObservationCompleteness.Partial` обновляет только присутствующие валидные окна и сохраняет остальные LKG windows с их cursor/provenance.
7. `ObservationCompleteness.Complete` обязан содержать все required windows provider-а; иначе envelope отклоняется как `NoSupportedWindows/SchemaMismatch` и полный LKG не понижается.
8. Невалидное присутствующее окно не merge-ится. Если окна связаны одним declared-complete payload, отклоняется весь payload; для declared-partial отклоняется только candidate window и фиксируется typed payload error.
9. После merge `ObservedAtUtc` provider snapshot равен максимуму `CapturedAtUtc` принятых окон, а не `ReceivedAtUtc`.

### 8.5. Retry, backoff и breaker

Backoff является доменной политикой и принимает error category, количество последовательных ошибок, reason и deterministic jitter input. Реальные случайные числа внедряются через `IJitterSource`.

Рекомендуемая политика:

- transient: full-jitter `5s, 15s, 45s, 2m, 5m, 15m`, cap 30m;
- invalid payload: три редких probe, затем `ActionRequired/WaitForVersionChange`;
- authentication: без быстрого polling, повтор по manual refresh, resume, auth signal или через 30m probe;
- configuration: statusLine-ошибка не блокирует Claude direct channel;
- success: failure count = 0, breaker закрывается, публикуется `ProviderRecovered`;
- manual refresh разрешает один half-open attempt, но не отключает single-flight.

Breaker не отдельный источник истины: его состояние выводится из phase, failure count и `NextWakeAtUtc`.

### 8.6. Lifecycle

Состояния запуска и остановки идемпотентны:

```text
Created -> Starting -> Waiting | Refreshing | RuntimeRestartBackoff | Faulted
Waiting -> Refreshing -> Waiting | BackingOff | CircuitOpen | ActionRequired
BackingOff -> Refreshing
RuntimeRestartBackoff -> Starting
CircuitOpen -> HalfOpen -> Waiting | CircuitOpen | ActionRequired
ActionRequired -> HalfOpen on controlled recovery signal
any running phase -> Suspended -> Waiting | Refreshing on Resume guard
unexpected runtime exit -> Faulted -> Starting on allowed recovery
any non-stopped phase -> Stopping -> Stopped
                                  -> ForceStopping -> Stopped
```

`AppLifecycleState.Running` означает, что Store, Presentation и pipeline runtimes запущены/зарегистрированы; он не зависит от health провайдеров. Приложение остаётся `Running`, когда provider находится в `BackingOff`, `CircuitOpen`, `ActionRequired` или `Faulted`.

Доменный `HandlePipelineRuntimeFaultInteractor` и `RuntimeRestartPolicy` решают, допустим ли restart: не более трёх attempts в rolling window 10 минут с задержками 1s/5s/30s. Application `PipelineRuntimeSupervisor` только отслеживает task/handles и исполняет `DisposeRuntime/RestartRuntime` effects. После исчерпания budget provider остаётся `Faulted`; восстановление допускается manual command, resume или редким 30-минутным probe. Restart одного runtime не пересоздаёт AppStore, ViewModel или второй provider.

Main host:

1. создаёт store и pipeline runtimes;
2. запускает store;
3. восстанавливает LKG;
4. запускает runtimes независимо и переводит приложение в `Running`, когда запуск каждого runtime завершился успехом либо зафиксирован typed startup failure;
5. при sleep отправляет `Suspend` и отменяет текущие долгие IO;
6. при resume отправляет `Resume` с deterministic jitter;
7. при exit сначала запрещает внешние refresh-команды, но оставляет Store открытым для финальных runtime completions;
8. отправляет stop effects, отменяет transports, ожидает runtimes с timeout и принимает их `Stopped/Faulted` transitions;
9. после финальных transitions закрывает Store/effect runner и только затем освобождает tray/WPF.

Ни один pipeline не владеет lifetime приложения и не может вызвать `Application.Shutdown`.

## 9. Провайдерные bounded contexts

### 9.1. Codex

Codex runtime владеет долгоживущей app-server session. Внутренние стадии:

```text
Locate executable -> Start hidden process -> Initialize -> Capability probe
-> Read rate limits -> Parse protocol DTO -> Map candidate -> Domain validation
```

`Provider.Codex` содержит pure JSON-RPC protocol/session state machine, DTO, parser и mapping. `Infrastructure.Windows` содержит hidden `Process`, pipes, Job Object/process-tree termination и concrete session runtime. Broken pipe, timeout, process exit и protocol mismatch останавливают только текущую session; решение о следующем attempt принимает доменный reducer.

### 9.2. Claude

Claude provider использует независимые transport candidates:

```text
statusLine snapshot/event ─┐
                          ├─> Claude source selection -> candidate -> domain validation
direct /usage CLI ────────┘
```

- валидный свежий statusLine event имеет низкую latency;
- direct `/usage` остаётся автономным fallback;
- выбор источника детерминирован: валидность, observation time, source priority и sequence;
- ошибка statusLine не считается ошибкой direct transport;
- cooldown direct transport является частью Claude pipeline policy;
- raw prompt, transcript, credentials и полный stdout не входят в DTO, state, cache или logs.

`Provider.Claude` содержит pure protocol DTO/parsers/mapping. Claude source-selection, required-window и per-window merge policies находятся в Domain. `Infrastructure.Windows` содержит direct hidden process, `FileSystemWatcher`, snapshot file adapter и `EventWaitHandle`. WPF bootstrapper только связывает эти реализации с Claude runtime; ни один Windows handle не попадает в provider assembly.

## 10. Persistence

`IProviderCache` — порт application runtime. Реализация Windows пишет versioned envelope атомарно: temporary file, flush, replace.

```csharp
public sealed record ProviderCacheEnvelope(
    int SchemaVersion,
    ProviderId Provider,
    DateTimeOffset WrittenAtUtc,
    ProviderLimits LastKnownGood,
    string Checksum);
```

Правила:

- cache содержит только валидный LKG;
- pipeline health и transient error не восстанавливаются как актуальное состояние;
- неизвестная schema version или checksum mismatch даёт typed cache error;
- повреждение одного provider cache не мешает другому;
- запись одного provider cache сериализована;
- cache никогда не содержит raw provider payload и секреты.

## 11. Presentation и WPF

### 11.1. Presentation project

`WidgetViewModel` зависит от `IAppStateReader`, `IAppCommandSink`, `ITimeProvider` и presentation scheduler. Он не зависит от WPF assemblies.

```csharp
public sealed class WidgetViewModel
{
    ProviderRowViewModel Codex { get; }
    ProviderRowViewModel Claude { get; }
    WidgetHealthViewModel Health { get; }
    RefreshCommand RefreshAll { get; }
}
```

`ProviderRowViewModel` предоставляет уже готовые presentation properties: percent text/value, period label, countdown text/urgency, reset label, loading/stale/error flags и tooltip model.

ViewModel обновляет свойства только если presentation value реально изменилось. Для countdown она получает абсолютный `ResetAtUtc`, вычисляет ближайший момент изменения строки и планирует один wake-up. Provider fetch из countdown запрещён.

### 11.2. WPF View

- XAML содержит layout, binding, visual states и theme resources;
- code-behind разрешён только для Win32/window mechanics, drag/resize и событий, не представимых командами;
- `MainWindow` не создаёт provider adapters и не форматирует provider snapshots;
- composition root находится в `AppBootstrapper`;
- Dispatcher adapter доставляет ViewModel notifications на UI thread;
- vertical и horizontal views привязаны к одним экземплярам row ViewModel.

## 12. Наблюдаемость

Логи и метрики являются внешним эффектом доменных событий. Обязательные поля:

- provider, transport, generation, sequence, correlationId;
- refresh reason, duration bucket, result/error code;
- previous/next pipeline phase;
- consecutive failures, next retry, LKG age;
- CLI version и protocol capability без raw response.

Запрещено логировать токены, cookies, prompt, transcript path, raw stdout/stderr и полный provider response.

Диагностический snapshot для tray формируется из `AppState`; он не читает private fields pipeline напрямую.

## 13. Сквозные flows

### FL-01. Startup с валидным LKG

Store запускается, caches читаются независимо, валидный LKG принимается и отображается с freshness по возрасту; provider runtimes стартуют после готовности Store.

### FL-02. Startup без LKG или с повреждённым cache

Cache error сохраняется отдельно, приложение переходит в `Running`, provider state остаётся `Missing`, UI показывает нейтральный loading-state без data-like заглушек.

### FL-03. Успешное provider update

Refresh command -> pure reducer -> `RunAttempt` effect -> tracked runtime task -> typed observation -> validation/merge -> atomic LKG update -> cache effect -> ViewModel diff.

### FL-04. Transient failure и recovery

Typed transport error обновляет только соответствующий transport health, LKG сохраняется, reducer планирует backoff; success закрывает failure episode и публикует recovery.

### FL-05. Authentication/compatibility failure

Pipeline переходит в `ActionRequired`, быстрые attempts прекращаются, manual/version/auth signal разрешает контролируемый probe.

### FL-06. Refresh storm и manual refresh во время attempt

Timer, push, resume и manual reasons схлопываются; работает один attempt и хранится максимум один pending reason set; control lane остаётся отзывчивой.

### FL-07. Невалидный payload

Validator создаёт typed payload error; LKG/cursors не меняются, cache effect отсутствует, provider runtime продолжает жить по failure policy.

### FL-08. Ordering и partial merge

Generation/sequence/source revision/captured time проверяются до merge; Claude partial observation обновляет только более новые валидные окна и не понижает полный LKG.

### FL-09. Circuit open/half-open recovery

Failure threshold открывает circuit, ordinary attempts блокируются, probe переводит в `HalfOpen`; success закрывает circuit, failure возвращает `CircuitOpen`.

### FL-10. Потерянный/дублированный signal

Duplicate hints схлопываются; потерянный statusLine/watcher signal компенсируется scheduled reconciliation. Нет оставленных waiter-задач, способных поглотить следующий signal.

### FL-11. Suspend/resume

Suspend отменяет active attempt через control lane; resume увеличивает generation и по due-guard переходит в `Refreshing` либо `Waiting`; completions старой generation отклоняются.

### FL-12. Runtime crash и bounded self-restart

Unexpected actor termination становится lifecycle error; доменная policy перезапускает только этот runtime в пределах budget либо оставляет provider `Faulted` до контролируемого probe. Ошибка нового start переходит в `RuntimeRestartBackoff` либо `Faulted`; provider-attempt `BackingOff` для этого не используется.

### FL-13. Orderly shutdown

Внешний ingress закрывается, Store остаётся открыт для completions, runtime control lanes отменяют IO/child trees; timeout переводит в `ForceStopping` и controlled kill. После terminal completions закрываются Store, logs, tray и WPF.

### FL-14. AppState -> ViewModel

Presentation selector получает immutable revision, вычисляет row models и публикует только изменившиеся свойства; UI dispatcher и bindings не блокируют Store.

### FL-15. Realtime countdown

ViewModel использует абсолютный reset timestamp, планирует ближайшее визуальное изменение и не вызывает provider refresh или layout recalculation.

### FL-16. Presentation settings

Orientation/scale/opacity/position/ghost preference валидируются и сохраняются отдельно; provider state/pipelines не пересоздаются.

### FL-17. Single-instance startup

Первый процесс получает mutex и запускает composition; второй не создаёт Store/runtimes/window и корректно завершается либо посылает show-команду существующему экземпляру после отдельного решения IPC.

### FL-18. Tray show/hide и menu fallback

Tray остаётся recovery channel; show нормализует позицию, hide не завершает pipelines, menu z-order fallback временно скрывает overlay и гарантированно восстанавливает его после закрытия меню.

### FL-19. Ghost enable/disable/recovery

Ghost transition атомарно применяет click-through/no-activate styles и topmost policy; tray выключает режим, partial failure приводит к явно восстанавливаемому состоянию.

### FL-20. Topmost reassertion

Z-order supervisor отслеживает shell/taskbar overlays, bounded образом восстанавливает видимость и не крадёт focus; management menus имеют приоритет над overlay.

### FL-21. Placement, DPI и monitor topology

Restore выбирает существующий monitor, преобразует saved physical coordinates под текущий DPI, разрешает намеренное наложение на taskbar, удерживает recovery strip на экране и пересчитывает позицию при удалении monitor-а.

## 14. Гарантии конкурентности

1. Для каждого провайдера одновременно выполняется не более одного transport attempt.
2. `AppState` изменяется только store consumer-ом.
3. Публикуемые snapshots immutable и консистентны по одной revision.
4. Порядок provider outcomes задаётся `(generation, sequence)`; wall clock не используется как единственный ordering key.
5. Cancellation штатного lifecycle не превращается в failure.
6. Events/signals могут быть потеряны или продублированы без потери корректности: timer/reconciliation обеспечивает eventual refresh, reducer обеспечивает idempotency.
7. Медленный UI не создаёт backpressure в pipelines и получает последнее состояние.

## 15. Тестовая архитектура

### Domain.Tests

Чистые table-driven тесты value objects, validation, error policy, freshness, reducers и каждого interactor. Никаких WPF, файлов и real-time delay.

### Application.Tests

Pipeline actor, command coalescing, single-flight, lifecycle, ordering, retry/breaker, store publication, subscriber isolation. Используются `FakeTimeProvider`, deterministic jitter, scripted transport и in-memory cache.

### Provider.*.Tests

Versioned fixture/contract tests parsинга, protocol mapping и typed error mapping. Fixtures redacted и хранятся в репозитории.

### Presentation.Tests

State-to-ViewModel mapping, loading/stale/action-required states, formatting, countdown boundaries, minimal property notifications и отсутствие fetch при countdown tick.

### Architecture.Tests

Проверяют project references и запрещённые зависимости:

- Domain не ссылается на WPF, WinForms и остальные solution projects;
- Domain не использует `System.IO`, `System.Diagnostics.Process`, JSON serializer и concrete logger;
- Presentation не ссылается на WPF, infrastructure и provider projects;
- WPF Views не создают provider transports;
- provider parsers не запускают процессы и не пишут файлы.

## 16. Нефункциональные требования

- старт с LKG: presentation-state доступен не позднее 300 ms без ожидания CLI;
- UI thread никогда не ждёт process/file/network IO;
- shutdown pipelines: до 3 s в штатном случае, после чего controlled kill child process tree;
- не более одного дочернего процесса на provider;
- память channel bounded; refresh storm не увеличивает очередь бесконечно;
- все времена хранятся в UTC, локальная зона применяется только в Presentation;
- не менее 90% line coverage Domain/Application/Presentation и 100% branch coverage для state transitions и error policies;
- ноль непойманных exceptions за границей transport adapter;
- ноль секретов/raw provider payload в state, cache и logs.

## 17. Миграционная стратегия

Миграция выполняется вертикальными срезами, сохраняя работающий executable:

1. Создать solution и отдельный Domain; перенести immutable models, errors, reducer и tests.
2. Ввести `AppStore` и адаптер, который временно продолжает кормить текущий `MainWindow` snapshots.
3. Перенести общий provider pipeline runtime и заменить двойной polling coordinator/supervisor одним владельцем расписания.
4. Перенести Codex adapter и contract tests.
5. Перенести Claude source selection/adapters и contract tests.
6. Создать Presentation project и `WidgetViewModel`; перевести XAML на bindings.
7. Оставить в WPF только window/tray/Win32 mechanics и composition root.
8. Удалить legacy coordinator, прямое форматирование в `MainWindow` и console test harness после прохождения regression suite.

В каждый момент рабочая ветка должна собираться, запускаться и сохранять текущие пользовательские настройки.

## 18. Решения, которые считаются принятыми после утверждения

- immutable global state + single-writer store;
- один actor-like pipeline на провайдера;
- domain-owned typed errors, policies, reducers и interactors;
- infrastructure exceptions не пересекают adapter boundary;
- data freshness отделена от pipeline health;
- ViewModel — отдельная WPF-независимая сборка;
- polling принадлежит pipelines, отдельный polling coordinator удаляется;
- все flow tests используют управляемое время и deterministic scheduling;
- миграция выполняется по срезам, а не big-bang rewrite.
