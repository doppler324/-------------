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
    /// Управляет 4-фазным SERP-first пайплайном кластеризации:
    /// 1. Сбор SERP (XmlRiver + кэш) для ВСЕХ ключей.
    /// 2. Граф интентов (Connected Components).
    /// 3. AI-именование per-cluster (deepseek-reasoner).
    /// 4. Дробление oversized (deepseek-chat).
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
        private readonly SerpClusterValidator? _serpValidator;

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
                _serpValidator = new SerpClusterValidator(_xmlRiverClient, _serpSettings);
            }
        }

        /// <summary>
        /// Запускает полный цикл кластеризации.
        /// </summary>
        public async Task<Dictionary<string, List<string>>?> RunAsync(List<string> keywords)
        {
            if (_serpSettings.EnableSerpFirst && _xmlRiverClient != null)
                return await RunSerpFirstAsync(keywords);
            else
                return await RunAiFirstAsync(keywords);
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
            // Фаза 3: Рекурсивное дробление oversized (графовый split)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 3: Рекурсивное дробление oversized ---");

            var splitClusters = new List<List<string>>();
            var splitUnclustered = new HashSet<string>(serpUnclustered, StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in serpClusters)
            {
                if (cluster.Count <= maxClusterSize)
                {
                    splitClusters.Add(cluster);
                }
                else
                {
                    Console.WriteLine($"  Кластер {cluster.Count} ключей > max ({maxClusterSize}) — дробление...");
                    var (subClusters, subUnclustered) = SplitOversizedRecursive(
                        cluster, serpData, maxClusterSize, _serpSettings.OverlapThreshold);

                    splitClusters.AddRange(subClusters);
                    foreach (var key in subUnclustered)
                        splitUnclustered.Add(key);
                }
            }

            Console.WriteLine($"  После дробления: {splitClusters.Count} кластеров, {splitUnclustered.Count} нераспределённых.");

            // ==========================================
            // Фаза 4: AI-именование per-cluster (deepseek-reasoner)
            // ==========================================
            Console.WriteLine($"\n--- Фаза 4: AI-именование кластеров ---");

            int clusterIndex = 0;
            var namedClusters = new Dictionary<string, List<string>>();
            var allUnclustered = new HashSet<string>(splitUnclustered, StringComparer.OrdinalIgnoreCase);

            foreach (var cluster in splitClusters)
            {
                clusterIndex++;
                Console.Write($"  Кластер {clusterIndex}/{splitClusters.Count}: {cluster.Count} ключей... ");

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
        /// Рекурсивно дробит oversized-кластер через граф кластеризацию с повышенным порогом.
        /// Если кластер > maxSize — применяет SerpGraphClusterizer с порогом currentThreshold + 1.
        /// Рекурсивно повторяет для каждого подкластера, пока все не станут ≤ maxSize
        /// или порог не превысит TopResultsCount (дальше дробить некуда).
        /// </summary>
        private (List<List<string>> SubClusters, List<string> Unclustered) SplitOversizedRecursive(
            List<string> cluster,
            Dictionary<string, KeywordSearchResult> serpData,
            int maxSize,
            int currentThreshold)
        {
            if (cluster.Count <= maxSize || currentThreshold > _serpSettings.TopResultsCount)
            {
                return (new List<List<string>> { cluster }, new List<string>());
            }

            int nextThreshold = currentThreshold + 1;
            Console.WriteLine($"    Split с порогом {nextThreshold}...");

            // Создаём подграф только для ключей этого кластера
            var subSerpData = new Dictionary<string, KeywordSearchResult>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in cluster)
            {
                if (serpData.TryGetValue(key, out var sr))
                    subSerpData[key] = sr;
            }

            var subGraph = new SerpGraphClusterizer(nextThreshold, _serpSettings.TopResultsCount);
            var (subClusters, subUnclustered) = subGraph.Clusterize(subSerpData);

            if (subClusters.Count <= 1 && subClusters.Sum(c => c.Count) == cluster.Count)
            {
                // Повышение порога не помогло разбить — оставляем как есть
                return (new List<List<string>> { cluster }, subUnclustered);
            }

            // Рекурсивно дробим каждый подкластер
            var result = new List<List<string>>();
            var allUnclustered = new HashSet<string>(subUnclustered, StringComparer.OrdinalIgnoreCase);

            foreach (var subCluster in subClusters)
            {
                var (furtherClusters, furtherUnclustered) = SplitOversizedRecursive(
                    subCluster, serpData, maxSize, nextThreshold);

                result.AddRange(furtherClusters);
                foreach (var key in furtherUnclustered)
                    allUnclustered.Add(key);
            }

            return (result, allUnclustered.ToList());
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

        /// <summary>
        /// Дробит oversized-кластеры через deepseek-chat.
        /// </summary>
        private async Task<Dictionary<string, List<string>>> SplitOversizedClustersAsync(
            Dictionary<string, List<string>> clusters, int maxSize)
        {
            if (maxSize <= 0)
                return clusters;

            var result = new Dictionary<string, List<string>>();
            int totalBefore = clusters.Count;

            foreach (var kvp in clusters)
            {
                // Служебные и мелкие кластеры не дробится
                if (kvp.Key == UnclusteredKey || kvp.Value.Count <= maxSize)
                {
                    result[kvp.Key] = kvp.Value;
                    continue;
                }

                Console.WriteLine($"  \"{kvp.Key}\" ({kvp.Value.Count} ключей, лимит {maxSize}) — дробим...");

                var split = await SplitClusterAsync(kvp.Value, maxSize);

                if (split != null && split.Count > 0)
                {
                    foreach (var s in split)
                        result[s.Key] = s.Value;

                    int newClusters = split.Count;
                    Console.WriteLine($"    → Разбит на {newClusters} кластеров.");
                }
                else
                {
                    // Не удалось разбить — оставляем как есть
                    Console.WriteLine($"    → Не удалось разбить, оставлен как есть.");
                    result[kvp.Key] = kvp.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Отправляет oversized-кластер в deepseek-chat для разбивки.
        /// </summary>
        private async Task<Dictionary<string, List<string>>?> SplitClusterAsync(
            List<string> keywords, int maxSize)
        {
            string instructionTemplate = LoadInstruction("instructions/serp_split_oversized.txt");
            if (string.IsNullOrWhiteSpace(instructionTemplate))
                return null;

            string instruction = string.Format(instructionTemplate,
                keywords.Count, maxSize);

            string systemPrompt = BuildSystemPrompt(instruction);
            string userMessage = string.Join("\n", keywords);

            try
            {
                var clusters = await DeepSeekHelper.SendRequestAsync(
                    _client, systemPrompt, userMessage, _deepSeekSettings);

                return clusters;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════
        // AI-First Pipeline (старый, для обратной совместимости)
        // ═══════════════════════════════════════════════════════

        private async Task<Dictionary<string, List<string>>?> RunAiFirstAsync(List<string> keywords)
        {
            int chunkSize = _businessSettings.ChunkSize;
            int maxClusterSize = _businessSettings.ParseMaxClusterSize();

            Console.WriteLine($"\nПравило гранулярности: {_businessSettings.GranularityRule}");
            Console.WriteLine($"Максимум ключей на кластер (парсинг): {maxClusterSize}");

            // Шаг 1: Draft
            Console.WriteLine($"\n--- Шаг 1: Draft (первые {chunkSize} ключей) ---");
            var draftChunk = keywords.Take(chunkSize).ToList();
            string draftInstruction = string.Format(
                LoadInstruction("instructions/step1_draft.txt"),
                maxClusterSize);

            string serpContext = "";
            if (_serpSettings.EnabledForDraft && _xmlRiverClient != null)
            {
                Console.WriteLine("  Сбор SERP-контекста для первого чанка...");
                serpContext = await BuildSerpContextAsync(draftChunk);
            }

            string draftPrompt = BuildSystemPrompt(draftInstruction, serpContext);

            var clusters = await DeepSeekHelper.SendRequestAsync(
                _client, draftPrompt, string.Join("\n", draftChunk), _deepSeekSettings);

            if (clusters == null || clusters.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] Draft не вернул ни одного кластера.");
                Console.ResetColor();
                return null;
            }

            Console.WriteLine($"Создано {clusters.Count} кластеров на {draftChunk.Count} ключах.");

            // Шаг 2: Mapping
            Console.WriteLine($"\n--- Шаг 2: Mapping (оставшиеся {keywords.Count - chunkSize} ключей) ---");
            string mappingInstructionTemplate = LoadInstruction("instructions/step2_mapping.txt");

            for (int i = chunkSize; i < keywords.Count; i += chunkSize)
            {
                var chunk = keywords.Skip(i).Take(chunkSize).ToList();
                var clusterNames = string.Join(", ", clusters.Keys);
                string mappingInstruction = string.Format(
                    mappingInstructionTemplate,
                    clusterNames,
                    maxClusterSize);
                string mappingPrompt = BuildSystemPrompt(mappingInstruction);

                Console.Write($"  Чанк {i / chunkSize + 1}: {chunk.Count} ключей... ");

                try
                {
                    var delta = await DeepSeekHelper.SendRequestAsync(
                        _client, mappingPrompt, string.Join("\n", chunk), _deepSeekSettings);

                    if (delta != null)
                    {
                        clusters = MergeClusters(clusters, delta);
                        Console.WriteLine($"готово. Всего кластеров: {clusters.Count}");
                    }
                    else
                    {
                        Console.WriteLine("ошибка (null), пропускаем чанк.");
                    }
                }
                catch (Exception)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"таймаут/ошибка, пропускаем чанк.");
                    Console.ResetColor();
                }
            }

            // Шаг 3: Refactoring
            Console.WriteLine("\n--- Шаг 3: Refactoring ---");
            string refactorInstructionTemplate = LoadInstruction("instructions/step3_refactoring.txt");
            string refactorInstruction = string.Format(
                refactorInstructionTemplate,
                maxClusterSize,
                _businessSettings.ClusteringLogic);
            string refactorPrompt = BuildSystemPrompt(refactorInstruction);
            string clustersJson = JsonSerializer.Serialize(clusters);

            var refactoringSettings = new DeepSeekSettings
            {
                ApiKey = _deepSeekSettings.ApiKey,
                Model = string.IsNullOrEmpty(_deepSeekSettings.RefactoringModel)
                    ? _deepSeekSettings.Model
                    : _deepSeekSettings.RefactoringModel,
                Temperature = _deepSeekSettings.Temperature,
                MaxTokens = _deepSeekSettings.MaxTokens,
                TopP = _deepSeekSettings.TopP
            };

            Console.WriteLine($"  Модель: {refactoringSettings.Model}");

            try
            {
                var finalClusters = await DeepSeekHelper.SendRequestAsync(
                    _client, refactorPrompt, clustersJson, refactoringSettings);

                if (finalClusters != null)
                {
                    Console.WriteLine("Рефакторинг завершён.");
                    clusters = finalClusters;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Шаг 3 (Refactoring) не выполнен: {ex.GetType().Name}");
                Console.ResetColor();
            }

            // Шаг 4: Iterative Refinement
            Console.WriteLine("\n--- Шаг 4: Iterative Refinement (до 5 итераций) ---");
            var clusterSizes = clusters
                .Where(c => c.Key != UnclusteredKey)
                .Select(c => c.Value.Count)
                .OrderByDescending(s => s)
                .ToList();

            int oversizedCount = clusterSizes.Count(s => s > maxClusterSize);
            Console.WriteLine($"  Кластеров: {clusters.Count} (без учёта {UnclusteredKey}: {clusterSizes.Count})");
            Console.WriteLine($"  MaxClusterSize = {maxClusterSize}");
            Console.WriteLine($"  Oversized: {oversizedCount}");

            if (oversizedCount == 0)
            {
                Console.WriteLine($"  Размеры (макс/мин/сред): {clusterSizes.FirstOrDefault()}/" +
                    $"{clusterSizes.LastOrDefault()}/{clusterSizes.Average():F1}");
            }

            clusters = await RefinementLoopAsync(clusters, maxClusterSize);

            // Шаг 4.5: Semantic Merge
            Console.WriteLine("\n--- Шаг 4.5: Semantic Merge (дедупликация) ---");
            clusters = await SemanticMergeAsync(clusters, maxClusterSize);

            // Шаг 4.6: SERP Validation
            Console.WriteLine("\n--- Шаг 4.6: SERP Validation (XmlRiver) ---");
            if (_serpValidator != null)
            {
                Console.WriteLine("  SERP-валидация включена. Проверка кластеров через поисковую выдачу...");
                clusters = await _serpValidator.ValidateAsync(clusters);
            }
            else
            {
                Console.WriteLine("  SERP-валидация отключена (не указаны XMLRiver_User/Key).");
            }

            Console.WriteLine($"\nИтого: {clusters.Count} кластеров.");
            return clusters;
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
        /// Собирает SERP-контекст для первого чанка ключей.
        /// </summary>
        private async Task<string> BuildSerpContextAsync(List<string> chunkKeys)
        {
            if (_xmlRiverClient == null || chunkKeys.Count == 0)
                return "";

            try
            {
                var serpData = await _xmlRiverClient.SearchBatchAsync(
                    chunkKeys,
                    _serpSettings.MaxConcurrency,
                    _serpSettings.TopResultsCount);

                if (serpData.Count == 0)
                    return "";

                // Формируем компактный блок: ключ → список доменов
                var lines = new List<string>();
                foreach (var kvp in serpData)
                {
                    var domains = kvp.Value.Results
                        .Select(r => r.Domain)
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(5);

                    lines.Add($"  \"{kvp.Key}\": {string.Join(", ", domains)}");
                }

                string serpBlock = string.Join("\n", lines);
                string serpContextTemplate = LoadInstruction("instructions/serp_context_block.txt");

                if (string.IsNullOrWhiteSpace(serpContextTemplate))
                    return "";

                return string.Format(serpContextTemplate, serpBlock);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Итеративный цикл AI-рефайнмента (только для AI-first режима).
        /// </summary>
        private async Task<Dictionary<string, List<string>>> RefinementLoopAsync(
            Dictionary<string, List<string>> clusters, int maxSize)
        {
            const int maxIterations = 5;
            var currentClusters = new Dictionary<string, List<string>>(clusters);

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                var oversized = currentClusters
                    .Where(c => c.Key != UnclusteredKey && c.Value.Count > maxSize)
                    .ToDictionary(c => c.Key, c => c.Value);

                if (oversized.Count == 0)
                {
                    Console.WriteLine($"  Все кластеры в рамках лимита (maxSize={maxSize}).");
                    return currentClusters;
                }

                var oversizedInfo = oversized
                    .OrderByDescending(c => c.Value.Count)
                    .Select(c => $"  • {c.Key} = {c.Value.Count} ключей");
                Console.WriteLine($"  Oversized ({oversized.Count} шт.):\n{string.Join("\n", oversizedInfo)}");

                Console.Write($"  Итерация {iteration + 1}/{maxIterations}: разбиваем {oversized.Count} кластеров... ");

                string oversizedJson = JsonSerializer.Serialize(oversized);
                string refinementInstruction = string.Format(
                    LoadInstruction("instructions/refinement_iteration.txt"),
                    maxSize);
                string refinementPrompt = BuildSystemPrompt(refinementInstruction);

                try
                {
                    var refined = await DeepSeekHelper.SendRequestAsync(
                        _client, refinementPrompt, oversizedJson, _deepSeekSettings);

                    if (refined != null)
                    {
                        foreach (var key in oversized.Keys)
                            currentClusters.Remove(key);

                        currentClusters = MergeClusters(currentClusters, refined);
                        Console.WriteLine($"теперь {currentClusters.Count} кластеров.");
                    }
                    else
                    {
                        Console.WriteLine("ошибка (null), прерываем refinement.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"ошибка ({ex.GetType().Name}), прерываем refinement.");
                    Console.ResetColor();
                    break;
                }
            }

            return currentClusters;
        }

        /// <summary>
        /// Semantic Merge — AI-дедупликация (только для AI-first режима).
        /// </summary>
        private async Task<Dictionary<string, List<string>>> SemanticMergeAsync(
            Dictionary<string, List<string>> clusters, int maxSize)
        {
            if (clusters.Count == 0)
                return clusters;

            string clustersJson = JsonSerializer.Serialize(clusters);
            string mergeInstruction = string.Format(
                LoadInstruction("instructions/merge_deduplication.txt"),
                maxSize);
            string mergePrompt = BuildSystemPrompt(mergeInstruction);

            Console.WriteLine("  Отправка всех кластеров на дедупликацию...");

            try
            {
                var merged = await DeepSeekHelper.SendRequestAsync(
                    _client, mergePrompt, clustersJson, _deepSeekSettings);

                if (merged != null)
                {
                    int before = clusters.Count;
                    int after = merged.Count;
                    int mergedCount = before - after;
                    if (mergedCount > 0)
                        Console.WriteLine($"  Объединено кластеров: {mergedCount} (было {before}, стало {after}).");
                    else
                        Console.WriteLine("  Дубликатов не найдено.");

                    return merged;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Semantic Merge не выполнен: {ex.GetType().Name}");
                Console.ResetColor();
            }

            return clusters;
        }

        /// <summary>
        /// Сливает два словаря кластеров.
        /// </summary>
        private static Dictionary<string, List<string>> MergeClusters(
            Dictionary<string, List<string>> existing,
            Dictionary<string, List<string>> delta)
        {
            foreach (var kvp in delta)
            {
                if (existing.ContainsKey(kvp.Key))
                {
                    foreach (var keyword in kvp.Value)
                    {
                        if (!existing[kvp.Key].Contains(keyword))
                            existing[kvp.Key].Add(keyword);
                    }
                }
                else
                {
                    existing[kvp.Key] = kvp.Value;
                }
            }
            return existing;
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
