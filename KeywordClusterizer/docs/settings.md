# Настройки `settings.json`

## `apiKey`

API-ключ DeepSeek для доступа к нейросети.

## `deepseek`

Настройки модели DeepSeek.

| Поле | По умолчанию | Описание |
|---|---|---|
| `model` | `deepseek-chat` | Базовая модель для шагов 1, 2, 4 (Draft / Mapping / Refinement) |
| `refactoringModel` | `deepseek-reasoner` | Модель для шага 3 (Refactoring). `deepseek-reasoner` даёт более глубокий аудит |
| `temperature` | `0.2` | Температура генерации: `0.0` = детерминированно, `1.0` = креативно |
| `maxTokens` | `100000` | Максимум токенов на ответ нейросети |
| `topP` | `1.0` | Top-p sampling (`1.0` = отключено) |

## `business`

Настройки бизнес-логики кластеризации.

| Поле | По умолчанию | Описание |
|---|---|---|
| `niche` | `сантехника` | Ниша сайта — подставляется в промпты для AI |
| `clusteringLogic` | `по интенту пользователя` | Логика группировки ключей |
| `granularityRule` | `кластеры от 2 до 60 ключей` | Правило гранулярности: формат `"от X до Y ключей"`. Парсится число `Y` как `maxClusterSize`. Используется в Phase 3 (рекурсивный split) как максимальный размер кластера |
| `chunkSize` | `100` | Размер чанка (только для старого AI-first пайплайна) |

## `serp`

Настройки SERP-кластеризации через XmlRiver (Yandex XML).

### SERP-first (новый пайплайн, `enableSerpFirst: true`)

| Поле | По умолчанию | Описание |
|---|---|---|
| `provider` | `xmlriver` | Провайдер поисковой выдачи |
| `xmlriverUser` | — | Логин XmlRiver (выдаётся при регистрации) |
| `xmlriverKey` | — | API-ключ XmlRiver |
| `enableSerpFirst` | `false` | Включить SERP-first кластеризацию (через граф интентов). Если `false` — используется старый AI-first пайплайн |
| `overlapThreshold` | `3` | Порог пересечения URL в топе выдачи (absolute count). Если у двух ключей совпадают ≥ `overlapThreshold` URL из Топ-10, они считаются одним интентом. Рекомендуется: 3-4 |
| `topResultsCount` | `10` | Сколько URL из топа поисковой выдачи брать для каждого ключа |
| `enableCache` | `true` | Кэшировать SERP-результаты в JSON-файл. При повторных запусках не тратит API-лимиты XmlRiver |
| `cachePath` | `serp_cache.json` | Путь к файлу кэша SERP-результатов (добавлен в `.gitignore`) |
| `maxRetries` | `3` | Сколько раз повторять запрос к XmlRiver при пустом ответе (транзиентные сбои) |
| `retryDelayMs` | `2000` | Задержка между повторами (в миллисекундах) |
| `maxConcurrency` | `10` | Максимум параллельных запросов к XmlRiver. XmlRiver позволяет до 10 потоков |

### AI-first (старый пайплайн, `enableSerpFirst: false`)

| Поле | По умолчанию | Описание |
|---|---|---|
| `enableValidation` | `false` | Включить SERP-валидацию кластеров (только старый режим) |
| `minOverlap` | `0.4` | Минимальный Jaccard overlap (0..1) для признания интента совпадающим |
| `sampleSize` | `3` | Сколько ключей из кластера опрашивать через XmlRiver |
| `enabledForDraft` | `false` | SERP-контекст для шага Draft (старый режим) |
| `enableFinalValidation` | `true` | Финальная проверка overlap (старый режим) |

## Пайплайн кластеризации

### SERP-first (новый, 5 фаз)

```
Phase 1:  SERP collection     → XmlRiver, параллельный сбор выдачи для ВСЕХ ключей (кэшируется)
Phase 2:  Graph clustering    → Connected Components (BFS), граф интентов на основе пересечения URL
Phase 2.5 Rescue Pass         → Прикрепление unclustered к ближайшему кластеру (≥1 общий URL)
Phase 3a: Recursive split     → Графовый split oversized с Hard Stop 6; орфаны → singleton-кластеры
Phase 3b: AI Semantic Split   → deepseek-chat для hard-stopped кластеров (threshold≥6 && count>maxSize)
Phase 4:  AI naming           → deepseek-reasoner, именование + чистка per-cluster (≤ maxClusterSize)
```

**Ключевые особенности:**
- Все 1200+ ключей обрабатываются сразу (без чанков по 100)
- Математически строгая кластеризация (пересечение URL = интент)
- **Hard Stop 6**: граф не поднимает порог выше 6 — железобетонный сигнал единого интента
- **Rescue Pass**: сироты с уникальной выдачей прикрепляются к ближайшему кластеру (даже 1 общий URL)
- **Orphan→Singleton**: узлы с 0 связей при пороге N становятся кластерами из 1 элемента
- **AI Semantic Split**: широкие интенты (threshold≥6, count>maxSize) разбиваются deepseek-chat по логике, не по SERP
- Каждый кластер ≤ `maxClusterSize` перед отправкой deepseek-reasoner
- 100% ключей сохраняются (потерянные AI → нераспределённые)

### AI-first (старый, 6 шагов)

```
Шаг 1: Draft          → AI, первичная структура из первого чанка
Шаг 2: Mapping        → AI, распределение остальных ключей чанками
Шаг 3: Refactoring    → AI, аудит + merge дубликатов + split oversized
Шаг 4: Refinement     → AI, до 5 итераций (split + merge)
Шаг 4.5: Sem. Merge   → AI, дедупликация кластеров с одинаковым интентом
Шаг 4.6: SERP Valid.  → XmlRiver, intra-cluster + cross-cluster проверка
```
