using System;
using System.Collections.Generic;
using System.Linq;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Кластеризация поисковых запросов на уровне слов внутри SERP-кластера.
    ///
    /// Алгоритм:
    /// 1. Токенизация и удаление стоп-слов.
    /// 2. Сбор уникальных слов кластера.
    /// 3. Получение word embeddings через OpenRouter.
    /// 4. Вычисление IDF-весов для каждого слова (локально внутри кластера).
    /// 5. Weighted Soft Jaccard между всеми парами фраз.
    /// 6. HAC (Hierarchical Agglomerative Clustering) до порога.
    ///
    /// Преимущества перед full-phrase Tanimoto графом:
    /// - IDF автоматически штрафует частотные слова ("унитаз", "бачок")
    ///   и усиливает редкие ("поплавок", "сифон", "микролифт").
    /// - SERP уже гарантирует макросмысловую группу → можно резать по словам.
    /// - Нет жёсткого удаления слов → нет пустых массивов.
    /// </summary>
    public class WordLevelClusterizer
    {
        private readonly float _wordSimThreshold;
        private readonly float _hacThreshold;

        /// <summary>Стоп-слова (русские, регистронезависимые).</summary>
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

        /// <param name="wordSimThreshold">
        /// Порог cosine similarity между word embeddings для засчитывания совпадения.
        /// Рекомендуется 0.85 — ловит морфологию ("поплавок"≈"поплавка").
        /// </param>
        /// <param name="hacThreshold">
        /// Порог Weighted Jaccard для остановки HAC.
        /// Рекомендуется 0.35 — ниже = мельче кластеры, выше = крупнее.
        /// </param>
        public WordLevelClusterizer(float wordSimThreshold = 0.85f, float hacThreshold = 0.35f)
        {
            _wordSimThreshold = wordSimThreshold;
            _hacThreshold = hacThreshold;
        }

        /// <summary>
        /// Выполняет word-level кластеризацию фраз внутри SERP-кластера.
        /// </summary>
        /// <param name="phrases">Фразы из одного SERP-кластера.</param>
        /// <param name="getEmbeddingsAsync">
        /// Функция для получения эмбеддингов списка слов (batch).
        /// Ожидается: Dictionary{слово → float[]}.
        /// </param>
        /// <returns>Список подкластеров (каждый — список оригинальных фраз).</returns>
        public async System.Threading.Tasks.Task<List<List<string>>> ClusterizeAsync(
            List<string> phrases,
            Func<List<string>, System.Threading.Tasks.Task<Dictionary<string, float[]>>> getEmbeddingsAsync)
        {
            if (phrases == null || phrases.Count == 0)
                return new List<List<string>>();

            // Шаг 1: токенизация и удаление стоп-слов
            var tokenizedPhrases = phrases
                .Select(p => RemoveStopWords(Tokenize(p)))
                .ToList();

            // Для кластеров ≤ 4 фраз — HAC не запускаем
            if (phrases.Count <= 4)
                return phrases.Select(p => new List<string> { p }).ToList();

            // Шаг 2: собираем все уникальные слова
            var uniqueWords = tokenizedPhrases
                .Where(words => words.Length > 0)
                .SelectMany(words => words)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Если после удаления стоп-слов осталось 0-1 уникальных слов — нечего кластеризовать
            if (uniqueWords.Count <= 1)
                return phrases.Select(p => new List<string> { p }).ToList();

            // Шаг 3: получаем word embeddings
            var wordEmbeddings = await getEmbeddingsAsync(uniqueWords);

            // Шаг 4: IDF weighting
            var idf = ComputeIDF(tokenizedPhrases);

            // Шаг 5: Weighted Soft Jaccard matrix
            int n = phrases.Count;
            var similarityMatrix = new float[n][];
            for (int i = 0; i < n; i++)
                similarityMatrix[i] = new float[n];

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float sim = CalculateWeightedSoftJaccard(
                        tokenizedPhrases[i], tokenizedPhrases[j],
                        wordEmbeddings, idf);
                    similarityMatrix[i][j] = sim;
                    similarityMatrix[j][i] = sim;
                }
            }

            // Шаг 6: HAC
            var clusters = RunHAC(n, similarityMatrix);

            // Преобразуем индексы обратно в фразы
            return clusters
                .Select(cluster => cluster.Select(idx => phrases[idx]).ToList())
                .ToList();
        }

        /// <summary>
        /// Разбивает фразу на слова (нижний регистр, только буквы).
        /// </summary>
        public static string[] Tokenize(string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase))
                return Array.Empty<string>();

            // Разбиваем по всем небуквенным символам, фильтруем пустые
            var words = new System.Text.RegularExpressions.Regex(@"\p{L}+")
                .Matches(phrase.ToLowerInvariant())
                .Select(m => m.Value)
                .ToArray();

            return words;
        }

        /// <summary>
        /// Удаляет стоп-слова из массива слов.
        /// </summary>
        public static string[] RemoveStopWords(string[] words)
        {
            return words
                .Where(w => !StopWords.Contains(w))
                .ToArray();
        }

        /// <summary>
        /// Вычисляет IDF веса для всех слов в кластере.
        /// IDF(w) = ln(N / df(w)) + 0.1
        /// где N — число фраз, df(w) — число фраз, содержащих w.
        /// </summary>
        public static Dictionary<string, float> ComputeIDF(List<string[]> tokenizedPhrases)
        {
            var idf = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            int totalPhrases = tokenizedPhrases.Count;

            if (totalPhrases == 0)
                return idf;

            // Собираем все уникальные слова
            var allWords = tokenizedPhrases
                .Where(words => words.Length > 0)
                .SelectMany(words => words)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var word in allWords)
            {
                // Сколько фраз содержат это слово
                int df = tokenizedPhrases.Count(words => words.Contains(word, StringComparer.OrdinalIgnoreCase));
                if (df > 0)
                {
                    // IDF формула + сглаживание 0.1
                    idf[word] = (float)(Math.Log((double)totalPhrases / df) + 0.1);
                }
                else
                {
                    idf[word] = 0.1f;
                }
            }

            return idf;
        }

        /// <summary>
        /// Вычисляет Weighted Soft Jaccard между двумя фразами.
        ///
        /// intersection = Σ IDF(w_a) * bestSim(w_a, w_b)
        /// где bestSim — cosine similarity между word embeddings,
        /// если bestSim >= wordSimThreshold.
        ///
        /// WeightedJaccard = intersection / (weight(A) + weight(B) - intersection)
        /// </summary>
        public float CalculateWeightedSoftJaccard(
            string[] wordsA,
            string[] wordsB,
            Dictionary<string, float[]> wordEmbeddings,
            Dictionary<string, float> idf)
        {
            // Если хотя бы одна фраза пустая — similarity = 0
            if (wordsA.Length == 0 || wordsB.Length == 0)
                return 0.0f;

            double intersectionWeight = 0.0;

            // Для каждого слова из A ищем лучшее совпадение в B
            foreach (var wordA in wordsA)
            {
                if (!wordEmbeddings.TryGetValue(wordA, out var embA))
                    continue;

                double bestSim = 0.0;

                foreach (var wordB in wordsB)
                {
                    if (!wordEmbeddings.TryGetValue(wordB, out var embB))
                        continue;

                    double sim = CosineSimilarity(embA, embB);
                    if (sim > bestSim)
                        bestSim = sim;
                }

                if (bestSim >= _wordSimThreshold && idf.TryGetValue(wordA, out var weightA))
                {
                    intersectionWeight += weightA * bestSim;
                }
            }

            // Вес первой фразы
            double totalWeightA = 0.0;
            foreach (var w in wordsA)
                if (idf.TryGetValue(w, out var wa))
                    totalWeightA += wa;

            // Вес второй фразы
            double totalWeightB = 0.0;
            foreach (var w in wordsB)
                if (idf.TryGetValue(w, out var wb))
                    totalWeightB += wb;

            // Weighted Jaccard
            double denominator = totalWeightA + totalWeightB - intersectionWeight;
            if (denominator <= 0.0)
                return 0.0f;

            return (float)(intersectionWeight / denominator);
        }

        /// <summary>
        /// Cosine similarity между двумя векторами-эмбеддингами.
        /// </summary>
        public static double CosineSimilarity(float[] a, float[] b)
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
