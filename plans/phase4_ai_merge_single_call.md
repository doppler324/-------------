# Phase 4: AI Merge + Naming (single DeepSeek call)

## Текущее состояние

Phase 4 сейчас:
- **skipNaming=true**: технические имена "Кластер 1", "Кластер 2" — без API
- **skipNaming=false**: 
  1. Для каждого кластера → `RefineClusterAsync()` (per-cluster DeepSeek call → `RefinedCluster`)
  2. Затем `MergeClustersAsync()` (Phase 4.5 — ещё один DeepSeek call для схлопывания синонимов)
  = **N+1 API вызовов**

## Цель

Заменить на **единый DeepSeek call** со всеми кластерами сразу:
- 1 API вызов вместо N+1
- AI сам решает: какие кластеры склеить, какие общие запросы извлечь в обзорную статью
- Ответ строго в JSON: `{ seo_articles: [...] }`

## Что меняем

### 1. NEW: [`Models/SeoArticleResponse.cs`](KeywordClusterizer/Models/SeoArticleResponse.cs)
```csharp
public class SeoArticleResponse
{
    [JsonPropertyName("seo_articles")]
    public List<SeoArticle> SeoArticles { get; set; } = new();
}

public class SeoArticle
{
    [JsonPropertyName("h1_title")]
    public string H1Title { get; set; } = "";

    [JsonPropertyName("source_clusters")]
    public List<string> SourceClusters { get; set; } = new();

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();
}
```

### 2. NEW: [`instructions/phase4_ai_merge.txt`](KeywordClusterizer/instructions/phase4_ai_merge.txt)

Промпт на основе сообщения пользователя:

```
Ты — строгий алгоритм-парсер и ведущий SEO-специалист. 
Я передаю тебе список узких семантических кластеров. 
Твоя задача — проанализировать их и вернуть ответ СТРОГО в формате валидного JSON. 
Никакого текста до или после JSON.

ПРАВИЛА ЛОГИКИ:
1. Изоляция узлов: Запрещено объединять разные технические детали в одну статью.
2. Точечное слияние синонимов: Объединяй кластеры целиком, если они описывают один и тот же процесс.
3. ИСКЛЮЧЕНИЕ ДЛЯ ОБЩИХ ЗАПРОСОВ: Если в узком техническом кластере застрял широкий базовый запрос — вытащи его в отдельную обзорную статью.
4. Неприкосновенность данных: Каждое ключевое слово из исходника должно попасть в финальный JSON ровно в том виде, в котором оно написано.

ФОРМАТ ОТВЕТА (строгий JSON, без markdown-обёртки):
{
  "seo_articles": [
    {
      "h1_title": "Название статьи (H1)",
      "source_clusters": ["Кластер 1", "Извлечено из Кластер 2"],
      "keywords": ["ключ 1", "ключ 2"]
    }
  ]
}
```

### 3. MODIFY: [`ClusterizationPipeline.cs`](KeywordClusterizer/ClusterizationPipeline.cs)

**Заменить блок Phase 4** (текущие строки ~194-268):

```csharp
// ==========================================
// Фаза 4: AI Merge + Naming (единый DeepSeek call)
// ==========================================
var namedClusters = new Dictionary<string, List<string>>();
var allUnclustered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var key in serpUnclustered)
    allUnclustered.Add(key);

if (_businessSettings.SkipNaming)
{
    // Быстрый режим: кластеры как есть, без API
    Console.WriteLine($"\n--- Фаза 4: Пропуск AI-обработки (skipNaming=true) ---");
    int idx = 0;
    foreach (var cluster in finalClusters)
    {
        idx++;
        string name = cluster.Count > 1 ? $"Кластер {idx}" : cluster[0];
        namedClusters[name] = cluster;
    }
}
else
{
    Console.WriteLine($"\n--- Фаза 4: AI Merge + Naming ---");
    
    // Формируем входные данные: нумерованные кластеры
    var clusterLines = new List<string>();
    for (int i = 0; i < finalClusters.Count; i++)
    {
        clusterLines.Add($"Кластер {i + 1}:");
        foreach (var key in finalClusters[i])
            clusterLines.Add($"- {key}");
        clusterLines.Add("");
    }
    
    string userMessage = string.Join("\n", clusterLines);
    string systemPrompt = BuildSystemPrompt("phase4_ai_merge.txt", includeSystemPrompt: false);
    
    var response = await DeepSeekHelper.SendRawRequestAsync<SeoArticleResponse>(
        _client, systemPrompt, userMessage, _deepSeekSettings,
        overrideThinking: true,
        overrideReasoningEffort: "high");
    
    if (response?.SeoArticles != null && response.SeoArticles.Count > 0)
    {
        foreach (var article in response.SeoArticles)
        {
            namedClusters[article.H1Title] = article.Keywords;
            Console.WriteLine($"  \"{article.H1Title}\" ({article.Keywords.Count} ключей)");
        }
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  [ОШИБКА] AI не вернул статьи. Использую исходные кластеры.");
        Console.ResetColor();
        // Fallback: исходные кластеры
        int idx = 0;
        foreach (var cluster in finalClusters)
        {
            idx++;
            string name = cluster.Count > 1 ? $"Кластер {idx}" : cluster[0];
            namedClusters[name] = cluster;
        }
    }
}
```

### 4. REMOVE methods from ClusterizationPipeline.cs:

- `RefineClusterAsync()` — per-cluster AI naming больше не нужен
- `MergeClustersAsync()` — Phase 4.5 merge pass больше не нужен

### 5. REMOVE or keep file:
- `Models/RefinedCluster.cs` — можно удалить (больше не используется)
- `instructions/step3_refactoring.txt` — инструкция для старого Phase 3, можно удалить

## Итоговый пайплайн

```
Phase 1: SERP сбор (XmlRiver + кэш)
Phase 2: URL overlap → Connected Components
Phase 2.5: Rescue Pass
Phase 3: IDF → Weighted Soft Jaccard (word embeddings) → HAC
Phase 4: AI Merge + Naming (один DeepSeek call со всеми кластерами)
```

## Преимущества

- **1 API вызов** вместо N+1 (экономия при 30 кластерах ~30x)
- AI видит **все кластеры сразу** → может корректно распределять общие запросы
- Единый промпт с чёткими правилами изоляции/слияния
- JSON-ответ парсится напрямую в `SeoArticleResponse`
