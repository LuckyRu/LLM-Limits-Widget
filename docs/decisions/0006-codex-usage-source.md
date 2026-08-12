# ADR 0006: Источник лимитов Codex для личного виджета

Дата: 2026-08-12  
Статус: accepted for spike

## Контекст

Виджету нужны остаток и время сброса Codex для личного ChatGPT Plus/Pro. Обычный OpenAI API показывает API billing/rate limits и не является источником остатка потребительской подписки ChatGPT.

CLI и Desktop показывают лимиты пользователю, но их UI-вывод не является удобным внешним machine-readable контрактом. В текущем CLI обнаружен App Server с JSON-RPC account API.

## Решение

Сначала исследовать и реализовывать provider поверх локального codex app-server --stdio:

- запускать дочерний процесс на Windows;
- использовать initialize, initialized, account/rateLimits/read;
- слушать account/rateLimits/updated;
- выбирать rateLimitsByLimitId.codex, с backward-compatible fallback на rateLimits;
- сопоставлять окна по длительности, а не по позиции;
- использовать usedPercent и resetsAt;
- не выполнять login из виджета и не извлекать cookies;
- считать источник version-coupled, иметь schema guard и fallback unavailable.

## Почему

- протокол уже даёт структурированные поля, необходимые UI;
- нет зависимости от DOM или CLI text formatting;
- credentials остаются в Codex-managed local auth;
- рабочий транспорт stdio проверен на Windows;
- OpenAI Codex repository документирует этот API и пример интеграции.

## Что не выбираем на этом этапе

- внутренний Desktop IPC/SQLite/logs;
- парсинг /status;
- browser automation и cookies;
- OpenAI API key;
- remote-control;
- Unix-only managed daemon lifecycle.

## Последствие

До интеграционного теста в авторизованной пользовательской сессии автоматический Codex provider остаётся техническим кандидатом, а не подтверждённой частью MVP. Виджет обязан различать fresh, stale и unavailable.
