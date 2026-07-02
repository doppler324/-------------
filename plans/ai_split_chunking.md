# Chunking для AI Semantic Split (Фаза 3b)

## Проблема

Hard-stopped кластеры (453, 78, 88, 108 ключей) отправляются целиком в `SplitClusterAsync` → deepseek-chat.  
453 ключа не влезают в контекст AI → таймаут → fallback на singleton → 453 кластера по 1 ключу.

Остальные 3 кластера (78, 88, 108) успешно разбились AI на 2 подкластера каждый.

**Корень:** Нет чанкования. Весь кластер идёт одним запросом.

## Решение: Chunking по maxClusterSize

### Алгоритм

1. В Фазе 3b, если кластер > maxClusterSize (60):
   - Разбить на чанки по maxClusterSize ключей
   - Каждый чанк отправить в `SplitClusterAsync`
   - Собрать все подкластеры
   - Если AI не смог разбить чанк — ключи чанка становятся singleton

### Псевдокод

```
foreach (var cluster in splitClusters)
{
    if (cluster.Count <= maxClusterSize)
    {
        finalClusters.Add(cluster);
        continue;
    }

    // Chunking
    var chunks = cluster
        .Select((key, index) => (key, index))
        .GroupBy(x => x.index / maxClusterSize)
        .Select(g => g.Select(x => x.key).ToList())
        .ToList();

    foreach (var chunk in chunks)
    {
        var aiSplit = await SplitClusterAsync(chunk, maxClusterSize);

        if (aiSplit != null && aiSplit.Count > 0)
        {
            // добавляем подкластеры + lost keys → singletons
            ...
        }
        else
        {
            // fallback: ключи чанка → singletons
            ...
        }
    }
}
```

### Преимущества

- Каждый запрос к AI — максимум 60 ключей (влезает в контекст)
- Даже если один чанк не обработается — остальные успешно разобьются
- Код меняется минимально, новая логика только в Фазе 3b

### Риски

- Ключи одного чанка могут относиться к разным интентам → AI должен их разделить
- Ключи из разных чанков могут относиться к одному интенту → AI разделит их по разным чанкам → дублирование интентов
  - Решение: это не страшно, Фаза 4 (AI-именование) даст разные имена; при необходимости можно добавить пост-объединение по схожести имён

## Оценка эффекта

Сейчас: 4 hard-stopped кластера (453+78+88+108 = 727 ключей) → 453 singleton + 6 AI-подкластеров  
После chunking: 727 ключей / 60 = ~13 чанков → ~26-40 AI-подкластеров (вместо 453 singleton)
