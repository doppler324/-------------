using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Phase 5: Отбор FAQ-кластеров и привязка к статьям.
    ///
    /// Шаг 1 — AI анализирует финальные кластеры и отбирает те, что не подходят
    /// для отдельной статьи, но подходят как блок FAQ (маленький объём, узкая тема,
    /// вопросно-ответный характер). Ответ — список имён кластеров.
    ///
    /// Шаг 2 — автоматическая привязка по смыслу (БЕЗ AI): для каждого FAQ-кластера
    /// ищется ближайший article-кластер по cosine similarity между representative-векторами
    /// (L2-нормализованный центроид эмбеддингов ключей). Привязка только если
    /// similarity ≥ LinkThreshold.
    ///
    /// Состав кластеров НЕ меняется: FAQ-кластеры остаются в списке статей как есть,
    /// получают метаданные ClusterMeta { IsFaq, LinkedArticle }.
    /// </summary>
    public class FaqSelectionPass
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly OpenRouterSettings _openRouterSettings;
        private readonly Phase5FaqSettings _faqSettings;
        private readonly BusinessSettings? _businessSettings;

        /// <summary>Строка консоли для перезаписываемого прогресса + блокировка записи.</summary>
        private int _progressLine;
        private int _lineWidth;
        private readonly object _consoleLock = new();

        /// <param name="faqSettings">Настройки Phase 5 (провайдер, модель, порог привязки).</param>
        /// <param name="businessSettings">Опционально: ниша/логика — добавляется в системный промпт для контекста.</param>
        public FaqSelectionPass(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            OpenRouterSettings openRouterSettings,
            Phase5FaqSettings faqSettings,
            BusinessSettings? businessSettings = null)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _openRouterSettings = openRouterSettings;
            _faqSettings = faqSettings;
            _businessSettings = businessSettings;
        }

        /// <summary>
        /// Запускает Phase 5: AI-отбор FAQ-кластеров + автоматическая привязка к статьям.
        /// Не модифицирует входной словарь — только возвращает метаданные.
        /// </summary>
        /// <param name="clusters">Финальные кластеры (имя → ключи) после Phase 4/4.5.</param>
        /// <param name="phraseEmbeddings">Эмбеддинги фраз (фраза → вектор) для вычисления representative-векторов.</param>
        /// <returns>Словарь имя кластера → ClusterMeta (IsFaq, LinkedArticle, LinkSimilarity).</returns>
        public async System.Threading.Tasks.Task<Dictionary<string, ClusterMeta>> RunAsync(
            Dictionary<string, List<string>> clusters,
            Dictionary<string, float[]> phraseEmbeddings)
        {
            var meta = new Dictionary<string, ClusterMeta>(StringComparer.OrdinalIgnoreCase);

            if (clusters == null || clusters.Count == 0)
                return meta;

            ConsoleUtils.WriteLine(
                $"\n--- Фаза 5: Отбор FAQ-кластеров и привязка к статьям (порог привязки {_faqSettings.LinkThreshold:F2}) ---",
                ConsoleColor.Cyan);

            // ==========================================
            // Шаг 1: AI-отбор FAQ-кандидатов
            // ==========================================
            var faqNames = await AskFaqClustersAsync(clusters);

            // Валидация имён: только реально существующие кластеры
            var validNames = new HashSet<string>(clusters.Keys, StringComparer.OrdinalIgnoreCase);
            var faqClusters = faqNames
                .Where(n => !string.IsNullOrWhiteSpace(n) && validNames.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (faqNames.Count > faqClusters.Count)
            {
                ConsoleUtils.WriteLine(
                    $"  [WARN] AI вернула {faqNames.Count - faqClusters.Count} названий, которых нет среди кластеров — пропущены.",
                    ConsoleColor.DarkYellow);
            }

            // Помечаем FAQ-кластеры в метаданных
            foreach (var name in faqClusters)
            {
                if (!meta.TryGetValue(name, out var m))
                {
                    m = new ClusterMeta();
                    meta[name] = m;
                }
                m.IsFaq = true;
            }

            if (faqClusters.Count == 0)
            {
                Console.WriteLine("  [FAQ] AI не отобрала кластеры для FAQ (все остаются статьями).");
                ConsoleUtils.WriteLine(
                    $"[Фаза 5] Готово: отобрано 0 FAQ-кластеров.",
                    ConsoleColor.Cyan);
                return meta;
            }

            Console.WriteLine($"  [FAQ] AI отобрала {faqClusters.Count} из {clusters.Count} кластеров как FAQ-блоки.");

            // ==========================================
            // Шаг 2: Автоматическая привязка FAQ → статья (по смыслу, без AI)
            // ==========================================
            LinkFaqClusters(clusters, phraseEmbeddings, faqClusters, meta);

            int linkedCount = meta.Values.Count(v => v.IsFaq && !string.IsNullOrWhiteSpace(v.LinkedArticle));
            ConsoleUtils.WriteLine(
                $"[Фаза 5] Готово: отобрано {faqClusters.Count} FAQ-кластеров, привязано к статьям: {linkedCount}.",
                ConsoleColor.Cyan);

            return meta;
        }

        /// <summary>
        /// Шаг 1: AI определяет для КАЖДОГО кластера — статья или FAQ.
        /// В нейросеть отправляются только НАЗВАНИЯ кластеров (без ключей).
        /// Список разбивается на батчи (BatchSize) — каждый батч отправляется отдельным
        /// запросом, чтобы не генерировать гигантский JSON сразу на все кластеры.
        /// Возвращает список названий кластеров, отнесённых к FAQ (без валидации — она в вызывающем коде).
        /// </summary>
        private async System.Threading.Tasks.Task<List<string>> AskFaqClustersAsync(
            Dictionary<string, List<string>> clusters)
        {
            var systemPrompt = LoadInstruction("instructions/phase5_faq_selection.txt");

            // Бизнес-контекст (ниша/логика), если задан
            if (_businessSettings != null)
            {
                systemPrompt += $"\nНиша сайта: {_businessSettings.Niche}. Логика кластеризации: {_businessSettings.ClusteringLogic}.";
            }

            var allNames = clusters.Keys.ToList();
            int batchSize = Math.Max(1, _faqSettings.BatchSize);
            int batchCount = (int)Math.Ceiling(allNames.Count / (double)batchSize);

            ConsoleUtils.WriteLine(
                $"  [AI] Кластеров: {allNames.Count}. Разбиваю на {batchCount} батчей (по {batchSize}).",
                ConsoleColor.DarkGray);

            // Инициализируем строку прогресса ПЕРЕД запросом (иначе WriteProgress пишет на строку 0)
            _progressLine = Console.CursorTop;
            _lineWidth = Console.WindowWidth - 1;

            var allDecisions = new List<Phase5FaqClusterDecision>();
            int unresolvedTotal = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int b = 0; b < batchCount; b++)
            {
                var batch = allNames
                    .Skip(b * batchSize)
                    .Take(batchSize)
                    .ToList();

                // Формируем вход: нумерованные названия кластеров текущего батча
                var lines = new List<string>
                {
                    $"Батч {b + 1}/{batchCount} — список кластеров (каждый — статья или FAQ-блок):",
                    ""
                };
                for (int i = 0; i < batch.Count; i++)
                    lines.Add($"{i + 1}. {batch[i]}");

                string userMessage = string.Join("\n", lines);

                WriteProgress($"  [AI] Батч {b + 1}/{batchCount}: отправляю {batch.Count} названий, ждём ответа нейросети...");

                var (response, error) = await DeepSeekHelper.SendWithRetryAsync<Phase5FaqResponse>(
                    _client, systemPrompt, userMessage, BuildConfig(),
                    maxRetries: 3, baseDelayMs: 5000,
                    endpoint: Endpoint, apiKeyOverride: ApiKeyOverride, skipDeepSeekFields: UseOpenRouter);

                if (response == null || response.Clusters == null || response.Clusters.Count == 0)
                {
                    ConsoleUtils.WriteLine(
                        $"  [AI] Батч {b + 1}/{batchCount}: ошибка ({DeepSeekHelper.DescribeError(error)}). Его кластеры остаются статьями.",
                        ConsoleColor.Yellow);
                    continue;
                }

                allDecisions.AddRange(response.Clusters);
                unresolvedTotal += response.Clusters.Count(c =>
                    !string.Equals(c.Type, "article", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(c.Type, "faq", StringComparison.OrdinalIgnoreCase));
            }

            stopwatch.Stop();
            ClearProgressLine();

            if (allDecisions.Count == 0)
            {
                ConsoleUtils.WriteLine(
                    "  [AI] Не получено ни одного решения. Все кластеры остаются статьями.",
                    ConsoleColor.Yellow);
                return new List<string>();
            }

            // Подробный статус: сколько статья / сколько FAQ
            int articleCount = allDecisions.Count(c =>
                string.Equals(c.Type, "article", StringComparison.OrdinalIgnoreCase));
            int faqCount = allDecisions.Count(c =>
                string.Equals(c.Type, "faq", StringComparison.OrdinalIgnoreCase));

            ConsoleUtils.WriteLine(
                $"  [AI] Ответы получены за {stopwatch.Elapsed.TotalSeconds:F1}с: статья — {articleCount}, FAQ — {faqCount}, не распознано — {unresolvedTotal}.",
                ConsoleColor.DarkGray);

            // Возвращаем названия кластеров, отнесённых к FAQ
            return allDecisions
                .Where(c => string.Equals(c.Type, "faq", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => c.Name.Trim())
                .ToList();
        }

        /// <summary>
        /// Шаг 2: для каждого FAQ-кластера ищет ближайшую статью по cosine similarity
        /// representative-векторов и записывает привязку в meta.
        /// </summary>
        private void LinkFaqClusters(
            Dictionary<string, List<string>> clusters,
            Dictionary<string, float[]> phraseEmbeddings,
            List<string> faqClusters,
            Dictionary<string, ClusterMeta> meta)
        {
            // Статьи = все кластеры, кроме FAQ. Representative-вектор для каждой.
            var articleVectors = new List<(string Name, float[] Vector)>();
            foreach (var kvp in clusters)
            {
                if (faqClusters.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                    continue;

                var vector = ComputeRepresentativeVector(kvp.Value, phraseEmbeddings);
                if (vector != null && vector.Length > 0)
                    articleVectors.Add((kvp.Key, vector));
            }

            // Показываем прогресс привязки (single-line, перезапись строки)
            _progressLine = Console.CursorTop;
            _lineWidth = Console.WindowWidth - 1;

            int linked = 0;
            foreach (var faqName in faqClusters)
            {
                if (!clusters.TryGetValue(faqName, out var keywords))
                    continue;

                var faqVector = ComputeRepresentativeVector(keywords, phraseEmbeddings);

                if (faqVector == null || faqVector.Length == 0 || articleVectors.Count == 0)
                {
                    WriteProgress($"  [Link] «{faqName}» — нет representative-вектора/статей, привязка не выполнена.");
                    continue;
                }

                // Ищем ближайшую статью
                string bestArticle = "";
                double bestSim = -1.0;
                foreach (var (articleName, articleVector) in articleVectors)
                {
                    double sim = CosineSimilarity(faqVector, articleVector);
                    if (sim > bestSim)
                    {
                        bestSim = sim;
                        bestArticle = articleName;
                    }
                }

                if (bestSim >= _faqSettings.LinkThreshold)
                {
                    meta[faqName].LinkedArticle = bestArticle;
                    meta[faqName].LinkSimilarity = bestSim;
                    linked++;
                    WriteProgress($"  [Link] «{faqName}» → «{bestArticle}» (cos={bestSim:F2}).");
                }
                else
                {
                    WriteProgress($"  [Link] «{faqName}» — лучший кандидат «{bestArticle}» (cos={bestSim:F2}) ниже порога, без привязки.");
                }
            }

            ClearProgressLine();

            if (linked == 0)
                Console.WriteLine("  [Link] Не удалось привязать ни один FAQ-кластер к статьям.");
            else
                Console.WriteLine($"  [Link] Привязано к статьям: {linked} из {faqClusters.Count}.");
        }

        /// <summary>
        /// Вычисляет representative-вектор кластера: L2-нормализованный центроид
        /// эмбеддингов ключей. Фразы без эмбеддинга пропускаются.
        /// </summary>
        private static float[]? ComputeRepresentativeVector(
            List<string> keywords, Dictionary<string, float[]> phraseEmbeddings)
        {
            if (keywords == null || keywords.Count == 0)
                return null;

            var vectors = new List<float[]>();
            foreach (var phrase in keywords)
            {
                if (phraseEmbeddings.TryGetValue(phrase, out var emb) && emb != null && emb.Length > 0)
                    vectors.Add(emb);
            }

            if (vectors.Count == 0)
                return null;

            return ClusterMath.CalculateNormalizedCentroid(vectors);
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

        /// <summary>Загружает содержимое файла инструкции. При отсутствии — возвращает базовую заглушку.</summary>
        private static string LoadInstruction(string filePath)
        {
            if (File.Exists(filePath))
                return File.ReadAllText(filePath).Trim();

            ConsoleUtils.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл '{filePath}' не найден.", ConsoleColor.Yellow);
            return "Верни ответ строго в формате JSON. Никакого текста до или после JSON.";
        }

        /// <summary>true, если выбран OpenRouter (провайдер из настроек Phase 5).</summary>
        private bool UseOpenRouter =>
            _faqSettings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase);

        /// <summary>Endpoint для OpenRouter, иначе null (по умолчанию DeepSeek).</summary>
        private string? Endpoint => UseOpenRouter ? "https://openrouter.ai/api/v1/chat/completions" : null;

        /// <summary>API-ключ для OpenRouter, иначе null (используется ключ DeepSeek).</summary>
        private string? ApiKeyOverride => UseOpenRouter ? _openRouterSettings.ApiKey : null;

        /// <summary>
        /// Собирает DeepSeekSettings для вызова AI из настроек Phase 5,
        /// подставляя значения из phase4/deepseek, где не заданы свои.
        /// Если thinking выключен — reasoningEffort принудительно ставится "low",
        /// чтобы не тратить время на долгие рассуждения (high при выключенном thinking — бессмысленно).
        /// </summary>
        private DeepSeekSettings BuildConfig()
        {
            bool thinking = _faqSettings.EnableThinking ?? _deepSeekSettings.EnableThinking;
            string reasoningEffort = _faqSettings.ReasoningEffort ?? _deepSeekSettings.ReasoningEffort;

            // Уважаем настройку: выключен thinking → не заставляем модель долго думать
            if (!thinking)
                reasoningEffort = "low";

            return new DeepSeekSettings
            {
                ApiKey = _deepSeekSettings.ApiKey,
                Model = !string.IsNullOrEmpty(_faqSettings.Model)
                    ? _faqSettings.Model : _deepSeekSettings.Model,
                Temperature = _faqSettings.Temperature ?? _deepSeekSettings.Temperature,
                MaxTokens = _faqSettings.MaxTokens ?? _deepSeekSettings.MaxTokens,
                TopP = _deepSeekSettings.TopP,
                EnableThinking = thinking,
                ReasoningEffort = reasoningEffort,
                Stream = _faqSettings.Stream ?? _deepSeekSettings.Stream
            };
        }

        /// <summary>Перезаписывает строку прогресса в консоли (не потоком, потокобезопасно).</summary>
        private void WriteProgress(string message)
        {
            lock (_consoleLock)
            {
                try
                {
                    Console.SetCursorPosition(0, _progressLine);
                    Console.Write(message.PadRight(_lineWidth).Substring(0, _lineWidth));
                }
                catch (IOException)
                {
                    Console.Write($"\n{message}");
                }
            }
        }

        /// <summary>Стирает строку прогресса перед итоговым выводом.</summary>
        private void ClearProgressLine()
        {
            lock (_consoleLock)
            {
                try
                {
                    Console.SetCursorPosition(0, _progressLine);
                    Console.Write(new string(' ', _lineWidth));
                    Console.SetCursorPosition(0, _progressLine);
                }
                catch (IOException)
                {
                    Console.WriteLine();
                }
            }
        }
    }
}
