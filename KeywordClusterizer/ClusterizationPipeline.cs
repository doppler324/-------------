using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KeywordClusterizer.Models;
using KeywordClusterizer.Services;

namespace KeywordClusterizer
{
    /// <summary>
    /// Управляет SERP-first пайплайном кластеризации:
    /// 1. Сбор SERP (XmlRiver + кэш) для ВСЕХ ключей.
    /// 2. Граф интентов (Connected Components).
    /// 2.5. Rescue Pass — прикрепление сирот к ближайшим кластерам.
    /// 3. Semantic Map-Reduce (тегизация oversized-кластеров через deepseek-chat).
    /// 4. AI-именование per-cluster (deepseek-reasoner).
    ///
    /// Если SERP-first выключен (EnableSerpFirst = false),
    /// использует старый AI-first пайплайн.
    /// </summary>
    public class ClusterizationPipeline
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly BusinessSettings _businessSettings;
        private readonly XmlRiverSettings _serpSettings;
        private readonly XmlRiverClient? _xmlRiverClient;
        private readonly SerpCacheService? _cacheService;
        private const string UnclusteredKey = "Нераспределённые";

        public ClusterizationPipeline(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            BusinessSettings businessSettings,
            XmlRiverSettings serpSettings)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _businessSettings = businessSettings;
            _serpSettings = serpSettings;

            // Инициализируем SERP-клиент только если есть учётные данные
            if (!string.IsNullOrWhiteSpace(_serpSettings.XmlriverUser) &&
                !string.IsNullOrWhiteSpace(_serpSettings.XmlriverKey))
            {
                // Создаём кэш, если включён
                if (_serpSettings.EnableCache)
                    _cacheService = new SerpCacheService(_serpSettings.CachePath);

                _xmlRiverClient = new XmlRiverClient(client, _serpSettings, _cacheService);
            }
        }

        /// <summary>
        /// Запускает полный цикл кластеризации.
        /// </summary>
        public async Task<Dictionary<string, List<string>>?> RunAsync(List<string> keywords)
        {
            // SERP-first пайплайн (AI-first удалён)
            if (_xmlRiverClient == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] XmlRiver не настроен (нет User/Key).");
                Console.ResetColor();
                return null;
            }
            return await RunSerpFirstAsync(keywords);
        }

        // ═══════════════════════════════════════════════════════
        // SERP-First Pipeline (новый)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Новый 4-фазный SERP-first пайплайн.
        /// </summary>
        private async Task<Dictionary<string, List<string>>?> RunSerpFirstAsync(List<string> keywords)
        {
            int maxClusterSize = _businessSettings.ParseMaxClusterSize();
            Console.WriteLine($"\n=== SERP-First пайплайн ===");
            Console.WriteLine($"Ключей: {keywords.Count}, MaxClusterSize: {maxClusterSize}");

            // ==========================================
            // Фаза 1: Сбор SERP для ВСЕХ ключей
            // ==========================================
            Console.WriteLine($"\n--- Фаза 1: Сбор SERP ({keywords.Count} ключей) ---");

            var serpData = await _xmlRiverClient!.SearchBatchAsync(
                keywords,
                _serpSettings.MaxConcurrency,
                _serpSettings.TopResultsCount);

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
                _serpSettings.OverlapThreshold,
                _serpSettings.TopResultsCount);

            var (serpClusters, serpUnclustered) = graphClusterizer.Clusterize(serpData);

            // Сохраняем SERP-кэш на диск
            if (_cacheService != null)
                _cacheService.Save();

            if (serpClusters.Count == 0 && serpUnclustered.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[ПРЕДУПРЕЖДЕНИЕ] Граф не создал ни одного кластера. Все ключи изолированы.");
                Console.ResetColor();
            }

            // ==========================================
            // Фаза 2.5: Rescue Pass — прикрепление сирот к ближайшим кластерам
            // ==========================================
            Console.WriteLine($"\n--- Фаза 2.5: Rescue Pass ---");
            RescuePass(serpClusters, serpUnclustered, serpData);

            // ==========================================
            // Фаза 3: Semantic Map-Reduce для oversized-кластеров
            // Кластеры > maxClusterSize — это широкие интенты, которые граф не может разбить.
            // Используем AI: чанки по 100 ключей → тегизация → GroupBy по тегу.
            // ==========================================
            Console.WriteLine($"\n--- Фаза 3: Semantic Map-Reduce (тегизация) ---");

            var finalClusters = new List<List<string>>();
            int oversizedCount = serpClusters.Count(c => c.Count > maxClusterSize);
            var processedOversizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (oversizedCount > 0)
                Console.WriteLine($"  Oversized кластеров: {oversizedCount} — отправляю в semantic tagging...");

            foreach (var cluster in serpClusters)
            {
                if (cluster.Count <= maxClusterSize)
                {
                    finalClusters.Add(cluster);
                    continue;
                }

                Console.WriteLine($"  Кластер {cluster.Count} ключей → Semantic Map-Reduce...");

                // Шаг 1: Chunking по 100 ключей
                var chunks = cluster
                    .Select((key, idx) => new { key, idx })
                    .GroupBy(x => x.idx / 100)
                    .Select(g => g.Select(x => x.key).ToList())
                    .ToList();

                Console.WriteLine($"    Чанков: {chunks.Count} по ~100 ключей.");

                // Шаг 2: Map — последовательная тегизация чанков с передачей контекста
                // Каждый следующий чанк видит уже созданные теги и переиспользует их
                var tagGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var accumulatedTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int totalTagged = 0;

                for (int ci = 0; ci < chunks.Count; ci++)
                {
                    var result = await TagKeywordsAsync(chunks[ci], accumulatedTagNames);

                    if (result == null)
                    {
                        Console.WriteLine($"      ⚠ чанк {ci + 1}/{chunks.Count}: ошибка, пропущен.");
                        continue;
                    }

                    foreach (var tk in result)
                    {
                        if (string.IsNullOrWhiteSpace(tk.Keyword) || string.IsNullOrWhiteSpace(tk.Tag))
                            continue;

                        if (!tagGroups.ContainsKey(tk.Tag))
                            tagGroups[tk.Tag] = new List<string>();

                        tagGroups[tk.Tag].Add(tk.Keyword);
                        accumulatedTagNames.Add(tk.Tag);
                        processedOversizedKeys.Add(tk.Keyword);
                        totalTagged++;
                    }

                    Console.WriteLine($"      чанк {ci + 1}/{chunks.Count}: {result.Count} ключей тегизировано, всего тегов: {accumulatedTagNames.Count}");
                }

                // Шаг 3: Проверка лимитов
                foreach (var kvp in tagGroups)
                {
                    if (kvp.Value.Count <= maxClusterSize)
                    {
                        finalClusters.Add(kvp.Value);
                    }
                    else
                    {
                        Console.WriteLine($"    Тег '{kvp.Key}' собрал {kvp.Value.Count} > {maxClusterSize} — оставляем как есть.");
                        finalClusters.Add(kvp.Value);
                    }
                }

                Console.WriteLine($"    → {tagGroups.Count} логических подкластеров ({totalTagged}/{cluster.Count} ключей тегизировано).");
            }

            // Собираем ключи oversized-кластеров, не прошедшие тегизацию — вернутся в нераспределённые
            var lostOversizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cluster in serpClusters.Where(c => c.Count > maxClusterSize))
            {
                foreach (var key in cluster)
                {
                    if (!processedOversizedKeys.Contains(key))
                        lostOversizedKeys.Add(key);
                }
            }
            if (lostOversizedKeys.Count > 0)
                Console.WriteLine($"  [Rescue] Не тегизировано (возврат в нераспределённые): {lostOversizedKeys.Count} ключей.");

            Console.WriteLine($"  После Semantic Map-Reduce: {finalClusters.Count} кластеров.");

            // ==========================================
            // Фаза 4: AI-именование per-cluster (deepseek-reasoner)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 4: AI-именование кластеров ---");

            int clusterIndex = 0;
            var namedClusters = new Dictionary<string, List<string>>();
            var allUnclustered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Ключи, не спасённые RescuePass — возвращаем в нераспределённые
            foreach (var key in serpUnclustered)
                allUnclustered.Add(key);

            // Ключи oversized-кластеров, не прошедшие Semantic Map-Reduce — тоже в нераспределённые
            foreach (var key in lostOversizedKeys)
                allUnclustered.Add(key);

            foreach (var cluster in finalClusters)
            {
                clusterIndex++;
                Console.Write($"  Кластер {clusterIndex}/{finalClusters.Count}: {cluster.Count} ключей... ");

                var refined = await RefineClusterAsync(cluster);

                if (refined != null)
                {
                    namedClusters[refined.ClusterName] = refined.Keywords;

                    // AI мог выкинуть часть ключей — возвращаем их в unclustered
                    var lostKeys = cluster.Where(k =>
                        !refined.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase));
                    foreach (var key in lostKeys)
                        allUnclustered.Add(key);

                    if (refined.Unclustered.Count > 0)
                    {
                        foreach (var key in refined.Unclustered)
                            allUnclustered.Add(key);
                    }

                    Console.WriteLine($"→ \"{refined.ClusterName}\" ({refined.Keywords.Count} ключей, {refined.PageType})");
                }
                else
                {
                    string fallbackName = cluster.Count > 1
                        ? cluster[0] + " и др."
                        : cluster[0];

                    namedClusters[fallbackName] = cluster;
                    Console.WriteLine($"→ (fallback) \"{fallbackName}\"");
                }
            }

            // Добавляем нераспределённые
            if (allUnclustered.Count > 0)
            {
                namedClusters[UnclusteredKey] = allUnclustered.ToList();
                Console.WriteLine($"  Нераспределённых ключей: {allUnclustered.Count}");
            }

            // Итог
            int totalKeys = namedClusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nИтого: {namedClusters.Count} кластеров, {totalKeys} ключей.");

            return namedClusters;
        }

        /// <summary>
        /// Rescue Pass: прикрепляет unclustered ключи к ближайшему кластеру по пересечению URL.
        /// Даже 1 общий URL — достаточный сигнал для прикрепления.
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

                // Ищем кластер с максимальным пересечением URL
                foreach (var cluster in clusters)
                {
                    foreach (var key in cluster)
                    {
                        if (!serpData.TryGetValue(key, out var csr)) continue;
                        int overlap = csr.Urls.Count(u => orphanUrls.Contains(u));

                        if (overlap >= 1 && (best == null || overlap > best.Value.overlap))
                            best = (overlap, cluster);

                        // Достигнут порог кластеризации — не ищем дальше
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
        /// Semantic Map-Reduce: отправляет чанк ключей в deepseek-chat для тегизации.
        /// Каждому ключу присваивается логический тег (2-3 слова).
        /// </summary>
        private async Task<List<TaggedKeyword>?> TagKeywordsAsync(
            List<string> chunk,
            HashSet<string>? existingTags = null)
        {
            string instruction = LoadInstruction("instructions/serp_tagging.txt");
            if (string.IsNullOrWhiteSpace(instruction))
                return null;

            // Подставляем уже созданные теги как контекст для переиспользования
            string existingTagsBlock = existingTags != null && existingTags.Count > 0
                ? string.Join(", ", existingTags.Select(t => $"'{t}'"))
                : "нет созданных тегов";
            instruction = instruction.Replace("{existing_tags}", existingTagsBlock);

            // Не используем глобальный system_prompt.txt — он навязывает структуру
            // { clusters, unclustered }, а инструкция serp_tagging.txt требует
            // { tags: [{ keyword, tag }] }
            string systemPrompt = BuildSystemPrompt(instruction, includeSystemPrompt: false);
            string userMessage = string.Join("\n", chunk);

            try
            {
                var response = await DeepSeekHelper.SendRawRequestAsync<TaggingResponse>(
                    _client, systemPrompt, userMessage, _deepSeekSettings);

                return response?.Tags;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Отправляет один SERP-кластер в deepseek-reasoner для именования и чистки.
        /// При пустом ответе делает до 3 повторных попыток с паузой 5 секунд.
        /// </summary>
        private async Task<RefinedCluster?> RefineClusterAsync(List<string> keywords)
        {
            string instruction = LoadInstruction("instructions/serp_cluster_refine.txt");
            if (string.IsNullOrWhiteSpace(instruction))
            {
                Console.WriteLine("инструкция не найдена.");
                return null;
            }

            // Не используем глобальный system_prompt.txt — он навязывает структуру
            // { clusters, unclustered }, а инструкция serp_cluster_refine.txt требует
            // { cluster_name, page_type, keywords, unclustered }
            string systemPrompt = BuildSystemPrompt(instruction, includeSystemPrompt: false);
            string userMessage = string.Join("\n", keywords);

            var reasonerSettings = new DeepSeekSettings
            {
                ApiKey = _deepSeekSettings.ApiKey,
                Model = string.IsNullOrEmpty(_deepSeekSettings.RefactoringModel)
                    ? _deepSeekSettings.Model
                    : _deepSeekSettings.RefactoringModel,
                Temperature = _deepSeekSettings.Temperature,
                MaxTokens = _deepSeekSettings.MaxTokens,
                TopP = _deepSeekSettings.TopP
            };

            const int maxRetries = 3;
            const int retryDelayMs = 5000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await DeepSeekHelper.SendRawRequestAsync<RefinedCluster>(
                        _client, systemPrompt, userMessage, reasonerSettings);

                    if (response == null)
                    {
                        if (attempt < maxRetries)
                        {
                            Console.Write($" (попытка {attempt}/{maxRetries}, повтор через {retryDelayMs / 1000}с...)");
                            await Task.Delay(retryDelayMs);
                            continue;
                        }
                        return null;
                    }

                    // Проверяем, что AI вернул осмысленный ответ
                    bool hasContent = !string.IsNullOrWhiteSpace(response.ClusterName) &&
                                      response.Keywords != null &&
                                      response.Keywords.Count > 0;

                    if (hasContent)
                    {
                        return new RefinedCluster
                        {
                            ClusterName = response.ClusterName,
                            PageType = response.PageType,
                            Keywords = response.Keywords ?? new List<string>(),
                            Unclustered = response.Unclustered ?? new List<string>()
                        };
                    }

                    // Пустой ответ — повторяем
                    if (attempt < maxRetries)
                    {
                        Console.Write($" (пусто, попытка {attempt}/{maxRetries}, повтор через {retryDelayMs / 1000}с...)");
                        await Task.Delay(retryDelayMs);
                    }
                    else
                    {
                        Console.Write($" (пусто после {maxRetries} попыток)");
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        Console.Write($" ({ex.GetType().Name}, попытка {attempt}/{maxRetries}, повтор через {retryDelayMs / 1000}с...)");
                        await Task.Delay(retryDelayMs);
                    }
                    else
                    {
                        Console.Write($" ({ex.GetType().Name} после {maxRetries} попыток)");
                        Console.ResetColor();
                        return null;
                    }
                }
            }

            return null;
        }

        // ═══════════════════════════════════════════════════════
        // Общие вспомогательные методы
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Строит системный промпт: глобальная роль + базовые правила + инструкция шага + SERP-контекст.
        /// <summary>
        /// Собирает системный промпт из глобального system_prompt.txt, baseRules и переданной инструкции.
        /// </summary>
        /// <param name="instructionText">Текст инструкции (из файла instructions/*.txt).</param>
        /// <param name="serpContext">Опциональный SERP-контекст для первого чанка.</param>
        /// <param name="includeSystemPrompt">
        /// Если false — исключает глобальный system_prompt.txt (нужно, когда инструкция
        /// определяет свою структуру JSON, отличную от { clusters, unclustered }).
        /// </param>
        private string BuildSystemPrompt(string instructionText, string serpContext = "", bool includeSystemPrompt = true)
        {
            var parts = new List<string>();

            if (includeSystemPrompt)
            {
                string systemPrompt = LoadInstruction("system_prompt.txt");
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    parts.Add(systemPrompt);
            }

            string baseRules = _businessSettings.ToBaseRules();
            if (!string.IsNullOrWhiteSpace(baseRules))
                parts.Add(baseRules);

            if (!string.IsNullOrWhiteSpace(instructionText))
                parts.Add(instructionText);

            if (!string.IsNullOrWhiteSpace(serpContext))
                parts.Add(serpContext);

            return string.Join("\n\n", parts);
        }

        /// <summary>
        /// Загружает содержимое файла инструкции.
        /// </summary>
        private static string LoadInstruction(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл инструкции '{filePath}' не найден. Используется пустая строка.");
                Console.ResetColor();
                return "";
            }
            return File.ReadAllText(filePath).Trim();
        }
    }
}
