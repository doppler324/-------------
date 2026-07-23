using System;
using System.Collections.Generic;
using System.Linq;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Macro Merge (Phase 3.5): объединение микро-кластеров (Phase 3) в макро-бакеты
    /// через Greedy Merge по representative-векторам.
    ///
    /// Режимы:
    /// - "medoid": representative = реальная фраза, наиболее близкая ко всем в кластере
    /// - "centroid": representative = L2-нормализованный усреднённый вектор всех фраз
    ///
    /// Имя бакета в обоих режимах = медоид ядра (реальная фраза из самого крупного кластера).
    /// </summary>
    public class MacroMergePass
    {
        private readonly float _threshold;
        private readonly string _representativeMode;

        /// <param name="threshold">
        /// Порог cosine similarity между representative-векторами для слияния.
        /// Рекомендуется: sentenceHacThreshold - 0.05.
        /// </param>
        /// <param name="representativeMode">
        /// "medoid" — реальная фраза (медоид кластера).
        /// "centroid" — L2-нормализованный центроид всех фраз кластера.
        /// </param>
        public MacroMergePass(float threshold, string representativeMode = "centroid")
        {
            _threshold = threshold;
            _representativeMode = representativeMode?.ToLowerInvariant() ?? "centroid";
        }

        /// <summary>
        /// Выполняет Greedy Merge микро-кластеров в макро-бакеты.
        /// Использует уже готовые sentence embeddings (без API-запросов).
        /// </summary>
        /// <param name="microClusters">Список микро-кластеров (фраз) из Phase 3 (только кластеры с 2+ фразами).</param>
        /// <param name="embeddings">
        /// Словарь уже полученных sentence embeddings (фраза → float[]).
        /// Должен содержать все фразы из microClusters.
        /// </param>
        /// <returns>
        /// List{MacroBucket} — готовые макро-бакеты с representative-векторами.
        /// </returns>
        public System.Threading.Tasks.Task<List<MacroBucket>> MergeAsync(
            List<List<string>> microClusters,
            Dictionary<string, float[]> embeddings)
        {
            if (microClusters == null || microClusters.Count == 0)
                return System.Threading.Tasks.Task.FromResult(new List<MacroBucket>());

            // Шаг 1: для каждого микро-кластера получаем representative-вектор и медоид
            var buckets = new List<MacroBucket>();
            foreach (var cluster in microClusters)
            {
                if (cluster.Count == 0)
                    continue;

                string medoid = FindMedoid(cluster, embeddings);
                float[] repVector = GetRepresentativeVector(cluster, embeddings);

                buckets.Add(new MacroBucket
                {
                    Name = medoid,
                    RepresentativeVector = repVector,
                    Keywords = cluster.ToList()
                });
            }

            // Шаг 2: Greedy Merge
            var merged = GreedyMerge(buckets);

            return System.Threading.Tasks.Task.FromResult(merged);
        }

        /// <summary>
        /// Находит медоид кластера — фразу с максимальной суммой cosine similarity
        /// ко всем остальным фразам в этом же кластере.
        /// Для кластеров 1-2 фразы — берётся первая.
        /// </summary>
        private string FindMedoid(List<string> cluster, Dictionary<string, float[]> embeddings)
        {
            if (cluster.Count <= 2)
                return cluster[0];

            string bestPhrase = cluster[0];
            double bestScore = double.MinValue;

            foreach (var candidate in cluster)
            {
                if (!embeddings.TryGetValue(candidate, out var embCand))
                    continue;

                double totalSim = 0.0;
                int count = 0;

                foreach (var other in cluster)
                {
                    if (ReferenceEquals(candidate, other))
                        continue;

                    if (!embeddings.TryGetValue(other, out var embOther))
                        continue;

                    totalSim += CosineSimilarity(embCand, embOther);
                    count++;
                }

                if (count > 0)
                {
                    double avgSim = totalSim / count;
                    if (avgSim > bestScore)
                    {
                        bestScore = avgSim;
                        bestPhrase = candidate;
                    }
                }
            }

            return bestPhrase;
        }

        /// <summary>
        /// Возвращает representative-вектор для кластера:
        /// - "medoid": эмбеддинг медоида
        /// - "centroid": L2-нормализованный центроид всех векторов кластера
        /// </summary>
        private float[] GetRepresentativeVector(List<string> cluster, Dictionary<string, float[]> embeddings)
        {
            if (_representativeMode == "medoid")
            {
                string medoid = FindMedoid(cluster, embeddings);
                if (embeddings.TryGetValue(medoid, out var emb))
                    return emb;
                return Array.Empty<float>();
            }

            // Режим "centroid": собираем все векторы и вычисляем центроид
            var vectors = new List<float[]>(cluster.Count);
            foreach (var phrase in cluster)
            {
                if (embeddings.TryGetValue(phrase, out var emb))
                    vectors.Add(emb);
            }

            if (vectors.Count == 0)
                return Array.Empty<float>();

            return ClusterMath.CalculateNormalizedCentroid(vectors);
        }

        /// <summary>
        /// Greedy Merge: от крупных кластеров к мелким.
        /// Для сравнения использует representative-векторы (CosineSimilarity).
        /// </summary>
        private List<MacroBucket> GreedyMerge(List<MacroBucket> buckets)
        {
            // Сортируем по убыванию размера
            var sorted = buckets.OrderByDescending(b => b.Size).ToList();
            var result = new List<MacroBucket>();

            while (sorted.Count > 0)
            {
                // Берём самый крупный кластер как ядро
                var core = sorted[0];
                sorted.RemoveAt(0);

                var corePhrases = new HashSet<string>(core.Keywords, StringComparer.OrdinalIgnoreCase);

                // Сканируем остальные на поглощение
                var absorbed = new List<int>();
                for (int i = sorted.Count - 1; i >= 0; i--)
                {
                    var candidate = sorted[i];

                    if (core.RepresentativeVector == null || core.RepresentativeVector.Length == 0 ||
                        candidate.RepresentativeVector == null || candidate.RepresentativeVector.Length == 0)
                        continue;

                    double sim = CosineSimilarity(core.RepresentativeVector, candidate.RepresentativeVector);

                    if (sim >= _threshold)
                    {
                        foreach (var phrase in candidate.Keywords)
                            corePhrases.Add(phrase);

                        absorbed.Add(i);
                    }
                }

                // Удаляем поглощённые кластеры
                foreach (int idx in absorbed.OrderByDescending(x => x))
                    sorted.RemoveAt(idx);

                // Сохраняем бакет
                result.Add(new MacroBucket
                {
                    Name = core.Name,
                    RepresentativeVector = core.RepresentativeVector ?? System.Array.Empty<float>(),
                    Keywords = corePhrases.ToList()
                });
            }

            return result;
        }

        /// <summary>
        /// Cosine similarity между двумя векторами.
        /// </summary>
        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0)
                return 0.0;

            int len = Math.Min(a.Length, b.Length);
            double dot = 0.0, magA = 0.0, magB = 0.0;

            for (int i = 0; i < len; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }

            double magnitude = Math.Sqrt(magA) * Math.Sqrt(magB);
            return magnitude > 0.0 ? dot / magnitude : 0.0;
        }
    }
}
