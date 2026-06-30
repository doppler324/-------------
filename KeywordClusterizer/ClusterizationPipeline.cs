using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// Управляет пайплайном кластеризации:
    /// 1. Draft — создание первичной страничной структуры из первого чанка.
    /// 2. Mapping — распределение остальных ключей чанками с жёстким лимитом.
    /// 3. Refactoring — аудит, merge дубликатов, разбивка oversized кластеров.
    /// 4. Iterative Refinement — до 5 итераций AI-сплита oversized + merge дубликатов.
    /// 4.5. Semantic Merge — AI-дедупликация кластеров с одинаковым интентом.
    /// 4.6. SERP Validation — проверка интентов через реальную поисковую выдачу (XmlRiver).
    /// </summary>
    public class ClusterizationPipeline
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly BusinessSettings _businessSettings;
        private readonly XmlRiverSettings _serpSettings;
        private readonly XmlRiverClient? _xmlRiverClient;
        private readonly SerpClusterValidator? _serpValidator;

        // Имя служебного кластера для нераспределённых ключей
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

            // Инициализируем SERP-клиент только если валидация включена
            if (_serpSettings.EnableValidation &&
                !string.IsNullOrWhiteSpace(_serpSettings.XmlriverUser) &&
                !string.IsNullOrWhiteSpace(_serpSettings.XmlriverKey))
            {
                _xmlRiverClient = new XmlRiverClient(client, _serpSettings);
                _serpValidator = new SerpClusterValidator(_xmlRiverClient, _serpSettings);
            }
        }

        /// <summary>
        /// Запускает полный цикл кластеризации.
        /// </summary>
        public async Task<Dictionary<string, List<string>>?> RunAsync(List<string> keywords)
        {
            int chunkSize = _businessSettings.ChunkSize;
            int maxClusterSize = _businessSettings.ParseMaxClusterSize();

            Console.WriteLine($"\nПравило гранулярности: {_businessSettings.GranularityRule}");
            Console.WriteLine($"Максимум ключей на кластер (парсинг): {maxClusterSize}");

            // ==========================================
            // Шаг 1: Draft — первые chunkSize ключей
            // ==========================================
            Console.WriteLine($"\n--- Шаг 1: Draft (первые {chunkSize} ключей) ---");
            var draftChunk = keywords.Take(chunkSize).ToList();
            string draftInstruction = string.Format(
                LoadInstruction("instructions/step1_draft.txt"),
                maxClusterSize);

            // SERP-контекст для Draft (если включён)
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

            // ==========================================
            // Шаг 2: Mapping — остальные ключи чанками
            // ==========================================
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

            // ==========================================
            // Шаг 3: Refactoring — аудит и разбивка
            // ==========================================
            Console.WriteLine("\n--- Шаг 3: Refactoring ---");
            string refactorInstructionTemplate = LoadInstruction("instructions/step3_refactoring.txt");
            string refactorInstruction = string.Format(
                refactorInstructionTemplate,
                maxClusterSize,
                _businessSettings.ClusteringLogic);
            string refactorPrompt = BuildSystemPrompt(refactorInstruction);

            string clustersJson = JsonSerializer.Serialize(clusters);

            // Используем модель для рефакторинга (если задана), иначе основную
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

            // ==========================================
            // Шаг 4: Iterative Refinement Loop (до 5 итераций)
            // ==========================================
            Console.WriteLine("\n--- Шаг 4: Iterative Refinement (до 5 итераций) ---");

            // Сводка после рефакторинга
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

            // ==========================================
            // Шаг 4.5: Semantic Merge — дедупликация кластеров с одинаковым интентом
            // ==========================================
            Console.WriteLine("\n--- Шаг 4.5: Semantic Merge (дедупликация) ---");
            clusters = await SemanticMergeAsync(clusters, maxClusterSize);

            // ==========================================
            // Шаг 4.6: SERP Validation — проверка интентов через поисковую выдачу
            // ==========================================
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

        /// <summary>
        /// Итеративный цикл AI-рефайнмента: до 5 проходов, на каждом oversized
        /// кластеры отправляются в нейросеть для разбивки на страничные группы.
        /// </summary>
        private async Task<Dictionary<string, List<string>>> RefinementLoopAsync(
            Dictionary<string, List<string>> clusters, int maxSize)
        {
            const int maxIterations = 5;
            var currentClusters = new Dictionary<string, List<string>>(clusters);

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                // Собираем oversized-кластеры (исключая "Нераспределённые")
                var oversized = currentClusters
                    .Where(c => c.Key != UnclusteredKey && c.Value.Count > maxSize)
                    .ToDictionary(c => c.Key, c => c.Value);

                if (oversized.Count == 0)
                {
                    Console.WriteLine($"  Все кластеры в рамках лимита (maxSize={maxSize}).");
                    return currentClusters;
                }

                // Выводим список oversized-кластеров с их размерами
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
                        // Удаляем старые oversized-кластеры
                        foreach (var key in oversized.Keys)
                            currentClusters.Remove(key);

                        // Добавляем новые (разбитые) кластеры
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
        /// Semantic Merge: отправляет все кластеры в AI для поиска дублирующихся
        /// интентов. Кластеры с одинаковым интентом объединяются (до maxSize ключей).
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
        /// Сливает новый словарь кластеров с существующим (добавляет ключи в существующие
        /// кластеры или создаёт новые, если их нет).
        /// </summary>
        private static Dictionary<string, List<string>> MergeClusters(
            Dictionary<string, List<string>> existing,
            Dictionary<string, List<string>> delta)
        {
            foreach (var kvp in delta)
            {
                if (existing.ContainsKey(kvp.Key))
                {
                    // Добавляем ключи, которых ещё нет в кластере
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
        /// Загружает содержимое файла инструкции для указанного шага.
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

        /// <summary>
        /// Собирает SERP-контекст для первого чанка ключей: параллельно опрашивает XmlRiver
        /// и формирует блок с доменами для вставки в промпт Draft.
        /// </summary>
        private async Task<string> BuildSerpContextAsync(List<string> chunkKeys)
        {
            if (_xmlRiverClient == null || chunkKeys.Count == 0)
                return "";

            try
            {
                // Параллельный сбор SERP для всех ключей первого чанка
                var serpData = await _xmlRiverClient.SearchBatchAsync(
                    chunkKeys,
                    _serpSettings.MaxConcurrency,
                    _serpSettings.TopResultsCount);

                // Формируем компактный блок: ключ → список доменов
                var contextLines = new List<string>();
                foreach (var kvp in serpData)
                {
                    if (kvp.Value.Results.Count == 0)
                        continue;

                    var domains = kvp.Value.Results
                        .Select(r => r.Domain)
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Distinct()
                        .ToList();

                    if (domains.Count > 0)
                    {
                        string domainsStr = string.Join(", ", domains);
                        contextLines.Add($"  \"{kvp.Key}\": {domainsStr}");
                    }
                }

                if (contextLines.Count == 0)
                {
                    Console.WriteLine("  SERP-контекст пуст (нет данных по ключам).");
                    return "";
                }

                string serpBlock = string.Join("\n", contextLines);
                string template = LoadInstruction("instructions/serp_context_block.txt");
                return string.Format(template, serpBlock);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [ПРЕДУПРЕЖДЕНИЕ] Не удалось собрать SERP-контекст: {ex.GetType().Name}");
                Console.ResetColor();
                return "";
            }
        }

        /// <summary>
        /// Собирает системный промпт: роль + JSON-формат + бизнес-правила + инструкция шага + опциональный SERP-контекст.
        /// </summary>
        private string BuildSystemPrompt(string instructionText, string serpContext = "")
        {
            // Загружаем общий системный промпт (роль агента + формат ответа)
            string systemPrompt = LoadInstruction("system_prompt.txt");

            // Бизнес-правила добавляются отдельным блоком
            string baseRules = _businessSettings.ToBaseRules();

            string result = $"{systemPrompt}\n\n{baseRules}\n\n{instructionText}";

            // SERP-контекст добавляется в конец, если не пуст
            if (!string.IsNullOrWhiteSpace(serpContext))
                result += $"\n\n{serpContext}";

            return result;
        }
    }
}
