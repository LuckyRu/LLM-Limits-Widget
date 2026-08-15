# Документация проекта

Эта папка — единое место для продуктовых решений, результатов исследований и технических договорённостей.

## Структура

- `product-brief.md` — цель продукта, аудитория, MVP и критерии успеха.
- `research/` — исследования, источники и проверяемые гипотезы.
- `technical/` — feasibility-проверки, технические риски и результаты spike.
- `decisions/` — короткие записи решений и их причин.

Актуальная реализационная документация:

- [FloatingOverlay implementation](technical/floating-overlay-implementation-2026-08-13.md) — архитектура WPF overlay, native placement controller, защита границ монитора, taskbar overlap, persistence, DPI и тестирование.
- [Ghost mode system design](technical/ghost-mode-system-design-2026-08-13.md) — полностью видимый click-through режим без mouse/keyboard interaction, shell-topmost поверх taskbar/system tray, управление через tray, persistence, recovery и тестовая матрица.
- [Limits domain and widget data flow](technical/limits-domain-and-widget-data-flow-2026-08-15.md) — provider-neutral модели, coordinator, обновление обеих раскладок, stale/unavailable semantics и lifecycle.
- [Architecture v2: системный дизайн](technical/architecture-v2-system-design-2026-08-15.md) — целевые границы домена, глобальный immutable state, independent provider pipelines, typed errors, concurrency/lifecycle и стратегия тестирования.
- [Architecture v2: ТЗ на реализацию](technical/architecture-v2-implementation-spec-2026-08-15.md) — deliverables, use cases, нормативные flows, state tables, этапы миграции и обязательная матрица тестов.
- [Architecture v2: M1 implementation](technical/architecture-v2-m1-implementation-2026-08-15.md) — фактически созданный Domain-срез, его границы, текущие ограничения и команды проверки.
- [Architecture v2: M2 implementation](technical/architecture-v2-m2-implementation-2026-08-15.md) — single-writer AppStore, priority lanes и tracked provider runtime.
- [Architecture v2: M3 Codex implementation](technical/architecture-v2-m3-implementation-2026-08-15.md) — новый pure Codex parser и mapping в domain observation envelope.
- [Architecture v2: M4 Claude implementation](technical/architecture-v2-m4-implementation-2026-08-15.md) — statusLine/direct `/usage` parsers, typed transport errors и mapping в domain observation envelope.

## Правила ведения

- Дату документа указываем в формате `YYYY-MM-DD`.
- Наблюдение, цитату источника и нашу интерпретацию разделяем явно.
- Изменение scope фиксируем в decision log.
- Текущую гипотезу не выдаём за подтверждённый факт.
- Для каждого исследования записываем следующий шаг и критерий, по которому гипотеза будет подтверждена или отклонена.

## Актуальное техническое решение

Для Codex первым автоматическим каналом исследуем локальный codex app-server --stdio с JSON-RPC методом account/rateLimits/read. Подробности и ограничения: [исследование получения лимитов Codex](technical/codex-usage-acquisition-2026-08-12.md) и [ADR 0006](decisions/0006-codex-usage-source.md).

Для Claude принято гибридное получение лимитов: statusLine bridge для быстрых обновлений и прямой вызов CLI /usage при stale-состоянии, на старте и по ручному обновлению. Подробности: [исследование получения лимитов Claude](technical/claude-usage-acquisition-2026-08-12.md), [ADR 0007](decisions/0007-claude-usage-source.md) и [ADR 0008](decisions/0008-claude-hybrid-usage-transport.md).

## Ближайшие документы

1. Интервью с пользователями, регулярно работающими с ChatGPT и Claude.
2. Feasibility spike по получению usage-данных на Windows без хранения паролей.
3. Черновик UX для tray icon, popup и состояний `актуально / устарело / недоступно`.
