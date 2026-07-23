using System;
using System.Collections.Generic;
using System.Linq;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Кластеризация поисковых запросов на уровне целых фраз (sentence-level)
    /// внутри SERP-кластера.
    ///
    /// Алгоритм:
    /// 1. Получение sentence embeddings через OpenRouter (batch: все фразы кластера).
    /// 2. Построение матрицы cosine similarity N×N.
    /// 3. HAC (Hierarchical Agglomerative Clustering) с complete linkage до порога.
    ///
    /// Заменяет WordLevelClusterizer (IDF + Weighted Soft Jaccard).
    /// text-embedding-3-small от OpenAI используется как sentence encoder.
    /// </summary>
    public class SentenceLevelClusterizer
    {
        private readonly float _hacThreshold;

        /// <param name="hacThreshold">
        /// Порог cosine similarity для остановки HAC (complete linkage).
        /// Рекомендуется 0.82 для text-embedding-3-small.
        /// Выше = мельче кластеры, ниже = крупнее.
        /// </param>
        public SentenceLevelClusterizer(float hacThreshold = 0.82f)
        {
            _hacThreshold = hacThreshold;
        }

        /// <summary>
        /// Выполняет sentence-level кластеризацию фраз внутри SERP-кластера.
        /// </summary>
        /// <param name="phrases">Фразы из одного SERP-кластера.</param>
        /// <param name="getEmbeddingsAsync">
        /// Функция для получения эмбеддингов списка фраз (batch).
        /// Ожидается: Dictionary{фраза → float[]}.
        /// </param>
        /// <returns>Список подкластеров (каждый — список оригинальных фраз).</returns>
        public async System.Threading.Tasks.Task<List<List<string>>> ClusterizeAsync(
            List<string> phrases,
            Func<List<string>, System.Threading.Tasks.Task<Dictionary<string, float[]>>> getEmbeddingsAsync)
        {
            if (phrases == null || phrases.Count == 0)
                return new List<List<string>>();

            // Для кластеров ≤ 4 фраз — HAC не запускаем (пропускаем как есть)
            if (phrases.Count <= 4)
                return phrases.Select(p => new List<string> { p }).ToList();

            // Шаг 1: получаем sentence embeddings (фразы целиком)
            var embeddings = await getEmbeddingsAsync(phrases);

            // Шаг 2: строим матрицу cosine similarity
            int n = phrases.Count;
            var similarityMatrix = new float[n][];
            for (int i = 0; i < n; i++)
                similarityMatrix[i] = new float[n];

            for (int i = 0; i < n; i++)
            {
                if (!embeddings.TryGetValue(phrases[i], out var embI))
                    continue;

                for (int j = i + 1; j < n; j++)
                {
                    if (!embeddings.TryGetValue(phrases[j], out var embJ))
                        continue;

                    float sim = (float)CosineSimilarity(embI, embJ);
                    similarityMatrix[i][j] = sim;
                    similarityMatrix[j][i] = sim;
                }
            }

            // Шаг 3: HAC
            var clusters = RunHAC(n, similarityMatrix);

            // Преобразуем индексы обратно в фразы
            return clusters
                .Select(cluster => cluster.Select(idx => phrases[idx]).ToList())
                .ToList();
        }

        /// <summary>
        /// Cosine similarity между двумя векторами-эмбеддингами.
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

        /// <summary>
        /// HAC (Hierarchical Agglomerative Clustering) с complete linkage.
        /// Начинает с N кластеров (по одной фразе в каждом).
        /// На каждом шаге находит пару с максимальной схожестью.
        /// Если max >= hacThreshold — склеивает.
        /// </summary>
        /// <param name="n">Число фраз.</param>
        /// <param name="similarityMatrix">Матрица попарной схожести N×N.</param>
        /// <returns>Список кластеров (каждый — список индексов фраз).</returns>
        private List<List<int>> RunHAC(int n, float[][] similarityMatrix)
        {
            // Каждый кластер — список индексов фраз
            var clusters = new List<HashSet<int>>();
            for (int i = 0; i < n; i++)
                clusters.Add(new HashSet<int> { i });

            while (clusters.Count > 1)
            {
                // Ищем пару кластеров с максимальной схожестью (complete linkage)
                int bestA = -1, bestB = -1;
                float bestSim = -1.0f;

                for (int i = 0; i < clusters.Count; i++)
                {
                    for (int j = i + 1; j < clusters.Count; j++)
                    {
                        // Complete linkage: min схожесть между элементами групп
                        float minSim = float.MaxValue;
                        foreach (int idxA in clusters[i])
                        {
                            foreach (int idxB in clusters[j])
                            {
                                float sim = similarityMatrix[idxA][idxB];
                                if (sim < minSim)
                                    minSim = sim;
                            }
                        }

                        if (minSim > bestSim)
                        {
                            bestSim = minSim;
                            bestA = i;
                            bestB = j;
                        }
                    }
                }

                // Если лучшая схожесть ниже порога — останавливаемся
                if (bestA < 0 || bestB < 0 || bestSim < _hacThreshold)
                    break;

                // Склеиваем bestB в bestA
                clusters[bestA].UnionWith(clusters[bestB]);
                clusters.RemoveAt(bestB);
            }

            // Преобразуем в List<List<int>>
            return clusters
                .Select(c => c.ToList())
                .ToList();
        }
    }
}
