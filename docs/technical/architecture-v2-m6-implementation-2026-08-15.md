# Architecture v2: M6 push observation ingress

Дата: 2026-08-15  
Статус: implemented domain ingress slice

## Реализовано

- добавлена `ObservationReceivedCommand` для push/signal transport-ов;
- statusLine observation проходит через тот же single-writer `AppReducer`, что и direct completion;
- push не создаёт новый process attempt и не блокирует активный direct attempt;
- единая `ObservationMergePolicy` применяется к push и attempt completion;
- ошибки push обновляют только соответствующий `TransportState`, не ломая Claude direct и Codex;
- успешный push обновляет LKG и создаёт обычный `SaveProviderCacheEffect`;
- тесты покрывают ingress statusLine и изоляцию ошибочного statusLine transport.

## Границы

Команда намеренно не содержит Windows event/file handles и не знает, откуда пришёл сигнал. `Infrastructure.Windows`/adapter отвечает за чтение statusLine и преобразование snapshot в `ProviderObservationEnvelope`; Store отвечает за последовательную публикацию и merge. Scheduler и bounded direct fallback остаются следующим application/infrastructure срезом.

## Проверка

```powershell
dotnet run --project .\tests\LLMLimitsWidget.Domain.Tests\LLMLimitsWidget.Domain.Tests.csproj -p:UseSharedCompilation=false
```

Ожидаемый результат:

```text
Domain M1/M5/M6: all cases passed.
```
