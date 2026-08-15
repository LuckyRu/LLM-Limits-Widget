# Architecture v2: M14 default cutover

Дата: 2026-08-15  
Статус: реализовано

## Решение

Architecture v2 теперь является основным production path при обычном запуске приложения. Это убирает необходимость помнить feature flag и исключает расхождение между проверяемым и обычным пользовательским сценарием.

Legacy path сохранен как временный rollback:

```powershell
LLMLimitsWidget.FloatingOverlay.exe --legacy
```

Также поддерживаются environment overrides:

- `LLM_WIDGET_LEGACY=1` — принудительно legacy;
- `LLM_WIDGET_ARCH_V2=0` — обратная совместимость с feature-flag rollout и принудительный legacy;
- `LLM_WIDGET_ARCH_V2=1` — явно оставить v2.

Приоритеты: `--legacy` выше environment overrides; без rollback-признаков выбирается v2.

## Защита от двойного экземпляра

v2 и legacy используют разные named mutex только для переходного периода:

- v2: `Local\\LLMLimitsWidget.FloatingOverlay.ArchitectureV2`;
- legacy: `Local\\LLMLimitsWidget.FloatingOverlay`.

После перезапуска приложения пользовательский обычный запуск будет v2. Разные mutex намеренно оставлены до удаления legacy path, чтобы можно было диагностически запустить rollback рядом с уже работающим v2 без ложного duplicate-instance результата.

## Проверки приемки

- обычный запуск без аргументов должен записывать `ArchitectureV2 composition_feature_enabled` и `composition_started`;
- запуск с `--legacy` не должен создавать v2 composition и должен сохранить legacy coordinator;
- запуск с `LLM_WIDGET_LEGACY=1` эквивалентен `--legacy`;
- `--legacy` имеет приоритет над `LLM_WIDGET_ARCH_V2=1`;
- оба режима сохраняют single-instance protection в рамках своего режима;
- v2 сохраняет реальные observations Codex/Claude, typed errors, retry и clean shutdown из M13.

## Ограничение

Legacy path пока не удален: он остается диагностическим rollback до отдельного решения после периода эксплуатации v2 и визуальной приемки в пользовательском окружении.
