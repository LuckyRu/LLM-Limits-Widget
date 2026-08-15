# Architecture v2: M15 reliability fixes

Дата: 2026-08-15  
Статус: реализовано и проверено

## Исправленные flows

- После успеха Codex планируется через 2 минуты, Claude Direct — через 5 минут. Wake имеет identity; устаревший wake не запускает лишний запрос.
- Manual refresh во время активного attempt coalescing-ится и выполняется следующим attempt, а не теряется.
- Retry policy учитывает `RetryDisposition`: только `Immediate` и `Backoff` создают wake; `WaitForSignal`, `WaitForVersionChange`, `WaitForUserAction` и `Never` не запускают polling-loop.
- Ошибочный observation использует тот же controlled failure flow и не оставляет pipeline в вечном `BackingOff` без wake.
- Успешный cache write/read отражается в `PersistenceState`. Cache v2 атомарный, provider-scoped, не содержит credentials или raw CLI output; cache LKG восстанавливается до первого network/CLI refresh.
- Повторный statusLine snapshot без нового окна не продлевает freshness. Ошибки чтения statusLine публикуются в domain transport state; watcher пересоздается и повторно проверяет путь через bounded recovery delay.
- WPF countdown планирует следующий repaint только в момент смены текста. Отсутствующие и истекшие countdown не создают таймер; multi-day/week formula не возвращает время в прошлом.
- Stop flow проходит через `StopApplicationCommand`, отменяет active attempts и stale wakes, ожидает остановку runtime, затем dispose'ит Codex app-server session. Store isolирует исключение effect executor через typed `RuntimeFaultedCommand`.
- v2 и legacy используют единый mutex, поэтому rollback не позволяет запустить второй виджет.

## Проверки

- расширены domain tests: periodic success wake, stale wake identity, queued refresh и `WaitForVersionChange`;
- расширены presentation tests: отсутствующий countdown, multi-day и week boundary;
- добавлены infrastructure cache round-trip и runtime disposable transport test;
- полный набор из семи тестовых projects: passed;
- real smoke: Codex `1` window, Claude `2` windows, cache save acknowledged;
- при запущенном v2 второй запуск с `--legacy` завершился с exit code `0` как duplicate instance.

## Ограничение smoke

Ghost overlay не выдаёт обычный interactive main-window handle, поэтому `CloseMainWindow()` не является валидным способом автоматизированно проверить tray-driven graceful exit. Lifecycle остановки покрыт application/runtime tests; пользовательский выход через tray остаётся штатным production-path.
