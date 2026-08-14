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
        private readonly Phase4CleanSettings _phase4CleanSettings;
        private readonly Phase5FaqSettings _phase5FaqSettings;
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
            Phase4Settings? phase4Settings = null,
            Phase4CleanSettings? phase4CleanSettings = null,
            Phase5FaqSettings? phase5FaqSettings = null)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _businessSettings = businessSettings;
            _serpSettings = serpSettings;
            _openRouterSettings = openRouterSettings;
            _phase4Settings = phase4Settings ?? new Phase4Settings();
            _phase4CleanSettings = phase4CleanSettings ?? new Phase4CleanSettings();
            _phase5FaqSettings = phase5FaqSettings ?? new Phase5FaqSettings();

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

        public async Task<Models.ClusteringResult?> RunAsync(
            List<string> keywords, string? resumeFromPhase = null)
        {
            // Если запрошено возобновление с чекпойнта — пропускаем фазы 1-3.6 и стартуем с нужной точки
            if (!string.IsNullOrWhiteSpace(resumeFromPhase))
            {
                return await ResumeFromCheckpointAsync(resumeFromPhase);
            }

            if (_xmlRiverClient == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] XmlRiver не настроен.");
                Console.ResetColor();
                return null;
            }
            return await RunSerpFirstAsync(keywords);
        }

        private async Task<Models.ClusteringResult?> RunSerpFirstAsync(List<string> keywords)
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
            // Фаза 3: Sentence-level кластеризация
            // ==========================================
            Console.WriteLine($"\n--- Фаза 3: Sentence-level кластеризация (cosine similarity + HAC) ---");

            var finalClusters = new List<List<string>>();
            var macroCores = new List<Models.MacroBucket>();
            var sentenceLevelClusterizer = new SentenceLevelClusterizer(
                _businessSettings.SentenceHacThreshold);

            // Эмбеддинги фраз — заполняются в Phase 3, нужны для Phase 5 (привязка FAQ к статьям)
            Dictionary<string, float[]>? phraseEmbeddings = null;

            // Проверка API-ключа OpenRouter перед началом
            if (_embeddingClient == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [ОШИБКА] OpenRouterEmbeddingClient не инициализирован (нет apiKey). Пропуск Phase 3.");
                Console.ResetColor();
                finalClusters.AddRange(serpClusters);
            }
            else
            {
                // Собираем все фразы из SERP-кластеров
                var allSerpPhrases = serpClusters
                    .Where(c => c.Count > 0)
                    .SelectMany(c => c)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Проверяем кэш: какие фразы уже есть, какие нужно запросить
                var uncached = _embeddingClient.GetMissingFromCache(allSerpPhrases);
                bool needApiRequest = uncached.Count > 0;
                bool apiAvailable = false;

                if (needApiRequest)
                {
                    Console.WriteLine($"  [Embed] В кэше {allSerpPhrases.Count - uncached.Count}/{allSerpPhrases.Count}. Нужно запросить {uncached.Count}.");

                    apiAvailable = await TestEmbeddingApiAsync();
                    if (!apiAvailable)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("  [ОШИБКА] API-ключ OpenRouter не работает. Пропуск Phase 3.");
                        Console.ResetColor();
                        finalClusters.AddRange(serpClusters);
                    }
                }
                else
                {
                    Console.WriteLine($"  [Embed] Все {allSerpPhrases.Count} фраз уже в кэше. API-запрос не нужен.");
                    apiAvailable = true; // кэша достаточно, API не нужен
                }

                // Загружаем эмбеддинги (из кэша + если API доступен — дозапрашиваем)
                if (!needApiRequest || apiAvailable)
                {
                    Console.WriteLine($"  [Embed] Загрузка {allSerpPhrases.Count} эмбеддингов...");
                    phraseEmbeddings = await _embeddingClient.GetEmbeddingsBatchAsync(allSerpPhrases);

                    foreach (var cluster in serpClusters)
                    {
                        if (cluster.Count <= 1)
                        {
                            finalClusters.Add(cluster);
                            continue;
                        }

                        int beforeSplit = finalClusters.Count;
                        var subClusters = await sentenceLevelClusterizer.ClusterizeAsync(
                            cluster,
                            // Используем уже загруженные эмбеддинги
                            (phrases) => System.Threading.Tasks.Task.FromResult(
                                phrases.Where(p => phraseEmbeddings.ContainsKey(p))
                                    .ToDictionary(p => p, p => phraseEmbeddings[p])));

                        finalClusters.AddRange(subClusters);

                        int afterSplit = finalClusters.Count;
                        int sLevelWidth = Console.WindowWidth - 1;
                        Console.SetCursorPosition(0, Console.CursorTop);
                        Console.Write($"  → {cluster.Count} → {afterSplit - beforeSplit} подкластеров (sentence-level)".PadRight(sLevelWidth).Substring(0, sLevelWidth));
                    }

                    // Стираем последнюю строку прогресса перед итогом
                    int clearW = Console.WindowWidth - 1;
                    Console.SetCursorPosition(0, Console.CursorTop);
                    Console.Write(new string(' ', clearW));
                    Console.SetCursorPosition(0, Console.CursorTop);

                    _embeddingClient.SaveCache();

                    // Отделяем одиночек (1 фраза) — они пойдут в Rescue Pass
                    var orphans = new List<string>();
                    var multiClusters = new List<List<string>>();
                    foreach (var c in finalClusters)
                    {
                        if (c.Count == 1)
                            orphans.Add(c[0]);
                        else
                            multiClusters.Add(c);
                    }

                    Console.WriteLine($"  После Phase 3: {finalClusters.Count} микро-кластеров (одиночек: {orphans.Count}).");

                    // ==========================================
                    // Фаза 3.5: Macro Merge (Greedy, representativeMode)
                    // ==========================================
                    if (_businessSettings.MacroMergeEnabled && multiClusters.Count > 0)
                    {
                        string modeLabel = _businessSettings.RepresentativeMode == "centroid" ? "centroid" : "medoid";
                        Console.WriteLine($"\n--- Фаза 3.5: Macro Merge ({modeLabel}, порог {_businessSettings.MacroMergeThreshold:F2}) ---");
                        var macroMerge = new Services.MacroMergePass(
                            _businessSettings.MacroMergeThreshold,
                            _businessSettings.RepresentativeMode);

                        macroCores = await macroMerge.MergeAsync(
                            multiClusters,
                            phraseEmbeddings);

                        Console.WriteLine($"  После Phase 3.5: {macroCores.Count} макро-бакетов.");
                    }
                    else
                    {
                        // Если MacroMerge отключён — все мульти-кластеры становятся ядрами
                        macroCores = multiClusters.Select(c => {
                            string medoid = c.Count <= 2 ? c[0] : c[0];
                            float[] rep = c.Count > 0 && phraseEmbeddings.TryGetValue(c[0], out var e) ? e : Array.Empty<float>();
                            return new Models.MacroBucket { Name = medoid, Keywords = c.ToList(), RepresentativeVector = rep };
                        }).ToList();
                    }

                    // ==========================================
                    // Фаза 3.6: Rescue Pass V2 (Nearest Centroid)
                    // ==========================================
                    if (_businessSettings.RescuePassV2Enabled && macroCores.Count > 0)
                    {
                        // Добавляем serpUnclustered к сиротам
                        orphans.AddRange(serpUnclustered);

                        Console.WriteLine($"\n--- Фаза 3.6: Rescue Pass V2 (Nearest Centroid, порог {_businessSettings.RescueThreshold:F2}) ---");
                        Console.WriteLine($"  Сирот: {orphans.Count}, ядер: {macroCores.Count}");

                        var rescue = new Services.RescuePassV2(_businessSettings.RescueThreshold, _businessSettings.SentenceHacThreshold);
                        var trueUnclustered = rescue.RescueOrphans(macroCores, orphans, phraseEmbeddings);

                        if (trueUnclustered.Count > 0)
                        {
                            macroCores.Add(new Models.MacroBucket
                            {
                                Name = "Нераспределённые",
                                Keywords = trueUnclustered
                            });
                            Console.WriteLine($"  Создан кластер \"Нераспределённые\": {trueUnclustered.Count} ключей.");
                        }
                    }
                }
            }

            // ==========================================
            // Фаза 4: AI Merge + Naming (единый call)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 4: AI Merge + Naming ---");

            var namedClusters = new Dictionary<string, List<string>>();
            var allUnclustered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Phase 4 работает с macroCores (результат Phase 3.5 + Phase 3.6)
            var phase4Buckets = macroCores.Count > 0 ? macroCores
                : finalClusters.Select(c => new Models.MacroBucket { Name = c.Count > 1 ? c[0] : c[0], Keywords = c.ToList() }).ToList();

            // Если Phase 4 полностью пропущена
            if (_businessSettings.SkipPhase4)
            {
                Console.WriteLine($"  Фаза 4 пропущена (skipPhase4=true). Имена бакетов = медоиды.");
                foreach (var bucket in phase4Buckets)
                    namedClusters[bucket.Name] = bucket.Keywords;
            }
            else if (_businessSettings.SkipNaming)
            {
                Console.WriteLine("  Пропуск AI-обработки (skipNaming=true). Имена бакетов = медоиды.");
                foreach (var bucket in phase4Buckets)
                {
                    // Имя бакета = медоид ядра (реальная фраза), чтобы названия были осмысленными,
                    // а не «Кластер N». Если Name пуст — fallback на первый ключ.
                    string name = string.IsNullOrWhiteSpace(bucket.Name)
                        ? (bucket.Keywords.Count > 0 ? bucket.Keywords[0] : $"Кластер {namedClusters.Count + 1}")
                        : bucket.Name;
                    namedClusters[name] = bucket.Keywords;
                }
            }
            else if (_businessSettings.SkipMerge)
            {
                // Режим "только naming": AI придумывает H1-заголовки, не меняя состав
                Console.WriteLine("  Режим: только naming (skipMerge=true).");

                // Передаём AI только несколько примеров ключей из каждого кластера (для контекста),
                // AI возвращает только названия, ключи берём из исходных phase4Buckets
                int sampleSize = Math.Min(3, phase4Buckets.Min(b => b.Keywords.Count));
                var namingLines = new List<string>();
                int progressLine = Console.CursorTop;
                int lineWidth = Console.WindowWidth - 1;
                for (int i = 0; i < phase4Buckets.Count; i++)
                {
                    Console.SetCursorPosition(0, progressLine);
                    Console.Write(($"  [Progress] {i + 1}/{phase4Buckets.Count} — \"{phase4Buckets[i].Name}\"").PadRight(lineWidth).Substring(0, lineWidth));
                    namingLines.Add($"Кластер {i + 1}:");
                    // Только первые sampleSize ключей для контекста
                    var samples = phase4Buckets[i].Keywords.Take(sampleSize).ToList();
                    foreach (var key in samples)
                        namingLines.Add($"- {key}");
                    if (phase4Buckets[i].Keywords.Count > sampleSize)
                        namingLines.Add($"- ... и ещё {phase4Buckets[i].Keywords.Count - sampleSize} ключей");
                    namingLines.Add("");
                }
                Console.WriteLine();

                string userMessage = string.Join("\n", namingLines);
                string systemPrompt = "Ты SEO-специалист. Каждому кластеру присвой H1-заголовок для будущей статьи. "
                    + $"Всего кластеров: {phase4Buckets.Count}. "
                    + "Верни JSON-объект, где ключ — номер кластера, а значение — H1-заголовок: "
                    + "{\"1\": \"H1-заголовок для кластера 1\", \"2\": \"H1-заголовок для кластера 2\", ...}. "
                    + "НЕ добавляй ключевые слова в ответ, только названия. "
                    + $"Ниша: {_businessSettings.Niche}. Логика: {_businessSettings.ClusteringLogic}.";

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

                Console.Write("  [AI] Ожидание ответа нейросети... ");
                var (rawJson, _) = await DeepSeekHelper.GetRawAiContentAsync(
                    _client, systemPrompt, userMessage, phase4Config,
                    overrideThinking: true,
                    overrideReasoningEffort: "high",
                    endpoint: endpoint,
                    apiKeyOverride: apiKeyOverride,
                    skipDeepSeekFields: useOpenRouter);
                Console.WriteLine("Готово.");

                // Парсим ответ: ожидаем {"1": "Название 1", "2": "Название 2", ...}
                string cleanJson = rawJson?.Trim() ?? "";
                if (cleanJson.StartsWith("```"))
                {
                    int start = cleanJson.IndexOf('\n');
                    int end = cleanJson.LastIndexOf("```");
                    if (start > 0 && end > start)
                        cleanJson = cleanJson[(start + 1)..end].Trim();
                }

                bool parsed = false;
                if (!string.IsNullOrEmpty(cleanJson))
                {
                    try
                    {
                        var nameMap = JsonSerializer.Deserialize<Dictionary<string, string>>(cleanJson);
                        if (nameMap != null && nameMap.Count > 0)
                        {
                            int mappedCount = 0;
                            for (int i = 0; i < phase4Buckets.Count; i++)
                            {
                                string key = (i + 1).ToString();
                                if (nameMap.TryGetValue(key, out var aiName) && !string.IsNullOrWhiteSpace(aiName))
                                {
                                    namedClusters[aiName.Trim()] = phase4Buckets[i].Keywords;
                                    mappedCount++;
                                }
                                else
                                {
                                    namedClusters[phase4Buckets[i].Name] = phase4Buckets[i].Keywords;
                                }
                            }
                            parsed = true;

                            if (mappedCount < phase4Buckets.Count)
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"  [WARN] AI вернул названия для {mappedCount} из {phase4Buckets.Count} кластеров. " +
                                    $"Остальные — с именами по умолчанию.");
                                Console.ResetColor();
                            }
                        }
                    }
                    catch { }
                }

                if (!parsed)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  [ОШИБКА] AI не вернул именование. Использую исходные имена.");
                    Console.ResetColor();
                    foreach (var bucket in phase4Buckets)
                        namedClusters[bucket.Name] = bucket.Keywords;
                }
            }
            else
            {
                // Формируем входные данные: нумерованные кластеры с ключами
                var clusterLines = new List<string>();
                int progressLine = Console.CursorTop;
                int lineWidth = Console.WindowWidth - 1;
                for (int i = 0; i < phase4Buckets.Count; i++)
                {
                    Console.SetCursorPosition(0, progressLine);
                    Console.Write(($"  [Progress] {i + 1}/{phase4Buckets.Count} — \"{phase4Buckets[i].Name}\"").PadRight(lineWidth).Substring(0, lineWidth));
                    clusterLines.Add($"Кластер {i + 1}:");
                    foreach (var key in phase4Buckets[i].Keywords)
                        clusterLines.Add($"- {key}");
                    clusterLines.Add("");
                }
                Console.WriteLine();

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

                Console.Write("  [AI] Ожидание ответа нейросети... ");
                var (rawJson, _) = await DeepSeekHelper.GetRawAiContentAsync(
                    _client, systemPrompt, userMessage, phase4Config,
                    overrideThinking: true,
                    overrideReasoningEffort: "high",
                    endpoint: endpoint,
                    apiKeyOverride: apiKeyOverride,
                    skipDeepSeekFields: useOpenRouter);
                Console.WriteLine("Готово.");

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

            // Чекпойнт после Phase 4 (до чистки) — для возобновления с этого этапа
            Services.CheckpointStore.Save(new Models.CheckpointData
            {
                Phase = "phase4",
                Clusters = namedClusters,
                Meta = new Dictionary<string, Models.ClusterMeta>()
            });

            // ==========================================
            // Фаза 4.5: AI-чистка кластеров
            // Вынесенные запросы, не подошедшие ни к одному кластеру, уходят в «Нераспределённые».
            // Выполняется после формирования namedClusters:
            //   — если skipPhase4=true, работает по медоидным именам (сразу после Phase 3.6);
            //   — если skipPhase4=false, работает по результату AI Merge + Naming.
            // ==========================================
            if (_phase4CleanSettings.Enabled && namedClusters.Count > 0)
            {
                var cleaner = new Services.Phase4ClusterCleanerPass(
                    _client, _deepSeekSettings, _openRouterSettings, _phase4CleanSettings, _businessSettings);
                namedClusters = await cleaner.CleanAsync(namedClusters);
            }

            // Чекпойнт после Phase 4.5 (до Phase 5) — для возобновления с этого этапа
            Services.CheckpointStore.Save(new Models.CheckpointData
            {
                Phase = "phase4_5",
                Clusters = namedClusters,
                Meta = new Dictionary<string, Models.ClusterMeta>()
            });

            // ==========================================
            // Фаза 5: Отбор FAQ-кластеров и привязка к статьям
            // AI отбирает кластеры, не подходящие для отдельной статьи, но подходящие
            // как FAQ-блоки. Состав кластеров НЕ меняется — FAQ-кластеры остаются в списке
            // статей и получают метаданные (IsFaq, LinkedArticle). Привязка к статье — по смыслу,
            // автоматически (cosine similarity по representative-векторам, без AI).
            // ==========================================
            var clusterMeta = new Dictionary<string, Models.ClusterMeta>(StringComparer.OrdinalIgnoreCase);
            if (_phase5FaqSettings.Enabled && namedClusters.Count > 0)
            {
                var faqPass = new Services.FaqSelectionPass(
                    _client, _deepSeekSettings, _openRouterSettings, _phase5FaqSettings, _businessSettings);
                clusterMeta = await faqPass.RunAsync(
                    namedClusters, phraseEmbeddings ?? new Dictionary<string, float[]>());
            }

            // Чекпойнт после Phase 5 (финальный) — для полного возобновления
            Services.CheckpointStore.Save(new Models.CheckpointData
            {
                Phase = "phase5",
                Clusters = namedClusters,
                Meta = clusterMeta
            });

            int totalKeys = namedClusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nИтого: {namedClusters.Count} кластеров, {totalKeys} ключей.");
            return new Models.ClusteringResult
            {
                Clusters = namedClusters,
                Meta = clusterMeta
            };
        }

        /// <summary>
        /// Возобновление работы с чекпойнта завершённой фазы (Phase 4 / 4.5 / 5).
        /// Загружает сохранённые кластеры и выполняет ТОЛЬКО последующие фазы:
        ///   — "phase4"   → пропустить фазы 1-4, выполнить 4.5 и 5
        ///   — "phase4_5" → пропустить фазы 1-4.5, выполнить только 5
        ///   — "phase5"   → всё готово, вернуть результат из чекпойнта
        /// </summary>
        private async Task<Models.ClusteringResult?> ResumeFromCheckpointAsync(string resumeFromPhase)
        {
            var checkpoint = Services.CheckpointStore.Load(resumeFromPhase);
            if (checkpoint == null || checkpoint.Clusters == null || checkpoint.Clusters.Count == 0)
            {
                ConsoleUtils.WriteLine(
                    $"[ОШИБКА] Чекпойнт '{resumeFromPhase}.json' не найден или пуст. Запустите полную кластеризацию.",
                    ConsoleColor.Red);
                return null;
            }

            ConsoleUtils.WriteLine(
                $"[Resume] Чекпойнт '{resumeFromPhase}.json' загружен ({checkpoint.Clusters.Count} кластеров).",
                ConsoleColor.Cyan);

            var namedClusters = checkpoint.Clusters;
            var clusterMeta = checkpoint.Meta ?? new Dictionary<string, Models.ClusterMeta>(StringComparer.OrdinalIgnoreCase);

            // Если всё уже завершено — возвращаем результат
            if (string.Equals(resumeFromPhase, "phase5", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUtils.WriteLine("[Resume] Все фазы уже завершены (phase5). Возвращаю результат.", ConsoleColor.Cyan);
                return new Models.ClusteringResult { Clusters = namedClusters, Meta = clusterMeta };
            }

            // Для Phase 4.5/5 нужен доступ к эмбеддингам — загружаем из кэша (без API, если всё есть)
            Dictionary<string, float[]>? phraseEmbeddings = null;
            if (_embeddingClient != null)
            {
                Console.WriteLine("  [Embed] Загрузка эмбеддингов из кэша для Phase 4.5/5...");
                var allPhrases = namedClusters.Values
                    .SelectMany(v => v)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                phraseEmbeddings = await _embeddingClient.GetEmbeddingsBatchAsync(allPhrases);
                _embeddingClient.SaveCache();
            }

            // ==========================================
            // Phase 4.5: AI-чистка кластеров (только если резюмируем с phase4)
            // ==========================================
            if (string.Equals(resumeFromPhase, "phase4", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("\n--- Фаза 4.5: AI-чистка кластеров (возобновление) ---");
                if (_phase4CleanSettings.Enabled && namedClusters.Count > 0)
                {
                    var cleaner = new Services.Phase4ClusterCleanerPass(
                        _client, _deepSeekSettings, _openRouterSettings, _phase4CleanSettings, _businessSettings);
                    namedClusters = await cleaner.CleanAsync(namedClusters);
                }

                Services.CheckpointStore.Save(new Models.CheckpointData
                {
                    Phase = "phase4_5",
                    Clusters = namedClusters,
                    Meta = new Dictionary<string, Models.ClusterMeta>()
                });
            }

            // ==========================================
            // Phase 5: Отбор FAQ-кластеров и привязка к статьям
            // ==========================================
            Console.WriteLine("\n--- Фаза 5: Отбор FAQ-кластеров и привязка к статьям (возобновление) ---");
            if (_phase5FaqSettings.Enabled && namedClusters.Count > 0)
            {
                var faqPass = new Services.FaqSelectionPass(
                    _client, _deepSeekSettings, _openRouterSettings, _phase5FaqSettings, _businessSettings);
                clusterMeta = await faqPass.RunAsync(
                    namedClusters, phraseEmbeddings ?? new Dictionary<string, float[]>());
            }

            Services.CheckpointStore.Save(new Models.CheckpointData
            {
                Phase = "phase5",
                Clusters = namedClusters,
                Meta = clusterMeta
            });

            int totalKeys = namedClusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nИтого: {namedClusters.Count} кластеров, {totalKeys} ключей.");
            return new Models.ClusteringResult
            {
                Clusters = namedClusters,
                Meta = clusterMeta
            };
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
        /// Проверяет, что API-ключ OpenRouter работает, выполняя тестовый запрос эмбеддинга.
        /// Не использует кэш — всегда реальный запрос к API.
        /// </summary>
        private async Task<bool> TestEmbeddingApiAsync()
        {
            Console.Write("  [Embed] Проверка API-ключа (реальный запрос, без кэша)... ");
            bool valid = await _embeddingClient!.TestApiAsync("test");
            Console.WriteLine(valid ? "OK" : "ОШИБКА (нулевой вектор/недоступно)");
            return valid;
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
