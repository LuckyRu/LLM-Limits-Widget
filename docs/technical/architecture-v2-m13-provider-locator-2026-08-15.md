# Architecture v2: M13 provider executable locator

Дата: 2026-08-15  
Статус: реализовано и проверено реальным запуском

## Найденная проблема

Первый реальный запуск нового composition root подтвердил получение Codex, но Claude оставался в состоянии `ProcessStartFailed`. Legacy-путь при этом успешно получал Claude, поэтому проблема была локальной для способа запуска, а не в авторизации или парсере `/usage`.

Причина: Windows-установка Claude Code в текущей среде не предоставляет `claude.exe` через обычный `PATH`. Исполняемый файл находится внутри packaged Claude cache.

## Решение

Добавлен `ProviderExecutableLocator` в Windows infrastructure:

- `CODEX_CLI_PATH` и `CLAUDE_CODE_PATH` позволяют явно задать путь для диагностики и portable/нестандартных установок;
- Codex по умолчанию запускается через `codex.exe` и использует штатный `PATH`-resolution;
- Claude сначала ищется в `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code` рекурсивно;
- при отсутствии packaged binary сохраняется fallback на `claude.exe`, чтобы типизированная ошибка `ExecutableNotFound` проходила через штатный retry/health flow;
- ошибки доступа к каталогу не ломают composition root и не обходят доменный error mapping.

Composition root больше не содержит provider-specific path discovery и получает уже разрешенные пути от Windows adapter layer.

## Реальный smoke после исправления

Запущено:

```powershell
LLMLimitsWidget.FloatingOverlay.exe --arch-v2
```

В application log для одного процесса зафиксировано:

```text
provider_observation_accepted codexFreshness=1 codexWindows=1 codexTransportError=""
provider_observation_accepted claudeFreshness=1 claudeWindows=2 claudeTransportError=""
composition_stopped
```

Это подтверждает, что новый pipeline получает реальные данные обоих провайдеров, публикует их в single-writer store и корректно завершает lifecycle без всплывающей консоли.

## Проверки

- сборка `FloatingOverlay.csproj`: passed, 0 warnings, 0 errors;
- infrastructure tests: проверяют direct Claude, Codex app-server, statusLine reader/watch pump и typed executable error;
- отдельный locator override test проверяет диагностические environment overrides;
- real smoke `--arch-v2`: Codex и Claude observations accepted, clean shutdown.

Новый путь остается feature-flagged (`--arch-v2` / `LLM_WIDGET_ARCH_V2=1`) до завершения отдельной визуальной приемки; legacy path сохраняется как rollback.
