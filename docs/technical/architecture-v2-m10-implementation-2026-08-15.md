# Architecture v2: M10 WPF composition root

Дата: 2026-08-15  
Статус: feature-flagged composition seam, UI cutover pending

## Реализовано

- `ArchitectureV2CompositionRoot` в WPF-проекте;
- сборка новых Domain/Application/Infrastructure/Presentation projects через WPF composition;
- создание Codex app-server runtime, Claude direct runtime, Claude statusLine pump, AppStore и WidgetViewModel;
- marshaling Store state notifications обратно на WPF Dispatcher;
- lifecycle disposal для watcher, provider runtimes, effect executor и Store;
- запуск нового контура только по `--arch-v2` или `LLM_WIDGET_ARCH_V2=1`;
- обычный запуск остаётся на legacy coordinator и не получает двойных provider calls.

## Ограничение feature flag

На этом этапе новый ViewModel ещё не заменяет legacy row rendering. Flag предназначен для composition/lifecycle smoke и проверки логов/процессов; production UI cutover будет отдельным изменением с mapper-а `WidgetViewModel` в WPF controls и rollback verification.

## Проверка

```powershell
dotnet build .\spikes\FloatingOverlay\FloatingOverlay.csproj -p:UseSharedCompilation=false
```

Ожидаемый результат: WPF проект и все новые архитектурные проекты собираются без warning/error.
