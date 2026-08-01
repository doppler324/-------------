# Настройки `settings.json`

## `apiKey`

API-ключ DeepSeek.

## `deepseek`

Настройки базовой модели.

| Поле | По умолчанию | Описание |
|---|---|---|
| `model` | `deepseek-chat` | Модель для Phase 4 и других AI-запросов |
| `refactoringModel` | `deepseek-chat` | Модель для рефакторинга (не используется в текущем пайплайне) |
| `temperature` | `0.2` | Температура генерации |
| `maxTokens` | `100000` | Максимум токенов на ответ |
| `topP` | `1.0` | Top-p sampling |
| `enableThinking` | `true` | Включить Chain-of-Thought (DeepSeek reasoning) |
| `reasoningEffort` | `low` | Уровень reasoning: `low`, `medium`, `high` |
| `stream` | `false` | Потоковый режим |

## `business`

Бизнес-настройки кластеризации.

| Поле | По умолчанию | Описание |
|---|---|---|
| `niche` | `сантехника` | Ниша сайта — подставляется в промпты |
| `clusteringLogic` | `по интенту пользователя` | Логика группировки ключей |
| `granularityRule` | `кластеры от 2 до 60 ключей` | Правило гранулярности. Парсится `Y` из `"от X до Y ключей"` |
| `skipNaming` | `false` | Пропустить AI-именование (Phase 4). `true` — тех. имена "Кластер N" |
| `skipMerge` | `false` | `true` — только naming: AI придумывает H1-заголовки, не меняя состав кластеров |
| `skipPhase4` | `false` | Полностью пропустить Phase 4 |
| `suppressClusterDisplay` | `false` | Не выводить список кластеров в консоль (для больших наборов) |
| | | |
| **Sentence-Level Clustering (Phase 3)** | | |
| `sentenceLevelClustering.enabled` | `true` | Включить sentence-level кластеризацию внутри SERP-кластеров (эмбеддинги + cosine + HAC) |
| `sentenceLevelClustering.sentenceHacThreshold` | `0.82` | Порог cosine similarity для остановки HAC. Выше = мельче кластеры |
| | | |
| **Macro Merge (Phase 3.5)** | | |
| `macroMerge.enabled` | `true` | Включить объединение микро-кластеров в макро-бакеты |
| `macroMerge.representativeMode` | `centroid` | Как вычислять representative-вектор: `"centroid"` (L2-нормализованный средний вектор всех фраз) или `"medoid"` (реальная фраза, ближайшая ко всем остальным) |
| `macroMerge.similarityThreshold` | `0.77` | Порог cosine similarity между representative-векторами для слияния. Рекомендуется `sentenceHacThreshold - 0.05` |
| | | |
| **Rescue Pass V2 (Phase 3.6)** | | |
| `rescuePassV2.enabled` | `true` | Прикрепление сирот (одиночек + unclustered) к ближайшему ядру |
| `rescuePassV2.rescueThreshold` | `0.78` | Порог cosine similarity для прикрепления сироты к ядру |

## `serp`

Настройки SERP-кластеризации через XmlRiver.

| Поле | По умолчанию | Описание |
|---|---|---|
| `provider` | `xmlriver` | Провайдер поисковой выдачи |
| `xmlriverUser` | — | Логин XmlRiver |
| `xmlriverKey` | — | API-ключ XmlRiver |
| `overlapThreshold` | `4` | Порог пересечения URL для графа интентов |
| `topResultsCount` | `10` | Сколько URL из топа выдачи брать для каждого ключа |
| `enableCache` | `true` | Кэшировать SERP-результаты в JSON |
| `cachePath` | `serp_cache.json` | Путь к файлу кэша SERP |
| `maxRetries` | `3` | Повторы при пустом ответе XmlRiver |
| `retryDelayMs` | `2000` | Задержка между повторами (мс) |
| `maxConcurrency` | `10` | Максимум параллельных запросов к XmlRiver |
| `enableSerpFirst` | `true` | Всегда `true` (SERP-first пайплайн) |

## `openrouter`

Настройки OpenRouter для эмбеддингов и Phase 4.

| Поле | По умолчанию | Описание |
|---|---|---|
| `apiKey` | — | API-ключ OpenRouter (отдельный от DeepSeek) |
| `embeddingModel` | `text-embedding-3-small` | Модель для sentence embeddings (Phase 3) |
| `embeddingDimensions` | `1536` | Размерность эмбеддингов (4096 для `qwen/qwen3-embedding-8b`) |
| `cachePath` | `embeddings_cache.json` | Путь к кэшу эмбеддингов |
| `batchSize` | `64` | Сколько фраз отправлять за один запрос к API эмбеддингов |
| `maxConcurrency` | `10` | Сколько потоков параллельно запрашивают эмбеддинги |

## `phase4`

Настройки Phase 4: AI Merge + Naming.

| Поле | По умолчанию | Описание |
|---|---|---|
| `provider` | `deepseek` | Провайдер: `"deepseek"` (прямое API) или `"openrouter"` (любая модель) |
| `model` | `""` | Модель (если пустая — используется `deepseek.model`). Для OpenRouter: `"anthropic/claude-3.5-sonnet"`, `"openai/gpt-4o-mini"` и т.д. |
| `temperature` | `0.2` | Температура Phase 4 |
| `maxTokens` | `100000` | Максимум токенов для Phase 4 |

## Пайплайн кластеризации (текущий)

```
Phase 1:   SERP collection        → XmlRiver, параллельный сбор выдачи для ВСЕХ ключей (кэшируется)
Phase 2:   Graph clustering       → Connected Components (BFS), граф интентов на основе пересечения URL
Phase 2.5  Rescue Pass            → Прикрепление unclustered к ближайшему кластеру (≥1 общий URL)
Phase 3:   Sentence-level         → sentence embeddings (OpenRouter) + cosine similarity + HAC внутри SERP-кластеров
Phase 3.5  Macro Merge            → Greedy merge микро-кластеров в макро-бакеты (representativeMode)
Phase 3.6  Rescue Pass V2         → Nearest Centroid: прикрепление сирот к ядрам + pairwise merge
Phase 4:   AI Merge + Naming      → skipMerge=true: AI придумывает только H1-заголовки, состав не меняется
```

## Удалённые/устаревшие настройки

Следующие поля больше не используются и удалены из `settings.json`:

| Поле | Причина удаления |
|---|---|
| `business.chunkSize` | AI-first пайплайн удалён |
| `business.mergeMode` | Phase 4.5 (Centroid/AI Merge) удалён |
| `business.mergeThreshold` | Centroid Merge удалён |
| `business.centroidMergeEnabled` | Centroid Merge удалён |
| `business.wordSplit.*` | Заменён на `wordLevelClustering.*` |
| `serp.*` (AI-first блок) | AI-first пайплайн удалён |
