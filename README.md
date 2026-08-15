# Floating LLM Limits Widget

Безрамочное плавающее окно для Windows 11, которое всегда может оставаться поверх других окон и показывает состояние лимитов персональных подписок ChatGPT и Claude.

## Цель проекта

Дать пользователю быстрый и понятный ответ на два вопроса перед началом интенсивной работы с AI:

1. Сколько доступного использования осталось у ChatGPT и Claude?
2. Когда соответствующий лимит сбросится?

Виджет должен экономить время, снижать неожиданное прерывание рабочих сессий и помогать выбирать, какой сервис использовать сейчас.

## Текущий статус

Проект находится на этапе продуктового исследования. Сначала проверяем пользовательскую боль, точность данных, способы авторизации и допустимый технический путь получения лимитов. Реализацию не начинаем до подтверждения жизнеспособности MVP.

## Документация

- [Индекс документации](docs/README.md)
- [Product brief](docs/product-brief.md)
- [Первичное продуктовое исследование](docs/research/2026-08-12-initial-research.md)
- [Техническая проверка MVP](docs/technical/feasibility-2026-08-12.md)
- [Получение лимитов Codex из desktop и CLI](docs/technical/codex-usage-acquisition-2026-08-12.md)
- [Получение лимитов Claude Pro из Desktop и Claude Code](docs/technical/claude-usage-acquisition-2026-08-12.md)
- [Выбор технологий](docs/decisions/0005-technology-selection.md)
- [ADR: источник лимитов Codex](docs/decisions/0006-codex-usage-source.md)
- [ADR: источник лимитов Claude Pro](docs/decisions/0007-claude-usage-source.md)
- [ADR: гибридный транспорт Claude Pro](docs/decisions/0008-claude-hybrid-usage-transport.md)
- [Decision log](docs/decisions/0001-project-scope.md)

## Запуск

Для этого репозитория виджет запускается только через [run-widget.ps1](run-widget.ps1). Скрипт останавливает дубликаты, собирает Release и запускает обычный Windows-процесс с трей-иконкой:

```powershell
.\run-widget.ps1
```

Для аварийного восстановления ghost-режима и позиции:

```powershell
.\run-widget.ps1 -NoGhost
```

`dotnet run` для запуска виджета не используется: он оставляет процесс под оболочкой `dotnet` и в ограниченном окружении может не иметь доступа к пользовательским настройкам, логам и трей-сессии.

## Рабочее название

Floating LLM Limits Widget. Название не финализировано.
