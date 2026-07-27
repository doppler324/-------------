using System;
using System.Collections.Generic;
using System.Linq;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Графовый кластеризатор интентов на основе SERP-данных.
    ///
    /// Алгоритм:
    /// 1. Для каждой пары ключей вычисляется пересечение URL в Топ-10 выдачи.
    /// 2. Если |SERP_u ∩ SERP_v| >= OverlapThreshold — добавляется ребро.
    /// 3. Поиск компонент связности через BFS — каждый компонент = один интент.
    ///
    /// Это математически строгий подход: Яндекс сам показал, какие ключи
    /// относятся к одному интенту (одинаковые сайты в выдаче).
    /// </summary>
    public class SerpGraphClusterizer
    {
        private readonly int _overlapThreshold;
        private readonly int _topResultsCount;

        /// <summary>
        /// Порог пересечения URL для создания ребра в графе.
        /// </summary>
        public int OverlapThreshold => _overlapThreshold;

        public SerpGraphClusterizer(int overlapThreshold = 3, int topResultsCount = 10)
        {
            _overlapThreshold = overlapThreshold;
            _topResultsCount = topResultsCount;
        }

        /// <summary>
        /// Выполняет кластеризацию ключей на основе SERP-данных.
        /// </summary>
        /// <param name="serpData">SERP-результаты для всех ключей.</param>
        /// <returns>Кортеж: (кластеры, нераспределённые ключи).</returns>
        public (List<List<string>> Clusters, List<string> Unclustered) Clusterize(
            Dictionary<string, KeywordSearchResult> serpData)
        {
            if (serpData == null || serpData.Count == 0)
                return (new List<List<string>>(), new List<string>());

            var keywords = serpData.Keys.ToList();
            int n = keywords.Count;

            Console.WriteLine($"  [Graph] Кластеризация {n} ключей, порог = {_overlapThreshold} URL...");

            // Шаг 1: Строим граф смежности
            var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keywords)
                adjacency[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int edgesCount = 0;

            // Оптимизация: предварительно фильтруем ключи без SERP
            var validKeys = keywords
                .Where(k => serpData.TryGetValue(k, out var sr) && sr.Urls.Count >= _overlapThreshold)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (validKeys.Count < n)
            {
                Console.WriteLine($"  [Graph] {n - validKeys.Count} ключей имеют меньше {_overlapThreshold} URL в выдаче — будут в unclustered.");
            }

            for (int i = 0; i < n; i++)
            {
                var keyA = keywords[i];

                if (!validKeys.Contains(keyA))
                    continue;

                var urlsA = new HashSet<string>(
                    serpData[keyA].Urls.Take(_topResultsCount),
                    StringComparer.OrdinalIgnoreCase);

                if (urlsA.Count < _overlapThreshold)
                    continue;

                for (int j = i + 1; j < n; j++)
                {
                    var keyB = keywords[j];

                    if (!validKeys.Contains(keyB))
                        continue;

                    var urlsB = serpData[keyB].Urls.Take(_topResultsCount).ToList();
                    if (urlsB.Count < _overlapThreshold)
                        continue;

                    // Считаем пересечение
                    int overlap = urlsB.Count(url => urlsA.Contains(url));

                    if (overlap >= _overlapThreshold)
                    {
                        adjacency[keyA].Add(keyB);
                        adjacency[keyB].Add(keyA);
                        edgesCount++;
                    }
                }

                // Прогресс для больших наборов (перезапись строки)
                if (n > 200 && (i + 1) % 100 == 0)
                {
                    Console.SetCursorPosition(0, Console.CursorTop);
                    int width = Console.WindowWidth - 1;
                    Console.Write(($"  [Graph] Обработано {i + 1}/{n} ключей...").PadRight(width).Substring(0, width));
                }
            }

            // Стираем строку прогресса перед финальным выводом
            Console.SetCursorPosition(0, Console.CursorTop);
            int clearW = Console.WindowWidth - 1;
            Console.Write(new string(' ', clearW));
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.WriteLine($"  [Graph] Построено {edgesCount} рёбер.");

            // Шаг 2: Поиск компонент связности (BFS)
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clusters = new List<List<string>>();
            var unclustered = new List<string>();

            foreach (var key in keywords)
            {
                if (visited.Contains(key))
                    continue;

                // Если у ключа нет рёбер — он изолирован
                if (adjacency[key].Count == 0)
                {
                    visited.Add(key);
                    unclustered.Add(key);
                    continue;
                }

                // BFS
                var component = new List<string>();
                var queue = new Queue<string>();
                queue.Enqueue(key);
                visited.Add(key);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    component.Add(current);

                    foreach (var neighbor in adjacency[current])
                    {
                        if (!visited.Contains(neighbor))
                        {
                            visited.Add(neighbor);
                            queue.Enqueue(neighbor);
                        }
                    }
                }

                if (component.Count > 0)
                    clusters.Add(component);
            }

            Console.WriteLine($"  [Graph] Создано {clusters.Count} SERP-кластеров, {unclustered.Count} ключей не кластеризовано.");

            // Сортировка кластеров по размеру (убывание)
            clusters = clusters.OrderByDescending(c => c.Count).ToList();

            return (clusters, unclustered);
        }
    }
}
