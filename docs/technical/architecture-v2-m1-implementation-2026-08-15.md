# Architecture v2: M1 implementation status

Дата: 2026-08-15  
Статус: implemented, not wired into the running WPF host

## Что реализовано

Создан первый независимый core-срез:

- [LLMLimitsWidget.Domain](../../src/LLMLimitsWidget.Domain/LLMLimitsWidget.Domain.csproj) — отдельная `net10.0`-сборка без ссылок на WPF, WinForms, Windows infrastructure и текущий `FloatingOverlay`;
- immutable `AppState`, `ProviderState`, `ProviderPipelineState`, `TransportState`, `PersistenceState`;
- provider/transport model для Codex и Claude;
- typed error hierarchy: Codex acquisition, Claude statusLine, Claude direct, persistence и lifecycle errors;
- `RemainingPercent` value object с отклонением значений вне `0..100`;
- `ProviderObservationEnvelope` с generation, sequence, source revision, captured/received timestamps и per-window cursor;
- pure `AppReducer` для startup, refresh, runtime-ready, runtime-start-failure, runtime-restart-backoff, wake, attempt completion и shutdown;
- декларативные effects: start/restart/stop runtime, provider attempt, wake и cache save;
- first M1 tests для immutable state transitions, typed validation, LKG acceptance, late completion, restart backoff и orderly stop.

## Что пока намеренно не подключено

Текущий `FloatingOverlay` executable продолжает работать на старом графе. Новый Domain пока не вызывает:

- Codex app-server;
- Claude statusLine/direct transports;
- persistence;
- WPF Dispatcher/ViewModel;
- background AppStore/effect runner.

Это будет сделано следующими вертикальными срезами, чтобы не создавать второй polling loop и не ломать пользовательский executable.

## Проверка

```powershell
dotnet build .\src\LLMLimitsWidget.Domain\LLMLimitsWidget.Domain.csproj -p:UseSharedCompilation=false
dotnet build .\tests\LLMLimitsWidget.Domain.Tests\LLMLimitsWidget.Domain.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Domain.Tests\LLMLimitsWidget.Domain.Tests.csproj --no-build
dotnet run --project .\tests\LLMLimitsWidget.Architecture.Tests\LLMLimitsWidget.Architecture.Tests.csproj
```

Ожидаемый результат:

```text
Domain M1: all cases passed.
Architecture M1: domain boundary passed.
```

Тестовые проекты пока остаются self-hosted console runners без NuGet test runner. Это временная мера для работы в текущем offline-friendly окружении; переход на discoverable test runner выполняется отдельным шагом после стабилизации solution structure.
