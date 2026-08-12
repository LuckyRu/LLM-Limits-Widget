# Получение лимитов Claude Pro из Desktop и Claude Code

Дата: 2026-08-12  
Статус: техническое исследование  
Scope: личный Claude Pro, Windows 11, Claude Desktop и Claude Code.

## Вывод

Для Claude найден рабочий локальный канал без Anthropic API: Claude Code передаёт JSON в пользовательский statusLine command через stdin.

Локальная проверка Windows показала, что Claude Desktop и Claude Code процессы присутствуют, но команда claude не зарегистрирована в PATH. Один из обнаруженных Claude Code процессов указывает на версию 2.1.227, однако его executable уже недоступен по указанному пути, поэтому запуск CLI из исследовательской среды не выполнялся. Переменная ANTHROPIC_API_KEY отсутствует.

### Реальный smoke test CLI

Актуальный launcher найден в package cache:

~~~text
C:/Users/Leshc/AppData/Local/Packages/Claude_pzs8sxrjxfjjc/LocalCache/Roaming/Claude/claude-code/2.1.227/claude.exe
~~~

Запуск с параметром --version успешен: Claude Code 2.1.227. Команда auth status вернула:

~~~json
{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}
~~~

doctor подтвердил: Claude Code не авторизован в claude.ai, subscription auth не активен. Поэтому statusline rate-limit JSON нельзя получить в текущей среде: для этого нужен первый API-ответ авторизованной Claude Code сессии. Claude Desktop установлен отдельно и его login state автоматически не переиспользуется CLI.

### Реальный smoke test после авторизации

После входа через Claude subscription CLI вернул:

~~~json
{
  "loggedIn": true,
  "authMethod": "claude.ai",
  "apiProvider": "firstParty",
  "subscriptionType": "pro"
}
~~~

Команда usage была запущена в non-interactive режиме без tools, без рабочей сессии и без model turn. Реальный результат:

~~~text
Current session: 95% used; resets Aug 13, 12:30am (Europe/Bucharest)
Current week (all models): 30% used; resets Aug 17, 5pm (Europe/Bucharest)
~~~

Следовательно, на момент теста:

- 5-hour remaining: 5%;
- weekly remaining: 70%;
- session reset: 2026-08-13 00:30 Europe/Bucharest;
- weekly reset: 2026-08-17 17:00 Europe/Bucharest;
- model/API cost: 0; model turns: 0.

Это подтверждает, что авторизованный Claude Code CLI реально видит лимиты Pro. Однако этот запуск не вызвал statusLine command: документация и headless-поведение CLI разделяют интерактивный statusline и print/SDK режим. Поэтому отдельный statusline bridge всё ещё нужно проверять внутри интерактивной trusted-сессии после пользовательского подтверждения рабочей папки.

Официально документированные поля:

- rate_limits.five_hour.used_percentage;
- rate_limits.five_hour.resets_at;
- rate_limits.seven_day.used_percentage;
- rate_limits.seven_day.resets_at.

Поле rate_limits появляется для подписчиков Claude.ai Pro/Max после первого API-ответа Claude Code. Значения показывают расход общего плана Claude/Claude Code, а не API billing.

Принято гибридное решение: statusline bridge используется как быстрый push-like канал, а прямой запуск авторизованного CLI с /usage — как fallback при stale snapshot, на старте и по ручному обновлению. Statusline не обязан быть единственным транспортом.

## Что подтверждено официально

Claude Pro имеет:

- rolling session limit на 5 часов;
- weekly limit для всех моделей;
- фиксированное для аккаунта weekly reset time;
- общий usage между Claude web/desktop/mobile и Claude Code.

Claude Code предоставляет:

- /usage — интерактивный просмотр plan limits и activity;
- /status — account/status interface;
- custom statusLine, которому передаётся JSON;
- Desktop usage ring рядом с model picker.

Anthropic отдельно предупреждает, что ANTHROPIC_API_KEY переключает Claude Code на API billing вместо включённого usage Pro/Max. Для нашего провайдера эта переменная не нужна и не должна использоваться.

## Каналы и приоритет

| Канал | Что получаем | Плюсы | Риски | Решение |
|---|---|---|---|---|
| Claude Code statusLine stdin JSON | 5h/7d used percentage и reset timestamps | официально описан, локально, структурированный JSON, без API key/cookies | данные появляются после первого API-ответа; bridge зависит от активной сессии; statusline может быть отключён или не вызван | основной кандидат для MVP spike |
| Claude Code /usage | интерактивные plan limits и reset | официальный пользовательский экран | UI/TTY, нет стабильного внешнего JSON; виджет не получает данные в фоне | ручная сверка и fallback |
| Claude Desktop usage ring | plan usage period и context usage | официальный UI Windows Desktop | стабильного внешнего IPC/API не найдено; чтение DOM/локального storage хрупко | ручная сверка, исследовать после bridge |
| Claude API rate-limit headers | API limits/remaining headers | официальный API для API billing | не лимиты Claude Pro; OAuth subscription token не является Messages API key | не использовать |
| undocumented /api/oauth/usage | по сообщениям сообщества может отдавать subscription windows | прямой JSON-кандидат | undocumented, auth/session details, reported 429, breaking changes | не использовать в MVP |
| локальные JSONL transcripts | история токенов/сессий и приблизительный расход | offline, без сети | не отражает subscription quota; вместимости и weighting неизвестны | только аналитика, не источник лимитов |

## StatusLine: минимальный контракт

Claude Code запускает настроенную команду локально и передаёт ей JSON через stdin. На Windows команда выполняется через Git Bash, если он установлен, либо через PowerShell.

Пример ожидаемого фрагмента:

~~~json
{
  "version": "2.1.90",
  "rate_limits": {
    "five_hour": {
      "used_percentage": 23.5,
      "resets_at": 1738425600
    },
    "seven_day": {
      "used_percentage": 41.2,
      "resets_at": 1738857600
    }
  }
}
~~~

Интерпретация:

- remaining = clamp(100 - used_percentage, 0, 100);
- resets_at — Unix epoch seconds;
- five_hour — rolling 5-hour window;
- seven_day — weekly window;
- отсутствие rate_limits — unavailable, а не 0%;
- null или отсутствие отдельного окна — частичный snapshot.

Пример конфигурации Windows из официальной документации:

~~~json
{
  "statusLine": {
    "type": "command",
    "command": "powershell -NoProfile -File C:/Users/username/.claude/statusline.ps1",
    "refreshInterval": 60
  }
}
~~~

Для проекта bridge должен:

1. прочитать весь stdin;
2. распарсить JSON;
3. взять только version и rate_limits;
4. добавить observed_at, source, freshness;
5. атомарно заменить snapshot-файл;
6. не сохранять prompt, transcript path, cwd, model, email, tokens и другие поля;
7. завершиться с кодом 0 даже при отсутствии rate_limits, чтобы не ломать Claude Code UI.

refreshInterval обновляет statusline-команду только в рамках активной Claude Code сессии. Он не запускает bridge независимо от Claude Code и не является автономным poller.

## Критическое ограничение: канал push-on-session

В отличие от Codex App Server, statusLine bridge не может сам запросить текущие лимиты по команде. Данные появляются:

- после API-ответа;
- когда Claude Code перерисовывает statusline;
- только пока существует активная Claude Code сессия с этой конфигурацией.

Поэтому режимы виджета:

- fresh — snapshot обновлён в пределах TTL;
- stale — есть последнее значение, но Claude Code давно не запускал statusline;
- unavailable — snapshot отсутствует, rate_limits ещё не пришёл или окно не предоставлено;
- error — повреждён snapshot/bridge.

Если нужен показатель после перезапуска Windows без активного Claude Code, официальный локальный push-канал недостаточен. Тогда остаётся ручное открытие /usage, browser UI с пользовательским действием или исследование undocumented endpoint, что не принимаем как базовую архитектуру.

Принятое решение закрывает это ограничение прямым вызовом /usage:

~~~text
claude.exe -p /usage --output-format json --tools "" --no-session-persistence --setting-sources user --permission-mode plan
~~~

Запуск не выполняет модельный turn и не требует доступа к рабочим файлам. Provider разбирает поле result, сохраняет только rate-limit snapshot и прекращает процесс после ответа.

Базовые интервалы:

- один direct refresh при старте виджета;
- fallback polling каждые 3–5 минут при активной Claude Code CLI-сессии после истечения TTL;
- polling каждые 15–30 минут при неактивной сессии;
- немедленный direct refresh по ручной команде.

При конфликте statusline и /usage более свежий direct snapshot считается authoritative. Оба источника и observed_at должны быть видны в диагностике.

## Desktop и CLI

### Claude Code CLI

CLI — лучший источник для первого автоматического spike. Он уже получает subscription rate-limit state и отдаёт его в statusline JSON. Нам не нужно запускать запрос с -p и не нужно тратить лимит: достаточно подключить bridge к обычной интерактивной сессии пользователя.

Команды /usage и /status оставляем для ручной проверки и диагностики. Парсить их текст как основной канал не нужно.

### Claude Desktop

Официальная документация Windows Desktop подтверждает usage ring рядом с выбором модели и общий plan usage между Claude Code surfaces. Внешний стабильный IPC/API для чтения этого ring не найден. Не подключаемся к Electron storage, локальным базам, случайным портам или DOM без отдельного security/reliability review.

Практическая стратегия:

1. Первый provider использует Claude Code statusLine bridge.
2. Desktop используется пользователем для ручной сверки.
3. После working bridge исследовать, выдаёт ли Desktop тот же JSON/state через поддержанный локальный канал.
4. Если нет, не делать Desktop отдельным автоматическим provider’ом.

## Надёжность и наблюдения сообщества

Официальная документация говорит, что поле появляется только после первого API-ответа и может отсутствовать до него. В GitHub issues также описаны случаи, когда rate_limits исчезал из statusline JSON или лимиты расходились с /usage. Поэтому bridge и UI должны проверять свежесть, версию CLI и наличие каждого окна.

Сообщество также сообщало о:

- расхождениях между /usage и фактическим блокированием;
- задержке после изменения плана;
- нестабильности расхода лимита между версиями Claude Code;
- невозможности получить subscription usage через headless action output.

Эти сообщения не заменяют официальную документацию, но подтверждают необходимость stale/unavailable и ручной сверки.

## Безопасность

Для MVP:

- не читать %USERPROFILE%/.claude/.credentials.json и другие credential-файлы;
- не извлекать cookies Claude Desktop/браузера;
- не использовать OAuth token как bearer для собственного HTTP-клиента;
- не использовать ANTHROPIC_API_KEY;
- statusline bridge сохраняет только rate-limit snapshot;
- файл snapshot доступен только текущему пользователю;
- при ошибке не писать stdin и stderr в лог целиком;
- не запускать Claude Code -p только ради проверки лимитов.

## Следующий spike

1. Реализовать прямой provider для /usage с timeout, parsing result и redacted diagnostics.
2. Реализовать statusline bridge с Named Pipe транспортом.
3. Добавить единый snapshot cache, TTL и source priority.
4. Проверить, что отсутствие rate_limits не ломает Claude Code.
5. Сверить оба источника с Claude Settings > Usage и Desktop usage ring.

## Источники

- [Claude Code status line — rate limits and JSON fields](https://code.claude.com/docs/en/statusline)
- [Claude Code commands — /usage and /status](https://code.claude.com/docs/en/commands)
- [Claude Code error reference — usage limits](https://code.claude.com/docs/en/errors)
- [Claude Code Desktop — usage ring](https://code.claude.com/docs/en/desktop)
- [Anthropic Help: Use Claude Code with your Pro or Max plan](https://support.claude.com/en/articles/11145838-use-claude-code-with-your-pro-or-max-plan)
- [Anthropic Help: What is the Pro plan?](https://support.claude.com/en/articles/8325606-what-is-the-pro-plan)
- [Claude Code issue #1518: subscription rate-limit state not exposed in action output](https://github.com/anthropics/claude-code-action/issues/1518)
- [Claude Code issue #40094: rate_limits missing from statusLine](https://github.com/anthropics/claude-code/issues/40094)
- [Claude Code issue #38335: rate-limit instability reports](https://github.com/anthropics/claude-code/issues/38335)
