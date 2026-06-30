using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// SERP-валидатор кластеров: через XmlRiver получает реальную поисковую выдачу
    /// по сэмплу ключей и проверяет, совпадают ли интенты внутри каждого кластера.
    ///
    /// Трёхфазная архитектура:
    /// 1. Сбор SERP — для каждого кластера запрашивается поисковая выдача по сэмплу ключей.
    /// 2. Intra-cluster — вычисляется Jaccard overlap URL внутри каждого кластера.
    /// 3. Cross-cluster — плохие ключи (низкий overlap) перемещаются в подходящий кластер.
    /// </summary>
    public class SerpClusterValidator
    {
        private readonly XmlRiverClient _xmlRiver;
        private readonly XmlRiverSettings _settings;

        // Глобальный кэш SERP: ключевое слово → результат поиска
        private readonly Dictionary<string, KeywordSearchResult> _allSerpData = new();

        // SERP-профили кластеров: имя кластера → объединение URL всех его сэмплированных ключей
        private readonly Dictionary<string, HashSet<string>> _clusterSerpProfiles = new();

        private const string UnclusteredKey = "Нераспределённые";
        private const string UnmatchedKey = "Неопределённые (серп)";

        public SerpClusterValidator(XmlRiverClient xmlRiver, XmlRiverSettings settings)
        {
            _xmlRiver = xmlRiver ?? throw new ArgumentNullException(nameof(xmlRiver));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Основной метод: трёхфазная SERP-валидация кластеров.
        /// </summary>
        public async Task<Dictionary<string, List<string>>> ValidateAsync(
            Dictionary<string, List<string>>? clusters)
        {
            if (clusters == null || clusters.Count == 0)
                return new Dictionary<string, List<string>>();

            _allSerpData.Clear();
            _clusterSerpProfiles.Clear();

            int totalApiCalls = 0;
            int flaggedClusters = 0;

            // ==========================================
            // Фаза 1: Сбор SERP для всех кластеров (параллельно)
            // ==========================================
            Console.WriteLine("  Фаза 1: сбор SERP-данных...");

            // Собираем: для каждого кластера → список сэмплированных ключей
            var clusterSamples = new Dictionary<string, List<string>>();

            foreach (var kvp in clusters)
            {
                if (kvp.Key == UnclusteredKey || kvp.Value.Count <= 1)
                    continue;

                var sampleKeys = SelectSampleKeywords(kvp.Value, _settings.SampleSize);
                clusterSamples[kvp.Key] = sampleKeys;
            }

            // Собираем все уникальные ключи для опроса
            var allSampleKeys = clusterSamples.Values
                .SelectMany(k => k)
                .Distinct()
                .ToList();

            // Параллельный опрос XmlRiver (MaxConcurrency потоков)
            if (allSampleKeys.Count > 0)
            {
                var batchResults = await _xmlRiver.SearchBatchAsync(
                    allSampleKeys,
                    _settings.MaxConcurrency,
                    _settings.TopResultsCount);

                foreach (var kvp in batchResults)
                    _allSerpData[kvp.Key] = kvp.Value;

                totalApiCalls = batchResults.Count;
            }

            // Строим SERP-профили кластеров (объединение URL сэмплированных ключей)
            foreach (var kvp in clusterSamples)
            {
                var profile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in kvp.Value)
                {
                    if (_allSerpData.TryGetValue(key, out var sr))
                    {
                        foreach (var url in sr.Urls)
                            profile.Add(url);
                    }
                }
                _clusterSerpProfiles[kvp.Key] = profile;
            }

            // ==========================================
            // Фаза 2: Intra-cluster проверка
            // ==========================================
            Console.WriteLine("  Фаза 2: внутрикластерная проверка...");

            var result = new Dictionary<string, List<string>>();
            var problemClusters = new List<(string Name, List<string> BadKeys)>();

            // Сначала переносим служебные и мелкие кластеры
            foreach (var kvp in clusters)
            {
                if (kvp.Key == UnclusteredKey || kvp.Value.Count <= 1)
                    result[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in clusters)
            {
                if (kvp.Key == UnclusteredKey || kvp.Value.Count <= 1)
                    continue;

                Console.Write($"  [SERP] \"{kvp.Key}\" ({kvp.Value.Count} ключей): ");

                var sampleKeys = clusterSamples[kvp.Key];

                // Собираем SERP для сэмпла из кэша
                var searchResults = sampleKeys
                    .Select(k => _allSerpData.TryGetValue(k, out var sr) ? sr : null)
                    .Where(sr => sr != null && sr.Urls.Count > 0)
                    .Select(sr => sr!)
                    .ToList();

                // Если все SERP пустые — пропускаем кластер
                if (searchResults.Count == 0)
                {
                    Console.WriteLine("SERP пусты (пропускаем).");
                    result[kvp.Key] = kvp.Value;
                    continue;
                }

                // Детальный вывод попарных Jaccard (с именами ключей)
                var pairLogs = new List<string>();
                for (int i = 0; i < searchResults.Count; i++)
                {
                    for (int j = i + 1; j < searchResults.Count; j++)
                    {
                        double pairOverlap = ComputeUrlOverlap(
                            searchResults[i].Urls, searchResults[j].Urls);
                        pairLogs.Add(
                            $"    \"{searchResults[i].Keyword}\" ↔ \"{searchResults[j].Keyword}\": " +
                            $"J={pairOverlap:P0}");
                    }
                }

                // Вычисляем средний попарный Jaccard overlap
                double avgOverlap = ComputeAverageOverlap(searchResults);

                if (avgOverlap >= _settings.MinOverlap)
                {
                    Console.WriteLine($"overlap {avgOverlap:P0} — OK");
                    // При OK всё равно показываем пары (для информации)
                    foreach (var log in pairLogs)
                        Console.WriteLine(log);
                    result[kvp.Key] = kvp.Value;
                }
                else
                {
                    flaggedClusters++;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"overlap {avgOverlap:P0} — НИЗКИЙ! Пары:");
                    Console.ResetColor();
                    foreach (var log in pairLogs)
                        Console.WriteLine(log);

                    // Определяем, какие сэмплированные ключи "плохие"
                    var badKeys = IdentifyBadKeys(sampleKeys, _allSerpData);

                    if (badKeys.Count > 0)
                    {
                        // Хорошие сэмплированные + все несэмплированные остаются в кластере
                        var remainingKeys = kvp.Value
                            .Where(k => !badKeys.Contains(k))
                            .ToList();

                        if (remainingKeys.Count > 0)
                        {
                            result[kvp.Key] = remainingKeys;
                        }

                        problemClusters.Add((kvp.Key, badKeys));
                        Console.WriteLine($"    → {badKeys.Count} ключей с расходящимся интентом.");
                    }
                    else
                    {
                        // Не смогли определить bad keys — оставляем как есть
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"    → Не удалось определить проблемные ключи, оставлено как есть.");
                        Console.ResetColor();
                        result[kvp.Key] = kvp.Value;
                    }
                }
            }

            // ==========================================
            // Фаза 3: Cross-cluster перегруппировка
            // ==========================================
            if (problemClusters.Count > 0)
            {
                Console.WriteLine($"  Фаза 3: кросс-кластерная перегруппировка " +
                    $"({problemClusters.Sum(p => p.BadKeys.Count)} ключей)...");

                var unmatchedKeys = new List<string>();

                foreach (var (sourceCluster, badKeys) in problemClusters)
                {
                    foreach (var badKey in badKeys)
                    {
                        if (!_allSerpData.TryGetValue(badKey, out var badKeyResult))
                            continue;

                        // Ищем лучший кластер для этого ключа
                        string? bestCluster = null;
                        double bestOverlap = 0;

                        foreach (var profileKvp in _clusterSerpProfiles)
                        {
                            // Не перемещаем в свой же кластер
                            if (profileKvp.Key == sourceCluster)
                                continue;

                            // Пропускаем удалённые кластеры
                            if (!result.ContainsKey(profileKvp.Key))
                                continue;

                            double overlap = ComputeUrlOverlap(
                                badKeyResult.Urls, profileKvp.Value);

                            if (overlap > bestOverlap)
                            {
                                bestOverlap = overlap;
                                bestCluster = profileKvp.Key;
                            }
                        }

                        if (bestCluster != null && bestOverlap >= _settings.MinOverlap)
                        {
                            // Перемещаем ключ в лучший кластер
                            if (!result.ContainsKey(bestCluster))
                                result[bestCluster] = new List<string>();
                            result[bestCluster].Add(badKey);
                            Console.WriteLine($"    → \"{badKey}\" перемещён в \"{bestCluster}\" " +
                                $"(overlap {bestOverlap:P0}).");
                        }
                        else
                        {
                            // Не нашли подходящий кластер
                            unmatchedKeys.Add(badKey);
                            Console.WriteLine($"    → \"{badKey}\" не нашёл подходящий кластер " +
                                $"(лучший: {bestCluster ?? "нет"}, overlap {bestOverlap:P0}).");
                        }
                    }
                }

                // Помещаем неприкаянные ключи в служебный кластер
                if (unmatchedKeys.Count > 0)
                {
                    if (!result.ContainsKey(UnmatchedKey))
                        result[UnmatchedKey] = new List<string>();
                    result[UnmatchedKey].AddRange(unmatchedKeys);
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"    → {unmatchedKeys.Count} ключей помещены в \"{UnmatchedKey}\".");
                    Console.ResetColor();
                }
            }

            // ==========================================
            // Фаза 4: Финальная проверка overlap во всех кластерах
            // ==========================================
            int belowThresholdClusters = 0;

            if (_settings.EnableFinalValidation)
            {
                Console.WriteLine("  Фаза 4: финальная проверка overlap во всех кластерах...");

                foreach (var kvp in result)
                {
                    if (kvp.Key == UnclusteredKey || kvp.Key == UnmatchedKey || kvp.Value.Count <= 1)
                        continue;

                    // Берём все ключи кластера, для которых есть SERP в кэше
                    var keysWithSerp = kvp.Value
                        .Where(k => _allSerpData.TryGetValue(k, out var sr) && sr.Urls.Count > 0)
                        .Select(k => _allSerpData[k])
                        .ToList();

                    if (keysWithSerp.Count < 2)
                        continue;

                    double finalOverlap = ComputeAverageOverlap(keysWithSerp);

                    if (finalOverlap < _settings.MinOverlap)
                    {
                        belowThresholdClusters++;
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  ⚠ [ФИНАЛ] \"{kvp.Key}\" ({kvp.Value.Count} ключей): " +
                            $"overlap {finalOverlap:P0} < {_settings.MinOverlap:P0} (мин.)");
                        Console.ResetColor();
                    }
                }

                if (belowThresholdClusters == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  ✓ Финальная проверка: все кластеры в порядке.");
                    Console.ResetColor();
                }
            }

            // Итоговая статистика
            int movedKeys = problemClusters.Sum(p => p.BadKeys.Count);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n  [SERP] Итого: {totalApiCalls} запросов к XmlRiver, " +
                $"{flaggedClusters} проблемных кластеров, " +
                $"{movedKeys} ключей перегруппировано.");
            Console.ResetColor();

            return result;
        }

        /// <summary>
        /// Определяет, какие из сэмплированных ключей имеют низкий overlap
        /// со средним по кластеру (исключая сам ключ).
        /// </summary>
        private List<string> IdentifyBadKeys(
            List<string> sampleKeys,
            Dictionary<string, KeywordSearchResult> serpData)
        {
            var badKeys = new List<string>();

            // Если всего 1 ключ с SERP — не можем определить
            var validKeys = sampleKeys
                .Where(k => serpData.TryGetValue(k, out var sr) && sr.Urls.Count > 0)
                .ToList();

            if (validKeys.Count <= 1)
                return badKeys;

            foreach (var key in validKeys)
            {
                if (!serpData.TryGetValue(key, out var keyResult))
                    continue;

                // Вычисляем средний overlap этого ключа со ВСЕМИ остальными
                double avgForKey = ComputeAverageOverlapExcluding(
                    validKeys.Select(k => serpData[k]).ToList(),
                    keyResult);

                if (avgForKey < _settings.MinOverlap)
                    badKeys.Add(key);
            }

            return badKeys;
        }

        /// <summary>
        /// Выбирает до maxKeys репрезентативных ключей из списка.
        /// Если ключей больше maxKeys, берёт распределённые (первый, середина, последний).
        /// </summary>
        private static List<string> SelectSampleKeywords(List<string> keywords, int maxKeys)
        {
            if (keywords.Count <= maxKeys)
                return keywords;

            var sample = new List<string> { keywords[0] };

            if (maxKeys >= 2)
                sample.Add(keywords[keywords.Count / 2]);
            if (maxKeys >= 3)
                sample.Add(keywords[^1]);

            // Для sampleSize > 3 добавляем равномерно распределённые
            for (int i = 3; i < maxKeys && i < keywords.Count; i++)
            {
                int index = (keywords.Count * i) / maxKeys;
                if (index > 0 && index < keywords.Count - 1 && !sample.Contains(keywords[index]))
                    sample.Add(keywords[index]);
            }

            return sample;
        }

        /// <summary>
        /// Вычисляет средний попарный Jaccard overlap между наборами URL.
        /// J(A, B) = |A ∩ B| / |A ∪ B|.
        /// </summary>
        private static double ComputeAverageOverlap(List<KeywordSearchResult> results)
        {
            var overlaps = new List<double>();

            for (int i = 0; i < results.Count; i++)
            {
                for (int j = i + 1; j < results.Count; j++)
                {
                    var urlsA = results[i].Urls;
                    var urlsB = results[j].Urls;

                    if (urlsA.Count == 0 || urlsB.Count == 0)
                        continue;

                    double overlap = ComputeUrlOverlap(urlsA, urlsB);
                    if (overlap >= 0)
                        overlaps.Add(overlap);
                }
            }

            return overlaps.Count > 0 ? overlaps.Average() : 0.0;
        }

        /// <summary>
        /// Jaccard overlap между двумя списками URL.
        /// </summary>
        private static double ComputeUrlOverlap(List<string> urlsA, List<string> urlsB)
        {
            if (urlsA.Count == 0 || urlsB.Count == 0)
                return 0;

            var intersection = urlsA.Intersect(urlsB, StringComparer.OrdinalIgnoreCase).Count();
            var union = urlsA.Union(urlsB, StringComparer.OrdinalIgnoreCase).Count();

            return union > 0 ? (double)intersection / union : 0.0;
        }

        /// <summary>
        /// Jaccard overlap между списком URL и набором URL (HashSet).
        /// </summary>
        private static double ComputeUrlOverlap(List<string> urls, HashSet<string> urlSet)
        {
            if (urls.Count == 0 || urlSet.Count == 0)
                return 0;

            int intersection = urls.Count(url => urlSet.Contains(url));
            var union = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
            union.UnionWith(urlSet);

            return union.Count > 0 ? (double)intersection / union.Count : 0.0;
        }

        /// <summary>
        /// Вычисляет средний overlap одного элемента со всеми остальными.
        /// </summary>
        private static double ComputeAverageOverlapExcluding(
            List<KeywordSearchResult> results, KeywordSearchResult target)
        {
            var overlaps = new List<double>();

            foreach (var other in results)
            {
                // Сравниваем по объекту KeywordSearchResult
                if (ReferenceEquals(other, target))
                    continue;

                if (target.Urls.Count == 0 || other.Urls.Count == 0)
                    continue;

                double overlap = ComputeUrlOverlap(target.Urls, other.Urls);
                if (overlap >= 0)
                    overlaps.Add(overlap);
            }

            return overlaps.Count > 0 ? overlaps.Average() : 0.0;
        }
    }
}
