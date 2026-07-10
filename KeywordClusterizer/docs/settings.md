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
| `skipNaming` | `false` | Пропустить Phase 4 (AI Merge+Naming). `true` — тех. имена |
| | | |
| **Word-Level Clustering** | | |
| `wordLevelClustering.enabled` | `true` | Включить Phase 3 (word-level кластеризацию внутри SERP-кластеров) |
| `wordLevelClustering.wordSimThreshold` | `0.85` | Порог cosine similarity между word embeddings (0.0-1.0). Выше = строже к морфологии |
| `wordLevelClustering.hacThreshold` | `0.35` | Порог Weighted Jaccard для остановки HAC (0.0-1.0). Ниже = мельче кластеры |

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
| `embeddingModel` | `openai/text-embedding-3-large` | Модель для word embeddings (Phase 3) |
| `embeddingDimensions` | `3072` | Размерность эмбеддингов |
| `cachePath` | `embeddings_cache.json` | Путь к кэшу эмбеддингов |

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
Phase 1:  SERP collection        → XmlRiver, параллельный сбор выдачи для ВСЕХ ключей (кэшируется)
Phase 2:  Graph clustering       → Connected Components (BFS), граф интентов на основе пересечения URL
Phase 2.5 Rescue Pass            → Прикрепление unclustered к ближайшему кластеру (≥1 общий URL)
Phase 3:  Word-level clustering  → IDF + Weighted Soft Jaccard (word embeddings) + HAC
Phase 4:  AI Merge + Naming      → Единый DeepSeek/OpenRouter call → SeoArticleResponse
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
