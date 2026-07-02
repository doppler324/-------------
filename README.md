# Keyword Clusterizer

Кластеризатор ключевых запросов для SEO-продвижения сайтов. Использует SERP-first подход: сбор поисковой выдачи (XmlRiver) → граф интентов (Connected Components) → AI-тегизация oversized-кластеров (DeepSeek) → AI-именование.

## Требования

- **.NET 9 Runtime** (скачать: [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0))
- **API-ключи:**
  - [DeepSeek API](https://platform.deepseek.com/) — для AI-тегизации и именования
  - [XmlRiver](https://xmlriver.com/) — для сбора поисковой выдачи (Yandex XML)

## Быстрый старт

```cmd
git clone https://github.com/doppler324/------------- кластеризатор
cd кластеризатор\KeywordClusterizer
```

1. Вписать API-ключи в [`settings.json`](KeywordClusterizer/settings.json)
2. Положить ключевые запросы в `keywords.txt` (по одному на строку)
3. Запустить:

```cmd
dotnet run
```

Результат — `clusters.csv` (группа → ключ).

## Настройка

[`settings.json`](KeywordClusterizer/settings.json):

```json
{
  "apiKey": "sk-...",
  "deepseek": {
    "model": "deepseek-chat",
    "refactoringModel": "deepseek-reasoner",
    "temperature": 0.2,
    "maxTokens": 8192
  },
  "business": {
    "niche": "унитазы",
    "granularityRule": "кластеры от 2 до 60 ключей"
  },
  "serp": {
    "xmlriverUser": "ваш_логин",
    "xmlriverKey": "ваш_ключ",
    "overlapThreshold": 3,
    "maxConcurrency": 10,
    "enableCache": true
  }
}
```

### Параметры SERP

| Поле | По умолч. | Описание |
|------|-----------|----------|
| `overlapThreshold` | 3 | Минимум общих URL в топе, чтобы считать ключи одним интентом |
| `topResultsCount` | 10 | Сколько URL из топа выдачи брать для каждого ключа |
| `maxConcurrency` | 10 | Потоков параллельного опроса XmlRiver |
| `enableCache` | true | Кэшировать SERP в `serp_cache.json` (экономит API-лимиты) |

### Параметры бизнеса

| Поле | Описание |
|------|----------|
| `granularityRule` | Максимальный размер кластера. Парсится число после "до" (например, "до 60 ключей") |

## Архитектура пайплайна

```
Фаза 1: Сбор SERP (XmlRiver, 1270 ключей параллельно)
         ↓
Фаза 2: Граф интентов (Connected Components, overlap ≥ 3)
         ↓
Фаза 2.5: Rescue Pass (прикрепление сирот к ближайшим кластерам)
         ↓
Фаза 3: Semantic Map-Reduce (только для кластеров > maxClusterSize)
         └─ Chunking по 100 ключей
         └─ Sequential AI-тегизация (deepseek-chat) с передачей контекста
         └─ GroupBy по тегу → логические подкластеры
         ↓
Фаза 4: AI-именование per-cluster (deepseek-reasoner)
         └─ Чистка ключей, определение page_type
         ↓
      Результат: clusters.csv
```

## Перенос на другой компьютер

1. Установить .NET 9 Runtime
2. Скопировать папку `KeywordClusterizer/` + `кластеризатор.sln`
3. Настроить `settings.json` (API-ключи)
4. Загрузить `keywords.txt`
5. `dotnet run`

### Single-file .exe (без установки .NET)

```cmd
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Готовый `KeywordClusterizer.exe` (~71 МБ) лежит в папке `publish/`. На другом компьютере достаточно скопировать:
- `publish/KeywordClusterizer.exe`
- `settings.json` (с API-ключами)
- `keywords.txt` (список запросов)
- `instructions/` (папка с промптами)

Запуск:
```cmd
KeywordClusterizer.exe
```
