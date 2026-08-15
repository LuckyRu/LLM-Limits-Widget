# Architecture v2: M5 observation ordering and merge policy

Дата: 2026-08-15  
Статус: implemented domain policy slice

## Реализовано

- `ObservationMergePolicy` вынесен в публичный Domain API для тестируемого composition boundary;
- проверка provider identity и required windows для complete observation;
- проверка `ResetAtUtc` относительно `CapturedAtUtc`;
- per-window merge: partial observation не перезаписывает отсутствующие окна и принимает только более новые candidates;
- ordering по `CapturedAtUtc`, а не по `ReceivedAtUtc`;
- сравнение `SourceRevision` при одинаковом времени захвата, с числовым сравнением для монотонных ревизий и ordinal fallback;
- provider-specific tie-breaker для Claude: validated direct CLI выше statusLine только при равных времени и source revision;
- старый источник не получает преимущество только из-за transport priority;
- тесты для равного времени direct/statusLine и rejection старого statusLine после direct.

## Что ещё не подключено

Политика уже используется reducer при обработке `AttemptCompletedCommand`, но отдельный ingress-командный путь для push statusLine и полноценный Claude source selector пока не подключены к Windows runtime. Это следующий infrastructure/application срез: signal ingress должен передавать observation в Store, а scheduler — запускать direct fallback только при missing/stale statusLine или по manual refresh.

## Проверка

```powershell
dotnet run --project .\tests\LLMLimitsWidget.Domain.Tests\LLMLimitsWidget.Domain.Tests.csproj -p:UseSharedCompilation=false
```

Ожидаемый результат:

```text
Domain M1/M5: all cases passed.
```

Полная регрессия M1–M4 также зелёная:

```text
Architecture M1: domain boundary passed.
Application M2: all cases passed.
Codex provider M3: all cases passed.
Claude provider M4: all cases passed.
```
