# Architecture v2: M12 real smoke result

Дата: 2026-08-15  
Статус: smoke passed

## Сценарий

Запущена Debug-сборка:

```powershell
LLMLimitsWidget.FloatingOverlay.exe --arch-v2
```

На машине уже работал Release-экземпляр legacy path. Для этого smoke feature flag использует отдельный mutex `Local\\LLMLimitsWidget.FloatingOverlay.ArchitectureV2`, поэтому проверка не была ошибочно заблокирована single-instance защитой legacy процесса.

## Фактический результат

В application log зафиксированы:

```text
ArchitectureV2 composition_started
ArchitectureV2 composition_feature_enabled
Wpf window_loaded
Wpf window_closing
App shutdown_begin exitCode=0
ArchitectureV2 composition_stopped
```

Новой видимой консоли не появилось. Legacy Release-экземпляр не требовал остановки для запуска smoke. Debug smoke завершился штатно; provider transport child processes были освобождены через composition disposal.

## Ограничение результата

Smoke подтверждает WPF lifecycle, feature flag, mutex isolation и clean shutdown. Полноценная визуальная проверка реальных значений в новом VM-контуре требует отдельного foreground UI inspection; данные provider adapters уже покрыты fixture/infrastructure tests.
