# Получение лимитов Codex из desktop и CLI

Дата: 2026-08-12  
Статус: техническое исследование  
Scope: личный ChatGPT Plus/Pro, Windows 11, Codex Desktop и Codex CLI.

## Вывод

Для Codex найден наиболее перспективный канал без OpenAI API: локальный codex app-server по JSON-RPC через stdio.

Он уже содержит отдельный метод account/rateLimits/read и уведомление account/rateLimits/updated. В ответе доступны:

- тип плана;
- основной и дополнительный лимитные окна;
- usedPercent;
- resetsAt как Unix timestamp;
- длительность окна;
- credits и состояние достижения лимита;
- несколько лимитных bucket’ов через rateLimitsByLimitId, в том числе codex.

Это не обычный публичный consumer SDK для внешних приложений. Это протокол Codex App Server, который нужно считать version-coupled и проверять на каждой поддерживаемой версии CLI/Desktop. При этом сам протокол документирован в репозитории OpenAI Codex и прямо рекомендует этот метод для чтения rate limits.

Решение для следующего spike: запускать локальный Codex CLI как дочерний процесс виджета, проходить JSON-RPC handshake, читать snapshot и поддерживать локальный cache. API-ключ OpenAI для этой задачи не использовать.

## Что проверено локально

Установленная версия:

- codex-cli 0.147.0;
- Windows 11, 10.0.26200;
- команды app-server, remote-control, doctor доступны;
- app-server --stdio запускается на Windows;
- handshake initialize + initialized проходит;
- запрос account/rateLimits/read распознаётся протоколом.

Тестовый запрос без пользовательской авторизации вернул ожидаемую ошибку:

~~~text
codex account authentication required to read rate limits
~~~

Это ограничение среды исследования, а не отказ протокола: текущий запущенный CLI использовал изолированный CODEX_HOME без credentials. Поэтому следующий интеграционный тест нужно выполнить в пользовательской сессии, где Codex CLI уже авторизован через ChatGPT.

### Успешный тест в авторизованной пользовательской сессии

Дата теста: 2026-08-12. Тест выполнен через локальный CODEX_HOME пользователя, без API-ключа и без запуска Codex-задачи.

Ответ account/rateLimits/read:

~~~text
planType: plus
primary.usedPercent: 38
primary.windowDurationMins: 10080
primary.resetsAt: 1787025796
secondary: null
credits.hasCredits: false
credits.balance: 0
rateLimitReachedType: null
~~~

Интерпретация:

- текущий доступный bucket — недельное окно;
- remaining составляет 62%;
- сброс: 2026-08-18 07:03:16 по локальному времени пользователя;
- отдельное 5-часовое окно в этом snapshot отсутствует и не должно отображаться как 0%;
- авторизация и получение лимитов через App Server подтверждены на реальном аккаунте ChatGPT Plus.

Это также показывает, что provider обязан поддерживать частичный ответ: конкретные лимитные окна могут отсутствовать для плана или текущего состояния аккаунта.

Локальная схема была сгенерирована двумя способами:

~~~text
codex app-server generate-json-schema --out tmp/app-server-schema
codex app-server generate-json-schema --out tmp/app-server-schema-stable
~~~

Метод account/rateLimits/read и notification account/rateLimits/updated присутствуют и в stable-схеме CLI 0.147.0. Файлы схемы оставлены в tmp/ и не являются частью исходного кода проекта.

## Каналы и их приоритет

| Канал | Что получаем | Плюсы | Риски | Решение |
|---|---|---|---|---|
| Codex App Server через stdio | структурированные лимиты и reset timestamp | локально, без парсинга UI, без OpenAI API key, JSON-RPC | версия протокола, авторизация и ошибки подключения | основной кандидат для MVP spike |
| App Server через WebSocket | то же, с отдельным транспортом | удобен для долгоживущего клиента | нужно управлять endpoint/auth; лишняя поверхность | не нужен для первого Windows MVP |
| CLI /statusline | человекочитаемый statusline с five-hour-limit и weekly-limit | простой fallback, поддержан самим CLI | формат UI, нет гарантии JSON, процесс/TTY | fallback для диагностики |
| CLI /status | ручной просмотр account/usage | уже знаком пользователю | не предназначен как machine-readable API; сообщались stale/missing значения | только ручная проверка и fallback |
| Desktop app IPC/App Server | тот же внутренний account state | потенциально не нужно запускать второй процесс | публичного стабильного desktop IPC для внешнего приложения не найдено | исследовать после CLI spike |
| Веб-страница Usage | пользовательский источник истины/сверка | отображает данные в ChatGPT UI | browser automation, cookies/session, ломкость DOM | ручная сверка, не первый автоматический канал |
| OpenAI API | API usage/rate limits | официальная API-интеграция | это API billing, не остаток ChatGPT Plus/Pro | последний вариант и не замена подписочному usage |

## App Server: минимальный контракт

Запуск:

~~~text
codex app-server --stdio
~~~

Handshake:

~~~json
{"id":1,"method":"initialize","params":{"clientInfo":{"name":"llm-limits-widget","title":"LLM Limits Widget","version":"0.1.0"},"capabilities":{"experimentalApi":false}}}
{"method":"initialized","params":{}}
~~~

Чтение snapshot:

~~~json
{"id":2,"method":"account/rateLimits/read"}
~~~

Первый выбор bucket’а:

1. result.rateLimitsByLimitId.codex, если объект присутствует;
2. result.rateLimits, если limitId равен codex;
3. состояние unavailable, если Codex bucket отсутствует.

Окна нельзя выбирать по позиции в JSON. Их нужно распознавать по windowDurationMins:

- 300 минут — five-hour window;
- 10080 минут — weekly window.

Остаток вычисляется как clamp(100 - usedPercent, 0, 100). Время сброса берётся из resetsAt, без попытки вывести его из локальных часов.

Notification:

~~~json
{"method":"account/rateLimits/updated","params":{"rateLimits":{}}}
~~~

Notification sparse: она может содержать не все поля. Виджет должен объединять её с последним полным snapshot или делать повторный account/rateLimits/read. null в sparse update нельзя трактовать как сброс ранее известного значения.

## Авторизация и безопасность

Официальная документация App Server описывает ChatGPT-managed login: Codex сам ведёт OAuth flow и сохраняет/обновляет credentials. Внешнему виджету не нужно получать пароль или читать cookies браузера.

Для MVP:

- не реализовывать account/login/start в виджете;
- использовать уже авторизованный локальный Codex CLI;
- не запрашивать и не хранить CODEX_ACCESS_TOKEN;
- не использовать remote-control pair;
- не запускать API-key login;
- не логировать stdout/stderr с credentials;
- если CLI не авторизован, показывать Codex: требуется вход и действие открыть Codex.

codex app-server daemon на Windows не является доступным managed lifecycle: команда сообщила, что lifecycle daemon поддерживается только на Unix. Для Windows baseline нужно использовать прямой ephemeral-процесс app-server --stdio, а не рассчитывать на Unix socket daemon.

## Desktop и CLI: что реально можно переиспользовать

### CLI

CLI уже умеет показать два лимитных окна через statusline placeholders. Это полезно как диагностический fallback и как ручная проверка пользовательского результата. Но запуск отдельного CLI-процесса ради парсинга текста не должен быть главным источником: вывод меняется вместе с UI, а команда /status не обещает стабильный JSON-контракт.

### Desktop

Desktop и CLI используют общий Codex account/agentic usage concept, но отдельный стабильный публичный IPC-контракт desktop-приложения для внешнего overlay не найден. Прямое подключение к случайным локальным портам, логам, SQLite или Electron storage не принимаем как базовую архитектуру без отдельного security/reliability review.

Практическая стратегия:

1. Первый provider запускает локальный codex app-server --stdio.
2. Если Codex Desktop уже работает, виджет не подключается к его внутреннему процессу и не внедряется в desktop.
3. После рабочего CLI provider проверить, использует ли Desktop тот же App Server protocol и можно ли безопасно избежать второго процесса.
4. Если desktop-путь не даёт стабильного контракта, оставить CLI provider как основной официальный локальный канал.

## Надёжность данных

Источником значения считаем backend snapshot, но отображаем доверие и свежесть:

- fresh: snapshot получен успешно и моложе заданного TTL;
- stale: процесс/сеть временно недоступны, показываем последнее значение с временем получения;
- unavailable: нет авторизации, bucket/window отсутствует или схема не поддерживается;
- error: протокол/CLI завершился с ошибкой.

Нельзя молча показывать 0%, если поле отсутствует. Это должно быть unavailable, иначе виджет создаст ложное ощущение исчерпанного лимита.

Исследование GitHub также показывает, что значения в CLI, Desktop и веб-Usage могут расходиться, пропадать для одного из окон или обновляться с задержкой. Поэтому в UI нужен источник и время последнего обновления, а в provider — диагностика версии CLI и raw error category без сохранения секретов.

## Следующий spike

1. На реальной авторизованной Windows-сессии запустить codex app-server --stdio.
2. Выполнить handshake и account/rateLimits/read.
3. Проверить наличие rateLimitsByLimitId.codex, primary/secondary и reset timestamps.
4. Оставить процесс живым и проверить account/rateLimits/updated.
5. Перезапустить CLI/provider и сравнить snapshot с ChatGPT Usage page.
6. Реализовать CodexLocalProvider с reconnect, TTL, schema/version guard и redacted diagnostics.
7. Только после этого подключать данные к WPF overlay.

## Источники

- [Codex App Server README — auth/account API](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md)
- [OpenAI Help: Using Codex with your ChatGPT plan](https://help.openai.com/en/articles/11369540-using-codex-with-your-chatgpt-plan)
- [OpenAI Help: ChatGPT Work and Codex](https://help.openai.com/en/articles/20001275/)
- [Codex issue #21084: rate-limit data contract for an overlay](https://github.com/openai/codex/issues/21084)
- [Codex issue #32791: missing five-hour limit in Desktop](https://github.com/openai/codex/issues/32791)
- [Codex issue #23192: web/Desktop usage desynchronization](https://github.com/openai/codex/issues/23192)
- [Codex issue #20310: request for machine-readable usage/status](https://github.com/openai/codex/issues/20310)
- [Codex issue #15281: status output and reset-time gaps](https://github.com/openai/codex/issues/15281)
