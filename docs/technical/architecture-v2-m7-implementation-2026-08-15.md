# Architecture v2: M7 Windows transport adapters

Дата: 2026-08-15  
Статус: implemented transport adapter slice, WPF composition pending

## Реализовано

- `WindowsHiddenProcessRunner` с `UseShellExecute=false`, `CreateNoWindow=true`, hidden window style, redirected stdout/stderr, timeout и kill child tree;
- typed `HiddenProcessException` для executable-not-found, start failure, timeout и unavailable output;
- `ClaudeDirectCliTransport`, который строит существующий `/usage` command line и преобразует результат pure parser-а в `AttemptOutcome`;
- `ClaudeStatusLineFileReader`, который читает snapshot без публикации в Store и передаёт source revision из `last-write ticks:length`;
- `CodexAppServerTransport`, который связывает Application runtime с pure Codex parser;
- `CodexAppServerSession` с persistent hidden `codex app-server --stdio`, initialize handshake, request serialization, response correlation, bounded session rotation и clean restart после protocol failure;
- fake transport/session tests на mapping успешных ответов, decimal precision, executable failure и source revision.

## Границы и безопасность

Infrastructure знает о Process/File API, но не о WPF, ViewModel и AppState reducer. Adapter не пишет сырые provider output в логи и не хранит credentials; используется уже авторизованный пользовательский контекст CLI. StatusLine reader возвращает результат наружу, а публикация выполняется только через `ObservationReceivedCommand` в single-writer Store.

`CreateNoWindow` предотвращает видимую консоль при direct refresh. Для Codex app-server stderr постоянно дренируется, чтобы diagnostic output не блокировал stdout protocol stream; содержимое stderr не попадает в пользовательский UI.

## Что остаётся

- composition root WPF должен создать concrete transports и `ProviderPipelineRuntime`;
- нужно подключить FileSystemWatcher/named-event signal к `ClaudeStatusLineFileReader` и dispatch `ObservationReceivedCommand`;
- нужно выполнить реальный пользовательский smoke с установленными Codex/Claude без вывода секретов;
- legacy `ProviderSupervisor` пока не удалён и остаётся rollback path.

## Проверка

```powershell
dotnet build .\tests\LLMLimitsWidget.Infrastructure.Windows.Tests\LLMLimitsWidget.Infrastructure.Windows.Tests.csproj -p:UseSharedCompilation=false
dotnet run --project .\tests\LLMLimitsWidget.Infrastructure.Windows.Tests\LLMLimitsWidget.Infrastructure.Windows.Tests.csproj --no-build
```

Ожидаемый результат:

```text
Infrastructure M7: all cases passed.
```
