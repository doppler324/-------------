# План: Centroid Merge Pass (математическое слияние кластеров)

## Цель

Добавить математический Merge Pass (Cosine Similarity центроидов) как альтернативу AI-слиянию. Выбор метода через settings.json.

## Конфигурация

В `settings.json` секция `business`:

```json
{
  "mergeMode": "centroid",   // "off" | "ai" | "centroid"
  "mergeThreshold": 0.88     // порог для centroid-режима (0.0-1.0)
}
```

| mergeMode | Что делает |
|-----------|-----------|
| `"off"` | Пропустить Phase 4.5 |
| `"ai"` | DeepSeek Merge Pass (существующий, с thinking:high) |
| `"centroid"` | Cosine Similarity центроидов в C# (новый) |

## Файлы

### 1. `Services/CentroidMergePass.cs` — НОВЫЙ

Методы:
- `ComputeCentroid(List<string> keywords, Dictionary<string, float[]> embeddings)` → средний вектор
- `CosineSimilarity(float[] a, float[] b)` → float (0..1)
- `Merge(List<List<string>> clusters, Dictionary<string, float[]> embeddings, float threshold, int maxIterations = 5)` → `List<List<string>>`

### 2. `Models/BusinessSettings.cs` — добавить

```csharp
public string MergeMode { get; set; } = "centroid";  // "off" | "ai" | "centroid"
public float MergeThreshold { get; set; } = 0.88f;
```

### 3. `settings.json` — добавить

```json
"mergeMode": "centroid",
"mergeThreshold": 0.88
```

### 4. `Program.cs` — парсинг новых полей

### 5. `ClusterizationPipeline.cs` — переписать Phase 4.5

```csharp
switch (_businessSettings.MergeMode)
{
    case "ai":     await AiMergePass(namedClusters); break;
    case "centroid": await CentroidMergePass(finalClusters, embeddings); break;
    case "off":    // пропустить
}
```
