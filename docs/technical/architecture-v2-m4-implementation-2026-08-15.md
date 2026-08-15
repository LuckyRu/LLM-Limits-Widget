# Architecture v2: M4 Claude implementation status

Дата: 2026-08-15  
Статус: implemented parser slice, process/session cutover pending

## Реализовано

- [LLMLimitsWidget.Provider.Claude](../../src/LLMLimitsWidget.Provider.Claude/LLMLimitsWidget.Provider.Claude.csproj) — provider assembly с единственной зависимостью от Domain;
- `ClaudeStatusLineParser` для JSON status line с окнами `five_hour` и `seven_day`;
- `ClaudeUsageParser` для JSON-ответа прямого `/usage`, включая текстовые строки текущей сессии и недели;
- mapping `used_percentage -> RemainingPercent` с сохранением десятичной точности;
- mapping `session -> FiveHours`, `week -> SevenDays`;
- parsing reset timestamps из Unix epoch status line и локализованного текста `/usage` с timezone;
- `ProviderObservationEnvelope` с provenance, generation, sequence, effect id и временными метками;
- раздельные typed errors `ClaudeStatusLineError` и `ClaudeDirectError` для корректной маршрутизации retry и диагностики;
- fixture tests для status line, прямого `/usage`, decimal precision, source provenance и malformed JSON.

## Границы

Парсеры — чистый слой: они не запускают Claude CLI, не читают status line-файл, не знают о WPF, логировании, retry или lifecycle процесса. Два реальных транспорта будут подключены отдельными infrastructure adapters; выбор между ними и merge policy остаются ответственностью Application/Domain orchestration.

Парсер прямого `/usage` сохраняет timezone, если Windows может её разрешить, и безопасно использует UTC fallback для неизвестной зоны. Это не является разрешением хранить credentials: авторизация и запуск процесса должны оставаться в существующем пользовательском контексте Claude CLI.

## Проверка

```powershell
dotnet build .\tests\LLMLimitsWidget.Provider.Claude.Tests\LLMLimitsWidget.Provider.Claude.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Provider.Claude.Tests\LLMLimitsWidget.Provider.Claude.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Claude provider M4: all cases passed.
```

Полная регрессия M1–M4:

```text
Domain M1: all cases passed.
Architecture M1: domain boundary passed.
Application M2: all cases passed.
Codex provider M3: all cases passed.
Claude provider M4: all cases passed.
```
