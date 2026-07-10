# Word-Level Weighted Soft Jaccard + IDF (Phase 3)

## Суть

Замена `EmbeddingGraphClusterizer` (Tanimoto на full-phrase эмбеддингах) на **Weighted Soft Jaccard + IDF на уровне слов**,
выполняемый **внутри каждого SERP-кластера**.

## Зачем

- Full-phrase Tanimoto (0.88) даёт ~20 синглтонов — слишком жёсткий порог для похожих фраз с разными глаголами
- IDF автоматически штрафует частые слова ("унитаз", "бачок") и усиливает редкие ("поплавок", "сифон")
- SERP уже гарантирует макросмысловую группу — можно безопасно резать по словам
- Weighted Jaccard не требует жёсткого удаления слов → нет пустых массивов

## Архитектура

```
SERP-кластер (Phase 2)
  │
  ├─ Токенизация фраз
  ├─ Удаление стоп-слов (хардкод + AI)
  ├─ Сбор уникальных слов кластера
  ├─ Получение word embeddings (OpenRouter batch)
  ├─ IDF weighting
  ├─ Weighted Soft Jaccard matrix
  └─ HAC → подкластеры
```

## Формулы

**IDF вес слова w в SERP-кластере:**
```
IDF(w) = ln(N / df(w)) + 0.1
```
где N — число фраз в кластере, df(w) — число фраз, содержащих w.

**Weighted Soft Jaccard:**
```
intersection = Σ IDF(w_a) * bestSim(w_a, w_b)  [для w_a, где bestSim >= threshold]
union = weight(A) + weight(B) - intersection

WeightedJaccard = intersection / union
```

**Word similarity (word embeddings):**
```
GetCosineSimilarity(wordA, wordB) = dot(embA, embB) / (|embA| * |embB|)
```

**Порог word similarity:** ~0.85 (чтобы ловить "поплавок" ≈ "поплавка", "поплавком")

## Изменения в файлах

### 1. Удалить
- [`Services/EmbeddingGraphClusterizer.cs`](KeywordClusterizer/Services/EmbeddingGraphClusterizer.cs) — full-phrase Tanimoto Graph больше не нужен
- [`Services/CentroidMergePass.cs`](KeywordClusterizer/Services/CentroidMergePass.cs) — Phase 3.5 не нужен, HAC уже даёт нужную гранулярность
- `Models/OpenRouterSettings.cs` — если используем только для эмбеддингов слов, можно объединить с DeepSeekSettings (опционально)

### 2. Создать
- **NEW** [`Services/WordLevelClusterizer.cs`](KeywordClusterizer/Services/WordLevelClusterizer.cs)
  - `tokenize(string phrase)` — разбивает на слова, нижний регистр
  - `removeStopWords(List<string> words)` — фильтр по хардкод-списку русских стоп-слов
  - `computeIDF(List<List<string>> tokenizedPhrases)` — словарь слово→IDF вес
  - `getWordEmbeddings(List<string> uniqueWords, OpenRouterEmbeddingClient)` — батч-запрос к OpenRouter
  - `calculateWeightedSoftJaccard(string[] wordsA, string[] wordsB, float[] embA, float[] embB, Dictionary<string,float> idf, float wordSimThreshold=0.85f)`
  - `clusterize(List<string> phrases, float hacThreshold=0.35f)` — полный метод

### 3. Изменить
- [`Models/BusinessSettings.cs`](KeywordClusterizer/Models/BusinessSettings.cs):
  ```
  // Удалить:
  public bool GraphClusteringEnabled { get; set; } = true;
  public float GraphThreshold { get; set; } = 0.88f;

  // Добавить:
  public bool WordLevelClusteringEnabled { get; set; } = true;
  public float JaccardThreshold { get; set; } = 0.25f;     // порог Weighted Jaccard для HAC
  public float WordSimThreshold { get; set; } = 0.85f;      // порог cosine similarity между словами
  public float HacThreshold { get; set; } = 0.35f;          // порог остановки HAC
  ```

- [`settings.json`](KeywordClusterizer/settings.json):
  ```json
  // Заменить block graphClustering:
  "wordLevelClustering": {
    "enabled": true,
    "jaccardThreshold": 0.25,
    "wordSimThreshold": 0.85,
    "hacThreshold": 0.35
  }
  ```

- [`Program.cs`](KeywordClusterizer/Program.cs):
  - Заменить чтение `graphClustering` → `wordLevelClustering`
  - Проверить, что `OpenRouterEmbeddingClient` используется по-новому

- [`ClusterizationPipeline.cs`](KeywordClusterizer/ClusterizationPipeline.cs):
  - Убрать CentroidMergePass (Phase 3.5)
  - Убрать `EmbeddingGraphClusterizer`
  - В Phase 3: для каждого SERP-кластера вызывать `WordLevelClusterizer.Clusterize()`
  - Упростить лог: `→ N → M подкластеров (word-level)`

### 4. Модифицировать
- [`Services/OpenRouterEmbeddingClient.cs`](KeywordClusterizer/Services/OpenRouterEmbeddingClient.cs) — уже готов к любым текстам (фразам или отдельным словам). Кэш автоматически сохранит word embeddings.

### 5. Стоп-слова (хардкод)
Создать статический список в `WordLevelClusterizer.cs`:
```csharp
private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
{
    "как", "в", "на", "для", "у", "и", "с", "от", "по", "из",
    "за", "к", "о", "об", "под", "над", "перед", "между",
    "что", "это", "его", "её", "их", "не", "ни", "или",
    "без", "до", "при", "через", "про", "со", "во", "же",
    "бы", "да", "нет", "все", "всё", "сам", "сама", "само",
    "мой", "твой", "наш", "ваш", "свой", "этот", "тот",
    "такой", "каждый", "любой", "весь", "один", "два",
    "чтобы", "если", "когда", "потому", "поэтому", "так",
    "ну", "вот", "вон", "там", "тут", "здесь", "тогда",
    "пока", "уже", "ещё", "еще", "только", "лишь", "даже",
    "ведь", "разве", "неужели", "ли", "будто", "словно",
    "именно", "както", "както", "тоесть", "также", "тоже"
};
```

## HAC (Hierarchical Agglomerative Clustering)

Простая реализация:
1. Каждая фраза — отдельный кластер
2. Вычислить Weighted Soft Jaccard для всех пар
3. Найти пару с max схожестью
4. Если max >= hacThreshold — склеить, пересчитать связи (complete linkage: min схожесть между элементами групп)
5. Повторять, пока max < hacThreshold

## Примечания

- Для SERP-кластеров ≤ 4 фраз — HAC не запускаем (пропускаем как есть)
- Для кластеров с 1 уникальным словом после стоп-фильтра — не запускаем HAC
- `embeddings_cache.json` теперь будет содержать как full-phrase эмбеддинги (если ещё нужны), так и word embeddings
- При удалении CentroidMergePass — удалить его вызов из ClusterizationPipeline.cs полностью
