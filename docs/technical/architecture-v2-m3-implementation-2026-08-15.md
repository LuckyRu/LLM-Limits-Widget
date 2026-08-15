# Architecture v2: M3 Codex implementation status

Дата: 2026-08-15  
Статус: implemented parser slice, process/session cutover pending

## Реализовано

- [LLMLimitsWidget.Provider.Codex](../../src/LLMLimitsWidget.Provider.Codex/LLMLimitsWidget.Provider.Codex.csproj) — чистая provider assembly с единственной зависимостью от Domain;
- parser текущего app-server JSON shape (`rateLimitsByLimitId.codex` и fallback `rateLimits`);
- mapping `usedPercent -> RemainingPercent` с decimal precision;
- mapping supported windows `300 -> FiveHours`, `10080 -> SevenDays`;
- `ProviderObservationEnvelope` с generation, sequence, effect id, captured/received timestamps;
- typed errors для malformed JSON, schema mismatch, unsupported windows, invalid reset и login/process failures;
- fixture tests для success, decimal precision, protocol metadata, malformed payload и unsupported duration.

## Границы

Parser не запускает `codex`, не читает файлы, не знает про WPF, logging или app-server process lifetime. Существующий hidden app-server transport пока остаётся в старом WPF-проекте и будет обёрнут compatibility adapter в следующем срезе.

## Проверка

```powershell
dotnet build .\tests\LLMLimitsWidget.Provider.Codex.Tests\LLMLimitsWidget.Provider.Codex.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Provider.Codex.Tests\LLMLimitsWidget.Provider.Codex.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Codex provider M3: all cases passed.
```
