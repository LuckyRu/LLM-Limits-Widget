# Architecture v2: M9 Presentation/ViewModel contract

Дата: 2026-08-15  
Статус: implemented pure ViewModel slice, WPF composition pending

## Реализовано

- отдельный `LLMLimitsWidget.Presentation` project с единственной зависимостью от Domain;
- `WidgetViewModel` с независимыми Codex/Claude rows;
- `ProviderRowViewModel` с provider health/freshness и двумя period slots;
- `LimitWindowViewModel` с compact decimal percent and reset timestamp;
- `CountdownTextFormatter` с адаптивной детализацией: seconds, minutes, `hr min`, days/hours, weeks/days;
- property notifications только при изменении отображаемого значения;
- отсутствие данных отображается пустым slot, а не фальшивыми placeholder values;
- unit tests на precision, compact countdown, missing data и отсутствие redundant notifications.

## Границы

ViewModel принимает immutable `AppState` и `nowUtc`, но не запускает таймер, process, watcher или WPF API. Реалтаймовый scheduler в UI composition должен вызывать `Apply` только при изменении текста countdown; XAML/View отвечает только за binding/layout/theme.

## Проверка

```powershell
dotnet build .\tests\LLMLimitsWidget.Presentation.Tests\LLMLimitsWidget.Presentation.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Presentation.Tests\LLMLimitsWidget.Presentation.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Presentation M9: all cases passed.
```
