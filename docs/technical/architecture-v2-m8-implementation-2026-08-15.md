# Architecture v2: M8 Claude statusLine signal pump

Дата: 2026-08-15  
Статус: implemented signal ingress slice, WPF composition pending

## Реализовано

- `ClaudeStatusLineSignalPump` с `FileSystemWatcher` для `Changed`, `Created` и `Renamed`;
- bounded channel на одну pending signal и debounce 100 ms, чтобы burst записи statusLine не создавал очередь одинаковых чтений;
- первоначальная reconciliation read при старте pump;
- graceful cancellation/disposal watcher и worker task;
- successful parsed snapshot публикуется только как priority `ObservationReceivedCommand` в `IApplicationCommandSink`;
- parser errors не пробиваются исключением из watcher loop;
- test с реальным временным файлом проверяет watcher → parser → domain command flow.

## Границы

Pump не меняет AppState напрямую и не вызывает direct Claude CLI. Он только превращает OS signal в domain ingress; source selection и direct fallback остаются отдельной policy/runtime responsibility. При остановке watcher и worker гарантированно освобождаются, а уже поставленная команда остаётся валидной и обрабатывается single-writer Store.

## Проверка

```powershell
dotnet run --project .\tests\LLMLimitsWidget.Infrastructure.Windows.Tests\LLMLimitsWidget.Infrastructure.Windows.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Infrastructure M7/M8: all cases passed.
```
