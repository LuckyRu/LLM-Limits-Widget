# ADR 0004: Windows Widgets как платформенный baseline

Дата: 2026-08-12  
Статус: superseded by ADR 0005

## Решение

Рассматривать packaged Win32 Widget provider или PWA Widget provider как официальный путь только для варианта Windows Widgets; этот вариант не выбран для основного MVP.

## Что это означает для пользовательского ожидания

Желаемая строка рядом с taskbar переосмыслена как свободно размещаемое overlay-окно. Мы не выдаём его за нативный Windows Widget.

## Почему

Официальная документация Microsoft описывает Widgets host как Widgets Board, а не как поверхность для произвольного постоянно видимого inline-контента в taskbar. Для личного инструмента overlay даёт более прямое соответствие пользовательскому сценарию и меньше платформенных зависимостей.

Источники:

- [Windows Widgets overview](https://learn.microsoft.com/en-us/windows/apps/design/widgets/)
- [Widget providers](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers)
- [Widget provider manifest](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-provider-manifest)
