using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;
using KeywordClusterizer.Services;

namespace KeywordClusterizer
{
    /// <summary>
    /// Управляет SERP-first пайплайном кластеризации:
    /// 1. Сбор SERP (XmlRiver + кэш).
    /// 2. Граф интентов (Connected Components по URL).
    /// 3. Word-level кластеризация (IDF + Weighted Soft Jaccard + HAC).
    /// 4. AI Merge + Naming (единый DeepSeek/OpenRouter call).
    /// </summary>
    public class ClusterizationPipeline
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly BusinessSettings _businessSettings;
        private readonly XmlRiverSettings _serpSettings;
        private readonly OpenRouterSettings _openRouterSettings;
        private readonly Phase4Settings _phase4Settings;
        private readonly XmlRiverClient? _xmlRiverClient;
        private readonly SerpCacheService? _cacheService;
        private readonly OpenRouterEmbeddingClient? _embeddingClient;
        private const string UnclusteredKey = "Нераспределённые";

        public ClusterizationPipeline(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            BusinessSettings businessSettings,
            XmlRiverSettings serpSettings,
            OpenRouterSettings openRouterSettings,
            Phase4Settings? phase4Settings = null)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _businessSettings = businessSettings;
            _serpSettings = serpSettings;
            _openRouterSettings = openRouterSettings;
            _phase4Settings = phase4Settings ?? new Phase4Settings();

            // Инициализируем SERP-клиент только если есть учётные данные
            if (!string.IsNullOrWhiteSpace(_serpSettings.XmlriverUser) &&
                !string.IsNullOrWhiteSpace(_serpSettings.XmlriverKey))
            {
                if (_serpSettings.EnableCache)
                    _cacheService = new SerpCacheService(_serpSettings.CachePath);
                _xmlRiverClient = new XmlRiverClient(client, _serpSettings, _cacheService);
            }

            // Инициализируем клиент эмбеддингов
            if (!string.IsNullOrWhiteSpace(_openRouterSettings.ApiKey))
            {
                _embeddingClient = new OpenRouterEmbeddingClient(client, _openRouterSettings);
            }
        }

        public async Task<Dictionary<string, List<string>>?> RunAsync(List<string> keywords)
        {
            if (_xmlRiverClient == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] XmlRiver не настроен.");
                Console.ResetColor();
                return null;
            }
            return await RunSerpFirstAsync(keywords);
        }

        private async Task<Dictionary<string, List<string>>?> RunSerpFirstAsync(List<string> keywords)
        {
            int maxClusterSize = _businessSettings.ParseMaxClusterSize();
            Console.WriteLine($"\n=== SERP-First пайплайн ===");
            Console.WriteLine($"Ключей: {keywords.Count}");

            // ==========================================
            // Фаза 1: Сбор SERP
            // ==========================================
            Console.WriteLine($"\n--- Фаза 1: Сбор SERP ({keywords.Count} ключей) ---");
            var serpData = await _xmlRiverClient!.SearchBatchAsync(
                keywords, _serpSettings.MaxConcurrency, _serpSettings.TopResultsCount);

            if (serpData.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] SERP-данные не получены.");
                Console.ResetColor();
                return null;
            }

            // ==========================================
            // Фаза 2: Граф интентов (Connected Components)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 2: Граф интентов ---");
            var graphClusterizer = new SerpGraphClusterizer(
                _serpSettings.OverlapThreshold, _serpSettings.TopResultsCount);
            var (serpClusters, serpUnclustered) = graphClusterizer.Clusterize(serpData);

            if (_cacheService != null)
                _cacheService.Save();

            // ==========================================
            // Фаза 2.5: Rescue Pass
            // ==========================================
            Console.WriteLine($"\n--- Фаза 2.5: Rescue Pass ---");
            RescuePass(serpClusters, serpUnclustered, serpData);

            // ==========================================
            // Фаза 3: Word-level кластеризация
            // ==========================================
            Console.WriteLine($"\n--- Фаза 3: Word-level кластеризация (IDF + Weighted Jaccard) ---");

            var finalClusters = new List<List<string>>();
            var wordLevelClusterizer = new WordLevelClusterizer(
                _businessSettings.WordSimThreshold, _businessSettings.HacThreshold);

            foreach (var cluster in serpClusters)
            {
                if (cluster.Count <= 1)
                {
                    finalClusters.Add(cluster);
                    continue;
                }

                int beforeSplit = finalClusters.Count;
                var subClusters = await wordLevelClusterizer.ClusterizeAsync(
                    cluster,
                    async (words) => await _embeddingClient!.GetEmbeddingsBatchAsync(words));

                finalClusters.AddRange(subClusters);

                int afterSplit = finalClusters.Count;
                Console.WriteLine($"  → {cluster.Count} → {afterSplit - beforeSplit} подкластеров (word-level)");
            }

            _embeddingClient?.SaveCache();
            Console.WriteLine($"  После Phase 3: {finalClusters.Count} кластеров.");

            // ==========================================
            // Фаза 4: AI Merge + Naming (единый call)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 4: AI Merge + Naming ---");

            var namedClusters = new Dictionary<string, List<string>>();
            var allUnclustered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in serpUnclustered)
                allUnclustered.Add(key);

            if (_businessSettings.SkipNaming)
            {
                Console.WriteLine("  Пропуск AI-обработки (skipNaming=true).");
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
                // Формируем входные данные: нумерованные кластеры с ключами
                var clusterLines = new List<string>();
                for (int i = 0; i < finalClusters.Count; i++)
                {
                    clusterLines.Add($"Кластер {i + 1}:");
                    foreach (var key in finalClusters[i])
                        clusterLines.Add($"- {key}");
                    clusterLines.Add("");
                }

                string userMessage = string.Join("\n", clusterLines);
                string instructionText = LoadInstruction("instructions/phase4_ai_merge.txt");
                string systemPrompt = string.IsNullOrEmpty(instructionText)
                    ? "Верни JSON с объединёнными кластерами."
                    : instructionText;

                // Выбор провайдера
                bool useOpenRouter = _phase4Settings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase);
                string? endpoint = useOpenRouter ? "https://openrouter.ai/api/v1/chat/completions" : null;
                string? apiKeyOverride = useOpenRouter ? _openRouterSettings.ApiKey : null;

                var phase4Config = new DeepSeekSettings
                {
                    ApiKey = _deepSeekSettings.ApiKey,
                    Model = !string.IsNullOrEmpty(_phase4Settings.Model)
                        ? _phase4Settings.Model : _deepSeekSettings.Model,
                    Temperature = _phase4Settings.Temperature ?? _deepSeekSettings.Temperature,
                    MaxTokens = _phase4Settings.MaxTokens ?? _deepSeekSettings.MaxTokens,
                    TopP = _deepSeekSettings.TopP,
                    EnableThinking = _phase4Settings.EnableThinking ?? _deepSeekSettings.EnableThinking,
                    ReasoningEffort = _phase4Settings.ReasoningEffort ?? _deepSeekSettings.ReasoningEffort,
                    Stream = _phase4Settings.Stream ?? _deepSeekSettings.Stream
                };

                if (useOpenRouter)
                    Console.WriteLine($"  Провайдер: OpenRouter, модель: {phase4Config.Model}");

                var (rawJson, _) = await DeepSeekHelper.GetRawAiContentAsync(
                    _client, systemPrompt, userMessage, phase4Config,
                    overrideThinking: true,
                    overrideReasoningEffort: "high",
                    endpoint: endpoint,
                    apiKeyOverride: apiKeyOverride,
                    skipDeepSeekFields: useOpenRouter);

                bool parsed = false;
                if (!string.IsNullOrEmpty(rawJson))
                {
                    // Формат 1: { "seo_articles": [...] }
                    try
                    {
                        var strict = JsonSerializer.Deserialize<SeoArticleResponse>(rawJson);
                        if (strict?.SeoArticles != null && strict.SeoArticles.Count > 0)
                        {
                            foreach (var article in strict.SeoArticles)
                                namedClusters[article.H1Title] = article.Keywords;
                            parsed = true;
                        }
                    }
                    catch { }

                    // Формат 2: [{ "name":..., "keywords":[...] }] (Gemini)
                    if (!parsed)
                    {
                        try
                        {
                            var geminiArticles = JsonSerializer.Deserialize<List<GeminiArticle>>(rawJson);
                            if (geminiArticles != null && geminiArticles.Count > 0)
                            {
                                foreach (var article in geminiArticles)
                                    if (!string.IsNullOrEmpty(article.Name) && article.Keywords?.Count > 0)
                                        namedClusters[article.Name] = article.Keywords;
                                parsed = true;
                            }
                        }
                        catch { }
                    }

                    // Формат 3: [{ "Название": [ключи] }]
                    if (!parsed)
                    {
                        try
                        {
                            var flatArticles = JsonSerializer.Deserialize<List<Dictionary<string, List<string>>>>(rawJson);
                            if (flatArticles != null && flatArticles.Count > 0)
                            {
                                foreach (var article in flatArticles)
                                    foreach (var kvp in article)
                                        namedClusters[kvp.Key] = kvp.Value;
                                parsed = true;
                            }
                        }
                        catch { }
                    }
                }

                if (!parsed)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  [ОШИБКА] AI не вернул статьи. Использую исходные кластеры.");
                    Console.ResetColor();
                    int idx = 0;
                    foreach (var cluster in finalClusters)
                    {
                        idx++;
                        string name = cluster.Count > 1 ? $"Кластер {idx}" : cluster[0];
                        namedClusters[name] = cluster;
                    }
                }
            }

            if (allUnclustered.Count > 0)
            {
                namedClusters[UnclusteredKey] = allUnclustered.ToList();
                Console.WriteLine($"  Нераспределённых ключей: {allUnclustered.Count}");
            }

            int totalKeys = namedClusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nИтого: {namedClusters.Count} кластеров, {totalKeys} ключей.");
            return namedClusters;
        }

        /// <summary>
        /// Rescue Pass: прикрепляет unclustered ключи к ближайшему кластеру по пересечению URL.
        /// </summary>
        private void RescuePass(
            List<List<string>> clusters,
            List<string> unclustered,
            Dictionary<string, KeywordSearchResult> serpData)
        {
            if (unclustered.Count == 0) return;

            Console.WriteLine($"  [Rescue] Спасение {unclustered.Count} сирот...");
            int rescued = 0;
            var remaining = new List<string>();

            foreach (var orphan in unclustered)
            {
                if (!serpData.TryGetValue(orphan, out var sr) || sr.Urls.Count == 0)
                {
                    remaining.Add(orphan);
                    continue;
                }

                var orphanUrls = new HashSet<string>(sr.Urls, StringComparer.OrdinalIgnoreCase);
                (int overlap, List<string> cluster)? best = null;

                foreach (var cluster in clusters)
                {
                    foreach (var key in cluster)
                    {
                        if (!serpData.TryGetValue(key, out var csr)) continue;
                        int overlap = csr.Urls.Count(u => orphanUrls.Contains(u));
                        if (overlap >= 1 && (best == null || overlap > best.Value.overlap))
                            best = (overlap, cluster);
                        if (overlap >= _serpSettings.OverlapThreshold)
                            goto Attach;
                    }
                }

                if (best != null)
                {
                    best.Value.cluster.Add(orphan);
                    rescued++;
                }
                else
                {
                    remaining.Add(orphan);
                }
                Attach:;
            }

            Console.WriteLine($"  [Rescue] Спасено: {rescued}, не удалось: {remaining.Count}");
            unclustered.Clear();
            unclustered.AddRange(remaining);
        }

        /// <summary>
        /// Загружает содержимое файла инструкции.
        /// </summary>
        private static string LoadInstruction(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл '{filePath}' не найден.");
                Console.ResetColor();
                return "";
            }
            return File.ReadAllText(filePath).Trim();
        }
    }
}
