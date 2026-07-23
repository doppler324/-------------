using System;
using System.Collections.Generic;
using System.Linq;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Rescue Pass V2 (Phase 3.6): Nearest Centroid Assignment для сирот.
    ///
    /// Алгоритм:
    /// 1. Nearest Centroid: для каждой сироты найти ближайшее ядро (max DotProduct).
    ///    Если similarity >= rescueThreshold — прикрепить.
    /// 2. Pairwise Merge: оставшихся сирот попарно сравнить (порог Phase 3).
    ///    Если нашли пару — создать новый MacroBucket.
    /// 3. Абсолютные одиночки → "Нераспределённые".
    ///
    /// Дополняет старый Rescue Pass (Phase 2.5, по URL), а не заменяет его.
    /// </summary>
    public class RescuePassV2
    {
        private readonly float _rescueThreshold;
        private readonly float _phase3Threshold;

        /// <param name="rescueThreshold">
        /// Мягкий порог для Nearest Centroid (0.78 при macroMergeThreshold=0.83).
        /// </param>
        /// <param name="phase3Threshold">
        /// Строгий порог Phase 3 для pairwise merge сирот (например, 0.88).
        /// </param>
        public RescuePassV2(float rescueThreshold, float phase3Threshold)
        {
            _rescueThreshold = rescueThreshold;
            _phase3Threshold = phase3Threshold;
        }

        /// <summary>
        /// Распределяет сирот по ближайшим ядрам + pairwise merge остатка.
        /// </summary>
        /// <param name="cores">Ядра (макро-бакеты). Модифицируется in-place (пополняется новыми).</param>
        /// <param name="orphans">Фразы-сироты (одиночки + unclustered).</param>
        /// <param name="embeddings">Словарь эмбеддингов всех фраз.</param>
        /// <returns>Список абсолютно нераспределённых фраз.</returns>
        public List<string> RescueOrphans(
            List<MacroBucket> cores,
            List<string> orphans,
            Dictionary<string, float[]> embeddings)
        {
            if (orphans == null || orphans.Count == 0)
                return new List<string>();

            // Если нет ядер — все сироты сразу уходят в pairwise merge
            if (cores == null || cores.Count == 0)
            {
                return PairwiseMergeOrphans(orphans, cores ?? new List<MacroBucket>(), embeddings);
            }

            // Шаг 1: Nearest Centroid Assignment
            var trueUnclustered = new List<string>();
            int rescued = 0;

            foreach (var orphan in orphans)
            {
                if (!embeddings.TryGetValue(orphan, out var orphanVector))
                {
                    trueUnclustered.Add(orphan);
                    continue;
                }

                MacroBucket? bestMatch = null;
                float maxSimilarity = float.MinValue;

                foreach (var core in cores)
                {
                    if (core.RepresentativeVector == null || core.RepresentativeVector.Length == 0)
                        continue;

                    float similarity = DotProduct(orphanVector, core.RepresentativeVector);

                    if (similarity > maxSimilarity)
                    {
                        maxSimilarity = similarity;
                        bestMatch = core;
                    }
                }

                if (bestMatch != null && maxSimilarity >= _rescueThreshold)
                {
                    bestMatch.Keywords.Add(orphan);
                    rescued++;
                }
                else
                {
                    trueUnclustered.Add(orphan);
                }
            }

            Console.WriteLine($"  [RescueV2] К ядрам: {rescued}, осталось сирот: {trueUnclustered.Count}");

            // Шаг 2: Pairwise Merge среди оставшихся сирот
            var absoluteUnclustered = PairwiseMergeOrphans(trueUnclustered, cores, embeddings);

            return absoluteUnclustered;
        }

        /// <summary>
        /// Попарное сравнение сирот (порог Phase 3). Найденные пары → новые MacroBucket в cores.
        /// </summary>
        private List<string> PairwiseMergeOrphans(
            List<string> trueUnclustered,
            List<MacroBucket> cores,
            Dictionary<string, float[]> embeddings)
        {
            if (trueUnclustered.Count < 2)
                return trueUnclustered;

            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newBuckets = new List<MacroBucket>();
            int paired = 0;

            for (int i = 0; i < trueUnclustered.Count; i++)
            {
                string orphanA = trueUnclustered[i];
                if (processed.Contains(orphanA))
                    continue;

                if (!embeddings.TryGetValue(orphanA, out var vecA))
                {
                    processed.Add(orphanA);
                    continue;
                }

                var newBucket = new MacroBucket
                {
                    Name = orphanA,
                    Keywords = new List<string> { orphanA }
                };

                for (int j = i + 1; j < trueUnclustered.Count; j++)
                {
                    string orphanB = trueUnclustered[j];
                    if (processed.Contains(orphanB))
                        continue;

                    if (!embeddings.TryGetValue(orphanB, out var vecB))
                        continue;

                    float sim = DotProduct(vecA, vecB);

                    if (sim >= _phase3Threshold)
                    {
                        newBucket.Keywords.Add(orphanB);
                        processed.Add(orphanB);
                    }
                }

                processed.Add(orphanA);

                if (newBucket.Keywords.Count > 1)
                {
                    // Вычисляем центроид для нового бакета
                    var vectors = newBucket.Keywords
                        .Select(k => embeddings.TryGetValue(k, out var v) ? v : null)
                        .Where(v => v != null)
                        .ToList();

                    if (vectors.Count > 0)
                        newBucket.RepresentativeVector = ClusterMath.CalculateNormalizedCentroid(vectors!);

                    newBuckets.Add(newBucket);
                    paired++;
                }
            }

            if (newBuckets.Count > 0)
            {
                cores.AddRange(newBuckets);
                Console.WriteLine($"  [RescueV2] Pairwise merge: создано {newBuckets.Count} новых бакетов.");
            }

            // Абсолютные одиночки (не вошли ни в одну пару)
            var absolute = trueUnclustered.Where(o => !processed.Contains(o)).ToList();
            Console.WriteLine($"  [RescueV2] Абсолютных одиночек: {absolute.Count}");

            return absolute;
        }

        /// <summary>
        /// Быстрое скалярное произведение (Dot Product).
        /// </summary>
        private static float DotProduct(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || b.Length == 0)
                return 0f;

            int len = Math.Min(a.Length, b.Length);
            float dot = 0f;

            for (int i = 0; i < len; i++)
                dot += a[i] * b[i];

            return dot;
        }
    }
}
