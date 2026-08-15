# Architecture v2: M2 implementation status

Дата: 2026-08-15  
Статус: implemented as an isolated runtime slice, not connected to WPF/provider transports

## Реализовано

- [AppStore](../../src/LLMLimitsWidget.Application/AppStore.cs) с единственным reducer consumer и двумя bounded lanes: priority и ordinary;
- immutable state publication через `StateChanged`;
- idempotent/no-op command handling без лишнего увеличения revision;
- [ProviderPipelineRuntime](../../src/LLMLimitsWidget.Application/ProviderPipelineRuntime.cs) на один provider;
- раздельные priority/control и ordinary effect lanes;
- tracked provider attempt task с отдельным cancellation token;
- stop/suspend-priority behavior без ожидания завершения обычной очереди;
- runtime completion dispatch обратно в Store;
- внедряемый `TimeProvider` для wake scheduling;
- application ports для provider transport, runtime и effect execution.

## Инварианты M2

- Store — единственный владелец `AppState`;
- один runtime — один provider;
- runtime actor не await-ит provider IO внутри mailbox loop;
- одновременно выполняется не более одного attempt на runtime;
- completion приходит в Store как команда и не меняет state напрямую;
- старый WPF executable и старые provider transports не подключены к новому графу.

## Проверка

```powershell
dotnet build .\src\LLMLimitsWidget.Application\LLMLimitsWidget.Application.csproj -p:UseSharedCompilation=false
dotnet build .\tests\LLMLimitsWidget.Application.Tests\LLMLimitsWidget.Application.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Application.Tests\LLMLimitsWidget.Application.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Application M2: all cases passed.
```

Следующий срез — compatibility adapters для существующих Codex/Claude data sources. До него новый runtime не должен запускать реальные CLI-процессы.
