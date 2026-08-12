# ADR 0007: Источник лимитов Claude Pro для личного виджета

Дата: 2026-08-12  
Статус: accepted for spike

## Контекст

Claude Pro и Claude Code используют общий subscription usage. Нужны остаток и время сброса 5-часового и недельного окон. Anthropic API billing не является остатком Claude Pro.

В официальной документации Claude Code обнаружен структурированный JSON-контракт пользовательского statusLine: после первого API-ответа CLI передаёт rate_limits.five_hour и rate_limits.seven_day с процентом использования и Unix reset timestamp.

## Решение

Сначала исследовать provider через локальный PowerShell statusline bridge:

- Claude Code передаёт JSON через stdin;
- bridge сохраняет только version, rate_limits, observed_at, source;
- snapshot записывается атомарно в %LOCALAPPDATA%;
- виджет читает последний snapshot и вычисляет remaining;
- отсутствующие окна отображаются как unavailable;
- TTL и stale state обязательны;
- API key, OAuth token, cookies и credentials-файлы не читаются.

## Почему

- это официальный интерфейс расширения Claude Code;
- JSON уже содержит ровно нужные поля;
- bridge не выполняет дополнительный запрос и не расходует лимит;
- работает с личной Claude.ai подпиской, а не только с API;
- подходит для Windows PowerShell.

## Что не выбираем на этом этапе

- Anthropic API rate-limit headers;
- undocumented /api/oauth/usage;
- парсинг /usage или /status;
- Desktop IPC, Electron storage, локальные базы и DOM;
- чтение credentials или browser cookies;
- запуск claude -p только ради usage.

## Последствие

Claude provider на первом этапе будет push-on-session: если Claude Code не запускался или не отправил новый statusline snapshot, виджет не может обещать свежий показатель. Для автономного значения после перезагрузки нужна отдельная подтверждённая интеграция либо явное состояние stale/unavailable.
