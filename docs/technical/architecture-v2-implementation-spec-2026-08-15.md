# Architecture v2: ТЗ на реализацию

Статус: draft for implementation  
Дата: 2026-08-15  
Основание: [Architecture v2 system design](architecture-v2-system-design-2026-08-15.md)

## 1. Цель работ

Перестроить рабочий прототип LLM Limits Widget в поддерживаемое desktop-приложение со строгими архитектурными границами, независимыми self-healing pipelines Codex/Claude, единым потокобезопасным состоянием и тестируемой ViewModel.

Работа считается завершённой, когда:

- границы защищены project references и architecture tests;
- domain/application/presentation flows выполняются без WPF, CLI и real-time delays;
- Codex и Claude обновляются независимо и управляются lifecycle приложения;
- UI не содержит бизнес-логики и не показывает фиктивные значения;
- каждый сценарий из раздела 5 имеет автоматические тесты из раздела 10;
- прежние возможности overlay, tray, layouts, ghost mode, placement и settings не регрессировали.

## 2. Обязательные ограничения

1. Не выполнять big-bang rewrite. Каждый этап заканчивается зелёной сборкой и работающим widget executable.
2. Не менять каналы авторизации: используются существующие пользовательские сессии Codex/Claude CLI.
3. Не добавлять API keys, чтение browser cookies или хранение credentials.
4. Не логировать raw provider payload, prompt, transcript и секреты.
5. Не использовать `Thread.Sleep`, реальные многоминутные delay и wall-clock sleeps в unit-тестах.
6. Не передавать infrastructure exceptions в Domain, AppState или ViewModel.
7. Не разрешать View/ViewModel запускать process, читать cache или обращаться к provider adapter.
8. Сохранить `run-widget.ps1` как единственный штатный запуск из репозитория.

## 3. Deliverables

### D-01. Solution и проекты

Создать `LLMLimitsWidget.slnx` либо `.sln` и проекты, перечисленные в системном дизайне. Перенести production-код из `spikes/FloatingOverlay` в `src`, сохраняя namespace по слоям.

Критерии:

- одна команда собирает всё решение;
- WPF executable имеет прежнюю иконку и `WinExe` output type;
- Domain и Presentation таргетируют обычный `net10.0`, если Windows API им не нужен;
- только WPF/Windows infrastructure таргетируют `net10.0-windows`;
- запрещённые project references отсутствуют.

### D-02. Domain

Реализовать:

- value objects `RemainingPercent`, `ObservationId`, `ProviderId`, `LimitPeriod`;
- immutable `ProviderLimits`, `ProviderState`, `AppState`;
- `TransportId`, immutable per-transport `TransportState`, отдельные `PersistenceState`/`PipelineLifecycleState` и вычисляемый `ProviderHealth`;
- `DataFreshness`, `PipelinePhase`, refresh reasons;
- закрытую и исчерпывающую иерархию typed provider errors;
- provider/transport-specific closed unions `CodexAcquisitionError`, `ClaudeStatusLineError`, `ClaudeDirectError` с exhaustive policy mapping;
- команды, события, effects и `Transition<TState>`;
- validation/freshness/failure/retry policies;
- interactors запуска, refresh, observation acceptance, failure handling, cache restore, freshness tick и shutdown;
- `HandlePipelineRuntimeFaultInteractor` и `RuntimeRestartPolicy`, принимающие решение о restart budget/delay;
- pure reducer как единственную точку изменения AppState.

Критерии:

- Domain не имеет project references и NuGet dependencies, кроме заранее отдельно одобренной библиотеки immutable collections;
- вся object graph AppState глубоко immutable; mutable backing collections запрещены;
- все public методы детерминированы входами;
- `DateTimeOffset.Now/UtcNow`, `Random.Shared`, file/process/logging APIs отсутствуют;
- значения вне 0..100 не clamp-ятся молча: входной candidate отклоняется typed validation error;
- неизвестные проценты представлены отсутствием окна/данных, а не `0`;
- LKG не содержит operational status/error text.
- acquisition, persistence и lifecycle errors хранятся раздельно и не перезаписывают друг друга.

### D-03. AppStore

Реализовать bounded single-reader command channel и immutable snapshot publication.

Контракт:

```csharp
public interface IAppStateReader
{
    AppState Current { get; }
    IDisposable Subscribe(IAppStateObserver observer);
}

public interface IAppCommandSink
{
    ValueTask DispatchAsync(DomainCommand command, CancellationToken cancellationToken = default);
}
```

Критерии:

- все revisions монотонны;
- Store использует отдельную bounded priority lane для stop/suspend/runtime completions; один reducer consumer всегда проверяет её раньше ordinary lane;
- ни один subscriber callback не выполняется под lock store;
- exception одного subscriber не влияет на остальных;
- slow subscriber получает latest snapshot без блокирования pipelines;
- после stop новые команды отклоняются typed lifecycle result;
- обработка одной команды атомарна для читателей.
- `ProviderPipelineReducer` внутри Store является единственным владельцем provider phase, pending reasons, retry/breaker policy, ordering/merge и effect decisions;
- runtime completion всегда возвращается как команда Store и не мутирует state напрямую;
- revision увеличивается только при фактическом state change, no-op/idempotent command не создаёт revision.

### D-04. Generic ProviderPipeline runtime

Реализовать общий actor-like runtime как исполнитель effects доменного `ProviderPipelineReducer`. Нормативная цепочка: `command -> Store/reducer -> effect -> runtime -> completion -> Store/reducer`.

Поддержать effects/control:

- ordinary effects: `RunAttempt`, `ScheduleWake`, `DisposeTransportSession`;
- priority control: `CancelAttempt`, `SuspendRuntime`, `ResumeRuntime`, `StopRuntime`;
- completions в Store: `WakeElapsed`, `AttemptCompleted`, `AttemptTimedOut`, `RuntimeFaulted`, `RuntimeStopped`.

Критерии:

- не более одного attempt одновременно;
- ordinary/control lanes bounded и раздельны; stop/suspend никогда не ждут за refresh storm;
- actor consumer не await-ит transport: active attempt хранится как tracked task + отдельный CTS, completion возвращается командой в Store;
- refresh storm коалесцируется reducer-ом до создания effects;
- manual refresh во время attempt создаёт в domain state максимум один pending reason set;
- provider outcome получает generation/sequence/correlation id;
- stop идемпотентен и дожидается consumer;
- старые generation/sequence не меняют глобальное состояние;
- unexpected loop termination становится typed lifecycle error и видимым state;
- pipeline не публикует state напрямую в UI.

### D-05. Scheduling и policies

Внедрить `TimeProvider`, `IDelayScheduler` и `IJitterSource`. Реализовать healthy intervals, retry/backoff, action-required и resume jitter как доменные решения.

Критерии:

- тест может мгновенно перевести время вперёд;
- одинаковый jitter seed даёт одинаковый план;
- manual refresh обходит healthy cooldown, но не single-flight;
- auth/compatibility errors не создают частый process-launch loop;
- successful recovery очищает failure counter и LastError;
- freshness может измениться без provider fetch.

### D-06. Persistence

Реализовать `IProviderCache` и Windows JSON adapter с versioned envelope, checksum и atomic replace.

Критерии:

- cache читается до первого provider attempt;
- cache одного provider хранится независимо;
- invalid/corrupt/version-mismatch cache отклоняется typed error;
- cache write запускается только после accepted observation;
- write failure не удаляет LKG из AppState;
- временный файл удаляется/перезаписывается безопасно после сбоя;
- state-файлы не содержат raw payload и secrets.

### D-07. Codex provider

Распределить код по project boundaries:

- `Provider.Codex`: pure protocol DTO/parser/mapper, capability rules и session state machine без Windows API;
- `Infrastructure.Windows`: hidden `Process`, stdio pipes, Job Object/process-tree termination и concrete session runtime;
- `Application`: generic pipeline runtime и typed completion contracts;
- `Wpf/AppBootstrapper`: composition concrete Codex transport.

Критерии:

- initialize/capability/read выполняются в одной управляемой session;
- request id и stdout read сериализованы;
- timeout/process exit/broken pipe закрывают session и child process tree;
- session rotates по policy;
- unknown JSON shape возвращает `ProtocolChanged`/`SchemaMismatch`;
- parser contract покрыт versioned redacted fixtures;
- cancellation при shutdown не публикует provider failure;
- console window не появляется.

### D-08. Claude provider

Распределить код по project boundaries:

- `Provider.Claude`: statusLine/direct protocol DTO, parsers и mapping во входные candidates;
- `Domain/Policies/Providers/Claude`: required windows, source-selection, completeness и per-window merge policy;
- `Infrastructure.Windows`: snapshot file adapter, named event/FileSystemWatcher, hidden direct `/usage` process;
- `Application`: generic runtime и Claude transport completions без Windows handles;
- `Wpf/AppBootstrapper`: composition Claude transports/runtime.

Критерии:

- свежий statusLine принимается без direct call;
- stale/missing/invalid statusLine включает bounded direct fallback;
- direct cooldown тестируем и не привязан к wall clock;
- push event будит pipeline, но не обходит single-flight;
- duplicate FileSystemWatcher events схлопываются;
- поздний statusLine не затирает более новый accepted direct observation;
- statusLine/direct имеют независимые `TransportState`; сбой одного не меняет health/error второго;
- envelope содержит generation, sequence, source revision, captured/received times, completeness и provenance;
- `Partial` payload merge-ится по окнам только если candidate window новее; отсутствующее окно сохраняется из LKG;
- `Complete` payload без required windows отклоняется целиком; старый/невалидный candidate не понижает полный LKG;
- при равном captured time validated direct выше statusLine, но source priority никогда не делает старые данные новее;
- console window не появляется.

### D-09. Presentation/ViewModel

Создать WPF-независимый `WidgetViewModel` и переиспользуемые `ProviderRowViewModel`, `LimitMetricViewModel`, `CountdownViewModel`.

Критерии:

- ViewModel получает AppState snapshots через reader;
- ViewModel отправляет команды только через command sink;
- `Missing` отображается как loading/empty, без `0%`, фиктивных reset и data-like placeholders;
- LKG и health отображаются независимо;
- vertical/horizontal layout используют одни и те же row VMs;
- percent formatting сохраняет до двух значащих десятичных знаков без лишних нулей;
- countdown строится из `ResetAtUtc`, обновляется только при изменении строки/urgency;
- countdown tick не dispatch-ит refresh;
- `PropertyChanged` отправляется только для реально изменившихся properties;
- локальная time zone используется только здесь.

### D-10. WPF host и composition root

Создать `AppBootstrapper`, который собирает adapters, pipelines, store и ViewModel. Перевести XAML на bindings.

Критерии:

- `MainWindow` не содержит `new Codex...`, `new Claude...`, reducer/mapper/formatter logic;
- code-behind ограничен window mechanics и делегированием команд VM;
- snapshot marshaling на Dispatcher выполняет presentation adapter;
- provider/persistence IO не выполняется на Dispatcher thread;
- tray refresh вызывает command VM/application, а не transport;
- orderly shutdown вызывается один раз из app lifecycle;
- `AppLifecycleState.Running` не зависит от provider health: `BackingOff`, `ActionRequired` и `Faulted` не удерживают приложение в `Starting`;
- Store остаётся открыт до приёма финальных runtime transitions и закрывается до освобождения tray/WPF только после них;
- runtime supervisor исполняет доменные `DisposeRuntime/RestartRuntime` effects; restart budget/delay решает Domain, второй provider, Store и ViewModel не пересоздаются;
- overlay, topmost, taskbar overlap, ghost mode, placement, scaling, opacity и single-instance поведение сохранены.

### D-11. Observability

Подписать logger/diagnostics adapter на domain events и pipeline instrumentation.

Критерии:

- каждый attempt имеет correlation id;
- логируются phase transitions и typed error code;
- raw payload/secrets не логируются;
- rotation существующих JSONL-логов сохраняется;
- diagnostic summary строится из AppState;
- logging failure не меняет domain flow.

### D-12. Тестовая инфраструктура

Перейти с единого console test harness на discoverable test projects. Рекомендуемый runner — xUnit; допустим NUnit/MSTest при едином выборе для solution.

Обязательные test doubles:

- `ManualTimeProvider`/`FakeTimeProvider`;
- `DeterministicJitterSource`;
- `ScriptedProviderTransport`;
- `InMemoryProviderCache`;
- `RecordingEffectRunner`;
- `TestAppStateObserver`;
- `SynchronousPresentationScheduler`;
- fixture loader с redaction assertion.

Критерии:

- `dotnet test` запускает все unit/contract/architecture tests;
- real provider smoke вынесен в отдельную explicit category и не запускается по умолчанию;
- unit suite не требует установленного Codex/Claude и пользовательской авторизации;
- concurrency tests не flaky и не используют произвольные sleeps.

## 4. Use cases

### UC-01. Запуск приложения

Актор: Windows host.  
Предусловие: single-instance lock получен.  
Основной поток: запустить store -> прочитать настройки и provider caches -> создать VM -> показать widget -> стартовать provider pipelines.  
Результат: UI доступен независимо от скорости CLI.

### UC-02. Автоматическое обновление Codex

Актор: Codex pipeline timer.  
Результат успеха: валидный snapshot становится Codex LKG и записывается в cache.  
Результат ошибки: Codex health меняется, Claude и UI thread продолжают работу.

### UC-03. Автоматическое обновление Claude через statusLine

Актор: statusLine bridge signal.  
Результат: свежий валидный payload принимается без прямого CLI process.

### UC-04. Фоновое обновление Claude без statusLine

Актор: Claude pipeline scheduler.  
Результат: после cooldown выполняется hidden direct `/usage`; пользователь не должен запускать Claude Desktop/Code вручную.

### UC-05. Ручное обновление всех провайдеров

Актор: пользователь через context/tray menu.  
Результат: оба pipeline получают manual reason независимо; уже идущие attempts не дублируются.

### UC-06. Просмотр LKG при временном сбое

Актор: пользователь.  
Результат: сохранённые проценты/reset остаются видны со stale/diagnostic presentation-state.

### UC-07. Требуется авторизация

Актор: provider adapter/domain policy.  
Результат: быстрый polling прекращён, сохранён LKG, UI показывает actionable health без спама и окон консоли.

### UC-08. Сон и пробуждение Windows

Актор: OS power event.  
Результат: старые IO отменены/изолированы, после resume оба pipeline выполняют по одному jittered refresh.

### UC-09. Реальное обновление countdown

Актор: presentation scheduler.  
Результат: текст меняется на границе отображаемой единицы, provider fetch не выполняется.

### UC-10. Завершение приложения

Актор: пользователь через tray.  
Результат: новые refresh запрещены, child processes завершены, cache/logging flushed, tray/window освобождены.

### UC-11. Восстановление после повреждённого cache

Актор: startup interactor.  
Результат: cache отклонён typed error, приложение стартует с Missing и обновляет provider обычным pipeline.

### UC-12. Изменение UI-настроек

Актор: пользователь.  
Результат: orientation/scale/opacity/position/ghost mode сохраняются presentation settings store; provider pipelines и domain limits state не пересоздаются.

### UC-13. Самовосстановление pipeline runtime

Актор: application runtime supervisor.  
Результат: unexpected actor crash изолируется, generation увеличивается, runtime перезапускается в bounded budget; после исчерпания budget только этот provider остаётся Faulted.

### UC-14. Запуск единственного экземпляра

Актор: пользователь/Windows.  
Результат: первый процесс создаёт приложение, второй не создаёт Store, runtimes, tray и окно и завершается контролируемо.

### UC-15. Управление через tray

Актор: пользователь.  
Результат: show/hide/exit/menu fallback управляют окном и recovery channel, не перезапуская domain/provider state.

### UC-16. Ghost и topmost recovery

Актор: пользователь и z-order supervisor.  
Результат: ghost mode полностью пропускает input, topmost восстанавливается без захвата focus, а partial failure доступен для восстановления через tray.

### UC-17. Восстановление placement при DPI/monitor changes

Актор: Windows topology/DPI events.  
Результат: виджет остаётся достижимым, допускает taskbar overlap и корректно переносится при исчезновении monitor-а.

### UC-18. Изменение/повреждение provider payload

Актор: provider transport/CLI update.  
Результат: schema/semantic violation превращается в typed payload error, LKG и cache не портятся, pipeline применяет compatibility/retry policy.

## 5. Нормативные flows

В этом разделе `S` — AppState, `P` — runtime provider pipeline, `VM` — ViewModel.

### FL-01 Startup с валидным LKG

1. Host запускает Store и переводит app lifecycle в `Starting`.
2. Cache effects выполняются независимо, результаты последовательно возвращаются в Store.
3. Валидный LKG принимается с freshness, вычисленной по age; cache health сохраняется отдельно.
4. Host запускает оба runtimes, Store принимает их startup results.
5. App lifecycle становится `Running`, даже если отдельный provider уже `BackingOff/ActionRequired/Faulted`.
6. VM получает LKG до завершения CLI attempts.

### FL-02 Startup без LKG/с повреждённым cache

1. Missing cache даёт `DataFreshness.Missing`; corrupt/version mismatch создаёт typed persistence error.
2. Ошибка одного cache не влияет на второй provider и не блокирует app startup.
3. UI показывает loading/empty без `0%`, reset и data-like placeholders.
4. Runtimes выполняют обычный initial refresh; app lifecycle переходит в `Running`.

### FL-03 Provider success

1. External refresh command попадает в Store.
2. Pure reducer присваивает generation/sequence/effect id и возвращает `RunAttempt`.
3. Runtime запускает tracked task и остаётся отзывчивым к control lane.
4. Completion возвращается в Store как typed observation envelope.
5. Validation/ordering/merge принимают окна атомарно по policy.
6. Reducer создаёт SaveCache effect; VM получает новую revision.

### FL-04 Transient failure + recovery

1. Adapter преобразует exception в transport-specific typed error.
2. Reducer сохраняет LKG, обновляет только этот TransportState и планирует backoff.
3. Wake completion возвращается в Store и создаёт новый attempt effect.
4. Success очищает failure episode данного transport, закрывает breaker и создаёт recovery event.

### FL-05 Auth/compatibility failure

1. Typed error выбирает `WaitForUserAction`/`WaitForVersionChange`.
2. Provider phase становится `ActionRequired`; LKG и другие transport states сохраняются.
3. Быстрый wake не планируется.
4. Manual/auth/version signal допускает один controlled probe.

### FL-06 Refresh storm/manual during attempt

1. Timer/statusLine/resume/manual ingress атомарно объединяет reason set и ставит не более одного Store marker.
2. При active attempt reducer сохраняет максимум один pending reason set и не создаёт второй effect.
3. Runtime control lane принимает stop/suspend независимо от ordinary queue.
4. После completion reducer повторно оценивает pending reasons и создаёт максимум один следующий attempt.

### FL-07 Invalid payload

1. Envelope проходит syntactic и semantic validation.
2. Invalid provider/percent/reset/schema/completeness создаёт typed payload error.
3. LKG, accepted cursors и cache не меняются.
4. Error записывается в TransportState, retry определяется policy.

### FL-08 Ordering и Claude partial merge

1. Проверяются effect id, generation, sequence и source revision.
2. Для каждого окна candidate `CapturedAtUtc` сравнивается с cursor LKG; `ReceivedAtUtc` не участвует как freshness источника.
3. Partial observation обновляет только присутствующие более новые валидные окна.
4. Complete observation без required windows отклоняется целиком.
5. При равном captured time direct имеет приоритет над statusLine; старый direct не выигрывает у нового statusLine.

### FL-09 CircuitOpen/HalfOpen

1. Failure threshold переводит provider/transport policy state в `CircuitOpen`.
2. Ordinary refresh не создаёт attempt effect.
3. Probe/manual signal создаёт ровно один `HalfOpen` attempt.
4. Success -> `Waiting`, failure count reset; failure -> `CircuitOpen` с controlled next probe.

### FL-10 Signal loss reconciliation

1. Duplicate watcher/event hints схлопываются.
2. Каждый wake waiter отменяется/dispose-ится при выборе другой ветки и не может поглотить будущий signal.
3. Потерянный push компенсируется scheduled reconciliation.
4. Re-read старого statusLine-файла отклоняется source revision/captured cursor.

### FL-11 Suspend/resume

1. Suspend command через reducer создаёт priority cancel/suspend effects.
2. Runtime отменяет active CTS, освобождает wake registration и подтверждает suspended.
3. Resume увеличивает generation; если refresh due — после jitter переходит `Refreshing`, иначе `Waiting` с одним wake.
4. Completion старой generation не меняет LKG.

### FL-12 Runtime crash + bounded restart

1. Unexpected actor exit возвращает `RuntimeFaulted` и lifecycle error в Store.
2. Domain restart policy проверяет rolling budget, увеличивает generation и создаёт dispose/restart effects.
3. Application supervisor освобождает handles и исполняет не более 3 разрешённых restarts за 10 минут с 1s/5s/30s delays.
4. После budget provider остаётся Faulted; manual/resume/30m probe может начать новый recovery episode.
5. Второй provider, Store и VM не пересоздаются.
6. Ошибка запуска нового runtime проходит ту же policy: retryable -> RuntimeRestartBackoff -> Starting после wake; non-retryable/exhausted -> Faulted.

### FL-13 Shutdown

1. Host закрывает внешний refresh ingress, но Store оставляет открытым.
2. Reducer создаёт priority stop/cancel effects для обоих runtimes.
3. Runtimes отменяют tasks, timers, sessions и child process trees и возвращают final completions.
4. При shutdown timeout reducer переводит state в `ForceStopping`, runtime выполняет controlled kill и подтверждает terminal completion.
5. Store принимает `Stopped/Faulted`; затем закрываются effect runner и Store.
6. Logging flush, tray и WPF освобождаются последними.

### FL-14 AppState -> VM

1. Presentation subscriber получает deeply immutable S revision.
2. Selector строит provider presentation models и aggregate health.
3. VM сравнивает values и публикует только изменившиеся properties через UI scheduler.
4. Slow/failed UI observer не блокирует Store и pipelines.

### FL-15 Countdown

1. VM получает `ResetAtUtc`, вычисляет text/urgency и следующую визуальную границу.
2. На wake выполняется только presentation calculation.
3. Unchanged value не создаёт `PropertyChanged`.
4. Countdown никогда не dispatch-ит provider refresh.

### FL-16 Presentation settings

1. View/VM изменяет orientation/scale/opacity/position/ghost preference через settings port.
2. Policy валидирует значение, adapter атомарно сохраняет versioned settings.
3. Provider AppState/runtimes не меняются и не перезапускаются.

### FL-17 Single instance

1. Startup пытается получить named mutex до создания Store/tray/window.
2. Первый процесс продолжает composition; второй пишет diagnostic и завершается без GUI/CLI children.
3. Abandoned mutex безопасно восстанавливается.

### FL-18 Tray show/hide/menu fallback

1. Show восстанавливает visibility и нормализует placement; hide оставляет Store/runtimes активными.
2. Tray menu временно demote-ит overlay; при неудаче fallback скрывает окно.
3. После menu close overlay восстанавливается и topmost policy применяется заново.
4. Exit запускает FL-13 ровно один раз.

### FL-19 Ghost enable/disable/recovery

1. Enable применяет click-through/no-activate styles и topmost без фокуса.
2. Disable снимает styles и восстанавливает допустимый foreground owner.
3. Partial transition фиксирует cleanup-required state; tray остаётся recovery channel.

### FL-20 Topmost reassertion

1. Supervisor получает shell/z-order signal или health tick.
2. Bounded reassertion восстанавливает overlay выше taskbar/system tray без focus steal.
3. Tray/context/system menus временно имеют приоритет; после закрытия policy возвращается.

### FL-21 Placement/DPI/monitor topology

1. Persisted physical rect сопоставляется существующему monitor и текущему DPI.
2. Taskbar overlap разрешён, но recovery strip всегда остаётся достижимым.
3. При удалении monitor-а rect переносится на nearest/primary monitor и snap policy применяется заново.
4. Scale/layout resize сохраняют anchor и aspect ratio.

## 6. Интерфейсы внешних портов

Минимальный набор:

```csharp
public interface IProviderTransport
{
    ProviderId Provider { get; }
    Task<AcquisitionOutcome<ProviderObservation>> AcquireAsync(
        ProviderAttemptContext context,
        CancellationToken cancellationToken);
}

public interface IProviderCache
{
    Task<CacheReadOutcome> ReadAsync(ProviderId provider, CancellationToken cancellationToken);
    Task<CacheWriteOutcome> WriteAsync(ProviderCacheEnvelope cache, CancellationToken cancellationToken);
}

public interface IDomainEventSink
{
    ValueTask PublishAsync(DomainEvent domainEvent, CancellationToken cancellationToken);
}

public interface IDelayScheduler
{
    Task DelayUntilAsync(DateTimeOffset atUtc, CancellationToken cancellationToken);
}

public interface IJitterSource
{
    TimeSpan Next(TimeSpan upperBound, JitterKey key);
}
```

Возвращаемые outcome-типы не бросают operational exceptions. Programmer bugs (`ArgumentNullException`, violated invariant) допускается fail-fast обнаруживать в development/tests, но application boundary обязана записать critical diagnostic и controlled shutdown/restart.

## 7. State transition tables

### 7.1. Pipeline

| Current | Input | Guard | Next | Effect | Test IDs |
|---|---|---|---|---|---|
| Created | Start | — | Starting | initialize runtime | A-023 |
| Starting | RuntimeReady | refresh due | Refreshing | run attempt | A-024 |
| Starting | RuntimeReady | refresh not due | Waiting | schedule wake | A-025 |
| Starting | RuntimeStartFailed | retry allowed | RuntimeRestartBackoff | publish lifecycle failure, schedule restart wake | A-038 |
| Starting | RuntimeStartFailed | non-retryable/budget exhausted | Faulted | wait for controlled recovery | A-039 |
| Waiting | Refresh | allowed | Refreshing | run attempt | A-026 |
| Waiting | Refresh | cooldown | Waiting | merge reason/schedule | A-027 |
| Refreshing | Refresh | — | Refreshing | set one pending reason set | A-006 |
| Refreshing | Success | pending relevant | Refreshing | accept success, run next attempt | A-028 |
| Refreshing | Success | no pending | Waiting | accept success, schedule healthy wake | A-029 |
| Refreshing | Transient failure | below threshold | BackingOff | publish failure, schedule retry | A-009 |
| BackingOff | WakeElapsed | retry allowed | Refreshing | run one attempt | A-035 |
| RuntimeRestartBackoff | WakeElapsed | restart budget available | Starting | execute RestartRuntime effect | A-044 |
| BackingOff | Failure threshold reached | — | CircuitOpen | cancel ordinary wakes, schedule probe | A-020 |
| CircuitOpen | Probe/manual allowed | no probe active | HalfOpen | run exactly one probe attempt | A-021 |
| HalfOpen | Success | — | Waiting | close breaker, reset failure count | A-022 |
| HalfOpen | Failure | — | CircuitOpen | schedule next controlled probe | A-022 |
| Refreshing/HalfOpen | Auth/compat failure | — | ActionRequired | publish failure, cancel fast wakes | A-010 |
| ActionRequired | Manual/Auth/VersionSignal | controlled probe allowed | HalfOpen | run exactly one probe | A-036 |
| Any running | Suspend | — | Suspended | priority cancel/suspend effect | A-011 |
| Suspended | Resume | refresh due | Refreshing | generation++, run jittered refresh | A-042 |
| Suspended | Resume | refresh not due | Waiting | generation++, schedule wake | A-043 |
| Any non-stopped | Stop | — | Stopping | priority cancel/stop effect | A-013, A-014 |
| Stopping | RuntimeStopped | — | Stopped | final state completion | A-013 |
| Stopping | ShutdownTimeout | runtime still alive | ForceStopping | controlled kill process tree/runtime | A-040 |
| ForceStopping | RuntimeStopped/ForcedTerminationCompleted | — | Stopped | retain lifecycle diagnostic | A-041 |
| Any running | RuntimeFaulted | restart budget available | Starting | generation++, controlled restart | A-016, A-030 |
| Any running | RuntimeFaulted | budget exhausted | Faulted | wait for controlled probe | A-031 |
| Faulted | Manual/Resume/Probe | recovery allowed | Starting | begin a new bounded recovery episode | A-037 |

### 7.2. Data freshness

| Condition | Result | Test IDs |
|---|---|---|
| LKG отсутствует | Missing | D-011 |
| age <= fresh TTL | Fresh | D-011 |
| fresh TTL < age <= stale TTL | Aging | D-011 |
| stale TTL < age <= expiry TTL | Stale | D-011 |
| age > expiry TTL | Expired | D-011 |

Границы определяются provider policy и тестируются включительно/исключительно.

### 7.3. Error policy

| Error codes | Slot | Disposition / state | UI projection | Test IDs |
|---|---|---|---|---|
| `ExecutableNotFound`, `ProcessStartFailed`, `ProcessExited`, `BrokenPipe`, `RequestTimeout`, `IoUnavailable` | соответствующий TransportState | Backoff; executable absence после probes может стать ActionRequired | LKG + stale/recovering, без modal UI | D-007, A-009, A-020..A-022 |
| `LoginRequired`, `SessionExpired`, `PermissionDenied` | соответствующий TransportState | WaitForUserAction, ActionRequired | LKG + actionable diagnostic | D-008, A-010, V-005 |
| `UnsupportedCliVersion`, `CapabilityMissing`, `ProtocolChanged` | соответствующий TransportState | WaitForVersionChange, ActionRequired | LKG + compatibility diagnostic | D-009, C-003, C-004, V-005 |
| `MalformedPayload`, `SchemaMismatch`, `NoSupportedWindows`, `InvalidPercentage`, `InvalidResetTime`, `ProviderMismatch` | соответствующий TransportState | reject candidate; backoff/probe по policy | LKG не меняется | D-002, D-003, D-015, C-004, L-010 |
| `OutOfOrderObservation` | transport diagnostics | no retry effect; idempotent reject | без пользовательского сигнала | D-005, D-006, L-006, T-004..T-009 |
| `StatusLineNotConfigured`, `SignalUnavailable`, `InvalidSnapshotPath` | ClaudeStatusLine TransportState | WaitForSignal/config; ClaudeDirect остаётся независим | direct fallback health | L-004, L-008, T-001 |
| `CacheReadFailed`, `CacheCorrupted`, `UnsupportedCacheSchema` | PersistenceState | startup продолжается, cache rejected | обычно silent diagnostics | P-002, P-003 |
| `CacheWriteFailed` | PersistenceState | accepted LKG не откатывается, retry отдельно | данные остаются видимы | P-008 |
| `PipelineNotStarted`, `PipelineAlreadyStopped`, `CommandQueueClosed` | PipelineLifecycleState | idempotent reject/no-op согласно lifecycle policy | обычно без UI | D-017, D-019, A-014 |
| `UnexpectedPipelineTermination`, `ShutdownTimeout` | PipelineLifecycleState | bounded restart либо controlled shutdown | provider fault/recovery channel | A-015, A-016, A-030, A-031, A-037..A-041 |

## 8. Definition of Done для кода

Для каждого production change обязательны:

- unit tests на happy path, error path и cancellation;
- проверка negative/edge cases;
- отсутствие новых analyzer warnings;
- XML/docs для public contracts и state invariants;
- redacted structured logs для новых operational flows;
- отсутствие `async void`, кроме WPF event handlers;
- все background tasks сохранены и await-ятся при shutdown;
- все subscriptions отписаны/disposed;
- all times UTC внутри Domain/Application;
- test names описывают `Given_When_Then` либо эквивалентное поведение.

## 9. Этапы реализации и зависимости

### M1. Архитектурный каркас

- Входной graph: `MainWindow -> LimitsCoordinator -> ProviderSupervisor -> current data sources`.
- Создать solution/projects, Domain models/errors/reducer/interactors, Domain.Tests и Architecture.Tests.
- Добавить characterization tests текущих provider/UI/desktop policies.
- Создать, но не подключать production, compatibility mapper `AppState -> legacy LimitsSnapshot`.
- Cache/settings schemas не меняются.
- Cutover отсутствует; executable работает полностью на legacy graph.
- Rollback: удалить новые неиспользуемые project references/files, runtime поведение не затронуто.

Выход: новый pure Domain зелёный, production behavior не изменён.

### M2. Store и pipeline runtime

- Реализовать AppStore, reducer/effect runner, runtimes, fake time/jitter/transports и lifecycle/concurrency tests.
- Создать временные `LegacyCodexTransportAdapter`/`LegacyClaudeTransportAdapter`, оборачивающие существующие transport/parsers **без** старого coordinator/supervisor scheduling.
- Выходной graph за feature flag: `WPF -> AppStore/new runtimes -> legacy transport adapters -> current data-source internals -> AppState-to-legacy-UI mapper`.
- Cutover: при включении нового graph одновременно отключаются `LimitsCoordinator` и `ProviderSupervisor`; двойной polling запрещён.
- Недельный cache читается через dual-read adapter: сначала schema v2, затем legacy; accepted legacy LKG записывается как v2 без удаления legacy файла.
- Rollback: выключить feature flag и вернуться к legacy coordinator; legacy cache/settings остаются читаемыми.

Выход: оба независимых runtime работают на существующих protocol implementations; нет двух владельцев расписания.

### M3. Codex vertical slice

- Разнести Codex parser/session/Windows process по нормативным projects, добавить typed outcomes, fixtures и smoke.
- Cutover только Codex adapter за provider-specific flag; Claude остаётся на M2 adapter.
- Сравнить redacted observation нового и временного parser-а на fixtures/реальном smoke, не запуская два app-server polling loops.
- Rollback: вернуть Codex к M2 legacy adapter; AppStore/runtime сохраняются.

### M4. Claude vertical slice

- Разнести Claude statusLine/direct/Windows signals по нормативным projects; реализовать ordering/per-window merge и per-transport state.
- Cutover только Claude adapter за provider-specific flag.
- После зелёного smoke удалить transport logic из legacy classes, но оставить rollback commit/tag до milestone acceptance.
- Cache v2 уже хранит per-window cursor/provenance; dual-read legacy cache назначает conservative captured time из legacy `ObservedAt`.
- Rollback: вернуть Claude к M2 legacy adapter; Codex M3 не затрагивается.

### M5. Presentation/WPF split

- Создать Presentation/VM и XAML bindings; текущая window/ghost/placement mechanics остаётся.
- Временный AppState-to-legacy-UI mapper заменяется прямой VM subscription.
- Cutover layout-by-layout: сначала один hidden/test host, затем vertical и horizontal production views.
- Settings dual-read: schema v2 читает legacy orientation/scale/opacity/placement/ghost и пишет v2; legacy settings не удаляются до M6.
- Rollback: переключить View на legacy mapper, provider graph не меняется.

### M6. Cleanup и hardening

- После истечения rollback window удалить legacy coordinator/supervisor/test harness/adapters и feature flags.
- Добавить diagnostics, chaos tests shutdown/process kill/cache corruption и полный desktop regression suite.
- Миграция cache/settings становится v2-only только после теста upgrade с копиями реальных redacted legacy файлов; backup/legacy files удаляются отдельным последующим решением, не автоматически в этой миграции.
- Обновить документацию фактическими типами и composition graph.
- Rollback M6 выполняется revert-commit-ом до удаления legacy файлов; пользовательские v2 state/settings сохраняются.

Следующий milestone начинается только после зелёного `dotnet test`, ручного smoke и зафиксированной rollback-процедуры предыдущего. В одном релизе запрещено одновременно менять provider transport, global state и WPF binding для одного и того же flow.

## 10. Матрица обязательных unit/contract tests

Каждый ID — обязательный автоматический тест либо parameterized test case. Реализация может добавлять тесты, но не удалять строки без изменения ТЗ.

### Domain/value/state

| Test ID | Покрываемый flow | Проверка |
|---|---|---|
| D-001 | validation | 0 и 100 процентов принимаются |
| D-002 | FL-07 | значения <0, >100, NaN/Infinity отклоняются typed error |
| D-003 | FL-07 | reset timestamp до допустимого прошлого/слишком далеко в будущем отклоняется policy |
| D-004 | FL-03 | валидная observation заменяет LKG атомарно |
| D-005 | FL-08 | меньшая generation отклоняется |
| D-006 | FL-08 | равная generation и меньший/equal sequence идемпотентно отклоняется |
| D-007 | FL-04 | transient failure сохраняет LKG |
| D-008 | FL-05 | auth failure переводит phase в ActionRequired |
| D-009 | FL-05 | compatibility failure выбирает WaitForVersionChange |
| D-010 | FL-01 | cache restore никогда не помечается Fresh без age evaluation |
| D-011 | freshness | все TTL boundaries дают ожидаемые состояния |
| D-012 | recovery | success очищает LastError/failure count и создаёт recovered event |
| D-013 | isolation | событие Claude не меняет Codex state и наоборот |
| D-014 | revision | фактическое изменение state увеличивает revision ровно один раз |
| D-015 | FL-07 | rejected observation не создаёт SaveCache effect |
| D-016 | cancellation | штатная отмена не создаёт LastError |
| D-017 | lifecycle | повторные start/stop дают определённый idempotent result |
| D-018 | immutability | опубликованный AppState нельзя изменить через backing collections |
| D-019 | revision | no-op/duplicate/idempotent command не меняет revision |
| D-020 | transport isolation | ошибка ClaudeStatusLine не меняет ClaudeDirect/Codex TransportState |
| D-021 | error slots | acquisition/cache/lifecycle errors не перезаписывают друг друга |
| D-022 | aggregate health | ProviderHealth детерминированно выводится из freshness/pipeline/transports/persistence |
| D-023 | exhaustive errors | каждый subtype provider-specific error union имеет явную disposition/state policy |

### AppStore/runtime

| Test ID | Покрываемый flow | Проверка |
|---|---|---|
| A-001 | store | concurrent writers обрабатываются одним reader без потери команд |
| A-002 | store | readers никогда не видят частичный transition |
| A-003 | store | exception subscriber изолирован |
| A-004 | store | slow subscriber не блокирует reducer |
| A-005 | FL-06 | четыре refresh reasons создают один attempt |
| A-006 | FL-06 | refresh во время attempt создаёт максимум один pending attempt |
| A-007 | single-flight | max concurrent transport calls равен 1 на provider |
| A-008 | independence | Codex и Claude attempts могут идти параллельно |
| A-009 | FL-04 | retry schedule соответствует policy и deterministic jitter |
| A-010 | FL-05 | auth failure не создаёт fast retry loop |
| A-011 | FL-11 | suspend останавливает scheduled refresh |
| A-012 | FL-11 | resume увеличивает generation и создаёт один jittered refresh |
| A-013 | FL-13 | stop отменяет attempt и завершает consumer |
| A-014 | FL-13 | repeated stop безопасен |
| A-015 | shutdown | timeout создаёт typed lifecycle diagnostic |
| A-016 | pipeline crash | unexpected consumer exception переводит state в Faulted |
| A-017 | bounded queue | refresh storm не увеличивает очередь выше capacity |
| A-018 | wake | потерянный push компенсируется scheduled reconciliation |
| A-019 | wake | завершившийся delay не оставляет waiter, способный поглотить следующий push |
| A-020 | breaker | достижение threshold переводит pipeline в CircuitOpen и блокирует обычные attempts |
| A-021 | breaker | CircuitOpen разрешает ровно один HalfOpen probe |
| A-022 | breaker | HalfOpen success закрывает breaker, failure снова открывает его |
| A-023 | lifecycle | Created + Start создаёт initialize effect и Starting state |
| A-024 | startup | RuntimeReady при due refresh создаёт один attempt |
| A-025 | startup | RuntimeReady без due refresh создаёт wake, а не attempt |
| A-026 | scheduling | Waiting + allowed refresh создаёт attempt |
| A-027 | scheduling | cooldown объединяет reasons без attempt |
| A-028 | pending | success с relevant pending reason запускает ровно один следующий attempt |
| A-029 | success | success без pending reason переходит Waiting и планирует healthy wake |
| A-030 | self-healing | runtime crash в budget увеличивает generation и перезапускает только один runtime |
| A-031 | self-healing | исчерпание restart budget оставляет provider Faulted до controlled probe |
| A-032 | control priority | stop/suspend обрабатываются во время зависшего tracked attempt и не ждут ordinary lane |
| A-033 | app lifecycle | app становится Running при provider BackingOff/ActionRequired/Faulted |
| A-034 | shutdown order | Store принимает final runtime transitions до закрытия и отклоняет команды после него |
| A-035 | recovery | BackingOff + WakeElapsed запускает ровно один retry attempt |
| A-036 | recovery | ActionRequired принимает только allowed Manual/Auth/VersionSignal и создаёт один HalfOpen probe |
| A-037 | self-healing | Faulted + allowed Manual/Resume/Probe начинает новый bounded recovery episode |
| A-038 | startup failure | retryable RuntimeStartFailed переходит RuntimeRestartBackoff и планирует restart wake |
| A-039 | startup failure | non-retryable/budget-exhausted RuntimeStartFailed переходит Faulted без loop |
| A-040 | forced shutdown | Stopping + ShutdownTimeout переходит ForceStopping и создаёт controlled-kill effect |
| A-041 | forced shutdown | completion controlled kill переводит ForceStopping в Stopped с diagnostic |
| A-042 | resume | Suspended + Resume при due refresh переходит сразу в Refreshing |
| A-043 | resume | Suspended + Resume без due refresh переходит Waiting и планирует wake |
| A-044 | startup recovery | RuntimeRestartBackoff + WakeElapsed переходит Starting и создаёт RestartRuntime effect |

### Persistence

| Test ID | Flow | Проверка |
|---|---|---|
| P-001 | FL-01 | валидный cache восстанавливает LKG |
| P-002 | FL-02 | malformed JSON даёт CacheCorrupted и не падает startup |
| P-003 | FL-02 | unknown schema version отклоняется |
| P-004 | cache | provider mismatch отклоняется |
| P-005 | cache | checksum mismatch отклоняется |
| P-006 | FL-03 | accepted observation записывается atomic replace |
| P-007 | cache | rejected observation не пишется |
| P-008 | cache | write failure не удаляет AppState LKG |
| P-009 | isolation | ошибка Codex cache не влияет на Claude cache |

### Codex provider

| Test ID | Flow | Проверка |
|---|---|---|
| C-001 | parse | актуальный redacted fixture маппится в weekly limit |
| C-002 | parse | decimal precision сохраняется |
| C-003 | parse | missing bucket -> NoSupportedWindows/CapabilityMissing |
| C-004 | parse | unknown shape -> ProtocolChanged/SchemaMismatch |
| C-005 | protocol | initialize/read request IDs последовательны |
| C-006 | session | concurrent requests сериализованы |
| C-007 | failure | timeout закрывает session и возвращает typed error |
| C-008 | failure | process exit/broken pipe закрывает child tree |
| C-009 | lifecycle | shutdown cancellation не является provider failure |
| C-010 | rotation | session rotation не допускает late result старой generation |

### Claude provider

| Test ID | Flow | Проверка |
|---|---|---|
| L-001 | parse | statusLine fixture сохраняет decimal precision обоих limits |
| L-002 | parse | direct `/usage` fixture маппит 5h/7d reset timestamps |
| L-003 | source select | свежий statusLine выигрывает без direct call |
| L-004 | source select | missing statusLine включает direct fallback |
| L-005 | source select | stale statusLine после cooldown включает direct fallback |
| L-006 | source select | более старый push не затирает новый direct result |
| L-007 | FL-10 | duplicate watcher/events схлопываются |
| L-008 | failure | invalid statusLine не ломает direct transport health |
| L-009 | failure | direct auth error сохраняет statusLine LKG и action state |
| L-010 | payload | partial payload следует документированной merge policy |
| L-011 | lifecycle | watcher/event/process resources освобождаются при stop |

### Transport ordering и multi-source state

| Test ID | Flow | Проверка |
|---|---|---|
| T-001 | FL-04 | failure одного transport меняет только его TransportState и aggregate health policy |
| T-002 | FL-03 | success transport-а очищает только его failure episode |
| T-003 | FL-08 | envelope с чужим provider/effect id отклоняется |
| T-004 | FL-08 | меньшая generation отклоняется без LKG/cache change |
| T-005 | FL-08 | duplicate/equal sequence является no-op без revision |
| T-006 | FL-08 | меньшая/equal source revision statusLine отклоняется |
| T-007 | FL-08 | более новый CapturedAtUtc выигрывает независимо от ReceivedAtUtc |
| T-008 | FL-08 | при равном captured time direct priority выигрывает у statusLine |
| T-009 | FL-08 | старый direct не выигрывает у нового statusLine только из-за priority |
| T-010 | FL-08 | Partial обновляет одно окно и сохраняет cursor/provenance другого |
| T-011 | FL-08 | Complete без required window отклоняется целиком |
| T-012 | FL-08 | invalid window в declared-complete payload не создаёт частичный downgrade |

### Presentation/ViewModel

| Test ID | Flow | Проверка |
|---|---|---|
| V-001 | FL-02 | Missing не показывает 0%, фиктивную дату или countdown |
| V-002 | FL-14 | Codex row показывает один metric, Claude — два |
| V-003 | formatting | проценты не содержат незначащих нулей и сохраняют до 2 знаков |
| V-004 | health | stale LKG остаётся видимым и имеет отдельный health state |
| V-005 | health | ActionRequired маппится в actionable tooltip/status |
| V-006 | FL-15 | countdown >24h меняется только по отображаемому часу |
| V-007 | FL-15 | countdown <24h меняется по отображаемой минуте |
| V-008 | FL-15 | urgency меняется на границах 1h/10m |
| V-009 | FL-15 | tick не вызывает refresh command |
| V-010 | diff | неизменный AppState не создаёт PropertyChanged |
| V-011 | diff | изменение Claude не нотифицирует Codex properties |
| V-012 | layout | обе раскладки используют одинаковые row VM values |
| V-013 | timezone | UTC reset форматируется в локальной zone только в Presentation |
| V-014 | commands | refresh UI dispatch-ит domain command, не вызывает adapter |
| V-015 | FL-16 | изменение UI-настройки не меняет provider state и не запускает refresh |
| V-016 | FL-16 | невалидные scale/opacity/placement отклоняются или нормализуются по явной policy |
| V-017 | FL-16 | unchanged setting не создаёт persistence write и PropertyChanged |
| V-018 | FL-14 | Expired LKG остаётся явно отличимым от fresh/missing и не превращается в нули |

### Desktop/Win32 policies

| Test ID | Flow | Проверка |
|---|---|---|
| W-001 | FL-17 | второй экземпляр не создаёт Store/runtimes/tray/window |
| W-002 | FL-17 | abandoned mutex безопасно передаёт ownership |
| W-003 | FL-18 | show нормализует placement, hide не останавливает pipelines |
| W-004 | FL-18 | menu z-order failure скрывает overlay и close гарантированно восстанавливает |
| W-005 | FL-18 | tray exit запускает orderly shutdown ровно один раз |
| W-006 | FL-19 | ghost enable применяет click-through/no-activate policy атомарно |
| W-007 | FL-19 | ghost disable снимает styles и не активирует недопустимое окно |
| W-008 | FL-19 | partial failure устанавливает cleanup-required и tray recovery остаётся доступным |
| W-009 | FL-20 | topmost reassertion bounded и не крадёт focus |
| W-010 | FL-20 | management menu временно имеет приоритет над overlay |
| W-011 | FL-21 | намеренное taskbar overlap разрешено при сохранении recovery strip |
| W-012 | FL-21 | off-screen persisted rect возвращается на существующий monitor |
| W-013 | FL-21 | удаление monitor-а переносит widget на nearest/primary monitor |
| W-014 | FL-21 | DPI conversion сохраняет physical anchor/aspect ratio |
| W-015 | FL-21 | drag/resize edge snapping не позволяет полностью потерять widget |

### Architecture

| Test ID | Проверка |
|---|---|
| X-001 | Domain не имеет project references |
| X-002 | Domain не ссылается на WPF/WinForms/Infrastructure/Providers |
| X-003 | Domain не использует IO/Process/JSON/concrete logging/system clock/random |
| X-004 | Presentation не ссылается на WPF/Infrastructure/Providers |
| X-005 | Provider parser assemblies не запускают process и не пишут cache |
| X-006 | WPF Views/MainWindow не создают provider transports |
| X-007 | Infrastructure не содержит domain state mutation вне command dispatch |
| X-008 | `Process`, Job Object, FileSystemWatcher и EventWaitHandle присутствуют только в Infrastructure.Windows/WPF host |
| X-009 | AppState public graph использует immutable collections/defensive copying |
| X-010 | Domain errors/state не содержат `Exception`, raw message/stdout или infrastructure types |

## 11. Матрица трассировки сценариев

| Use case | Нормативный flow | Основные автоматические тесты |
|---|---|---|
| UC-01 Запуск | FL-01, FL-02, FL-14 | D-010, A-023..A-025, A-033, P-001..P-005, V-001 |
| UC-02 Codex auto refresh | FL-03, FL-04, FL-09, FL-12 | D-004, D-007, C-001..C-010, A-007, A-009, A-020..A-022, A-035 |
| UC-03 Claude statusLine | FL-03, FL-06, FL-08, FL-10 | L-001, L-003, L-006..L-008, T-003..T-012, A-005, A-006, A-019 |
| UC-04 Claude direct fallback | FL-03, FL-04, FL-08, FL-10 | L-002, L-004, L-005, L-009, T-001, T-002, A-009 |
| UC-05 Manual refresh | FL-06, FL-09 | A-005, A-006, A-020..A-022, A-032, V-014 |
| UC-06 LKG при сбое | FL-04, FL-14 | D-007, D-012, D-020..D-022, P-008, V-004, V-018 |
| UC-07 Авторизация | FL-05 | D-008, D-009, A-010, A-036, L-009, V-005 |
| UC-08 Sleep/resume | FL-08, FL-11 | D-005, D-006, A-011, A-012, A-042, A-043, T-004, T-005 |
| UC-09 Countdown | FL-15 | V-006..V-010, V-013 |
| UC-10 Завершение | FL-13 | D-016, D-017, A-013..A-015, A-032, A-034, A-040, A-041, C-009, L-011 |
| UC-11 Повреждённый cache | FL-02 | P-002..P-005, P-009 |
| UC-12 UI-настройки | FL-16 | V-015..V-017 |
| UC-13 Runtime self-healing | FL-12 | A-016, A-030, A-031, A-033, A-037..A-039, A-044 |
| UC-14 Single instance | FL-17 | W-001, W-002 |
| UC-15 Tray | FL-18, FL-13 | W-003..W-005, A-034 |
| UC-16 Ghost/topmost recovery | FL-19, FL-20 | W-006..W-010 |
| UC-17 Placement/DPI/monitors | FL-21 | W-011..W-015 |
| UC-18 Invalid provider payload | FL-07 | D-002, D-003, D-015, C-003, C-004, L-010, T-011, T-012 |

Трассировка является нормативной: переименование use case, flow или test ID требует синхронного обновления всех трёх разделов.

## 12. Integration и ручные acceptance scenarios

Эти проверки дополняют, но не заменяют unit tests:

1. Запуск offline с LKG: данные появляются сразу как stale, консоль не мигает.
2. Запуск offline без LKG: нет похожих на реальные заглушек.
3. Убить Codex app-server во время read: Claude продолжает обновляться, Codex восстанавливается.
4. Отключить Claude statusLine: direct fallback обновляет данные в фоне.
5. Одновременно нажать refresh и вызвать statusLine event: один process/attempt на provider.
6. Потерять login: нет process storm, LKG сохранён, диагностика понятна.
7. Sleep/resume: по одному refresh на provider, старый ответ не применяется.
8. Закрыть приложение во время обоих attempts: нет orphan child processes.
9. Повредить один cache: приложение запускается, второй provider восстановлен.
10. Переключать layouts/scale/ghost mode во время refresh: provider state не теряется, UI не блокируется.
11. Запустить второй экземпляр: не появляется второе окно/tray/process tree.
12. Включить/выключить ghost mode поверх интерактивного окна: ввод всегда получает ожидаемый target, tray recovery доступен.
13. Открыть system/tray menu поверх overlay: меню не перекрывается необратимо, overlay восстанавливается после close.
14. Отключить монитор/сменить DPI: widget остаётся достижимым и сохраняет taskbar placement intent.
15. Искусственно завершить runtime actor: перезапускается только один provider в пределах restart budget.

## 13. Критерии приёмки архитектуры

Архитектурная миграция принимается только при одновременном выполнении:

- `dotnet build` и `dotnet test` проходят с чистого checkout;
- все Test ID из матрицы реализованы и трассируются в названиях/traits;
- Domain/Application/Presentation имеют не менее 90% line coverage и 100% branch coverage state transitions/error policies; исключения документируются по строкам и утверждаются отдельно;
- Domain/Presentation architecture tests проходят;
- production executable проходит ручные scenarios 1–10;
- существующие geometry/ghost/topmost/settings regression tests перенесены и зелёные;
- real Codex/Claude smoke проходит в пользовательской сессии без raw output в логах;
- нет console window flash;
- нет orphan processes после exit;
- документация обновлена по фактическим именам типов и проектам;
- legacy `LimitsCoordinator`, `ProviderSupervisor` и бизнес-логика `MainWindow` удалены после полной замены.

## 14. Правила code review

Reviewer обязан отклонить change, если:

- доменная логика реализована в View, ViewModel adapter или infrastructure;
- появился новый untyped `catch (Exception)` без boundary mapping/critical handling;
- operational exception пересёк provider adapter boundary;
- mutable state читается/пишется несколькими tasks без single owner;
- background task fire-and-forget и не контролируется lifecycle;
- test зависит от случайного времени/sleep;
- error path стирает LKG;
- refresh action может запустить параллельный process одного provider;
- UI показывает unknown как `0` либо фиктивную дату;
- лог может содержать credential/raw provider payload.
