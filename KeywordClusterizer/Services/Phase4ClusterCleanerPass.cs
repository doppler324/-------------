using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Phase 4.5: AI-чистка кластеров.
    ///
    /// Прогоняет кластеры через нейросеть (параллельно, с лимитом потоков):
    ///   Шаг 1 — выявляет запросы, не подходящие кластеру (валидация полноты: ничего не теряем).
    ///   Шаг 2 — распределяет вынесенные запросы по другим кластерам.
    /// Проходы повторяются до стабилизации (лимит MaxIterations).
    /// Запросы, не подошедшие ни к одному кластеру, уходят в «Нераспределённые» (обрабатывается последним).
    /// </summary>
    public class Phase4ClusterCleanerPass
    {
        private const string UnclusteredKey = "Нераспределённые";

        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly OpenRouterSettings _openRouterSettings;
        private readonly Phase4CleanSettings _cleanSettings;
        private readonly BusinessSettings? _businessSettings;

        /// <summary>Кандидаты-получатели в шаге 2: имена кластеров на текущую итерацию (без «Нераспределённые»).</summary>
        private HashSet<string> _candidateNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Строка консоли для перезаписываемого прогресса + блокировка записи.</summary>
        private int _progressLine;
        private int _lineWidth;
        private readonly object _consoleLock = new();

        /// <param name="cleanSettings">Настройки Phase 4.5 (провайдер, модель, лимит итераций, число потоков).</param>
        /// <param name="businessSettings">Опционально: ниша/логика — добавляется в системный промпт для контекста.</param>
        public Phase4ClusterCleanerPass(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            OpenRouterSettings openRouterSettings,
            Phase4CleanSettings cleanSettings,
            BusinessSettings? businessSettings = null)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _openRouterSettings = openRouterSettings;
            _cleanSettings = cleanSettings;
            _businessSettings = businessSettings;
        }

        /// <summary>
        /// Запускает чистку кластеров: итерации до стабилизации.
        /// Внутри итерации обычные кластеры обрабатываются параллельно (до MaxConcurrency),
        /// «Нераспределённые» — последним. Планы перемещений применяются последовательно.
        /// </summary>
        /// <param name="clusters">Словарь имя кластера → ключи. Модифицируется: создаётся/пополняется «Нераспределённые», пустые кластеры удаляются.</param>
        /// <returns>Очищенные кластеры (тот же словарь, изменённый in-place).</returns>
        public async Task<Dictionary<string, List<string>>> CleanAsync(Dictionary<string, List<string>> clusters)
        {
            ConsoleUtils.WriteLine(
                $"\n--- Фаза 4.5: AI-чистка кластеров (итераций до {_cleanSettings.MaxIterations}, потоков {_cleanSettings.MaxConcurrency}) ---",
                ConsoleColor.Cyan);

            var systemPrompts = LoadSystemPrompts();
            int iteration = 0;
            int totalRemoved = 0;

            while (iteration < _cleanSettings.MaxIterations)
            {
                iteration++;
                bool changed = false;

                Console.WriteLine($"\n[Итерация {iteration}/{_cleanSettings.MaxIterations}]");

                // Фиксируем кандидатов на старте итерации (обычные кластеры, без «Нераспределённые»)
                _candidateNames = new HashSet<string>(
                    clusters.Keys.Where(k => !IsUnclustered(k)),
                    StringComparer.OrdinalIgnoreCase);

                // Разделяем: обычные кластеры и «Нераспределённые» (последний)
                var normal = clusters.Where(kv => !IsUnclustered(kv.Key)).ToList();
                var unclustered = clusters.Where(kv => IsUnclustered(kv.Key)).ToList();

                _progressLine = Console.CursorTop;
                _lineWidth = Console.WindowWidth - 1;

                // Параллельная обработка обычных кластеров
                if (normal.Count > 0)
                {
                    var plans = await ProcessClustersParallelAsync(normal, systemPrompts);
                    int moved = ApplyPlans(clusters, plans);
                    totalRemoved += moved;
                    changed |= moved > 0;
                }

                // «Нераспределённые» — последним, со свежим списком кандидатов
                if (unclustered.Count > 0)
                {
                    _candidateNames = new HashSet<string>(
                        clusters.Keys.Where(k => !IsUnclustered(k)),
                        StringComparer.OrdinalIgnoreCase);

                    var plans = await ProcessClustersParallelAsync(unclustered, systemPrompts);
                    int moved = ApplyPlans(clusters, plans);
                    totalRemoved += moved;
                    changed |= moved > 0;
                }

                // Стираем прогресс-строку перед итоговым выводом
                ClearProgressLine();

                if (!changed)
                {
                    Console.WriteLine($"  Стабилизация достигнута на итерации {iteration} — изменений больше нет.");
                    break;
                }
            }

            // ==========================================
            // Гарантированный финальный проход по «Нераспределённым»
            // Выполняется ПОСЛЕ цикла итераций, чтобы «Нераспределённые», созданные только
            // в последней итерации (или из-за лимита MaxIterations), точно прошли шаг 2
            // (распределение их запросов по нормальным кластерам).
            // ==========================================
            if (clusters.TryGetValue(UnclusteredKey, out var unclFinal) && unclFinal.Count > 0)
            {
                Console.WriteLine("\n[Финальный проход] Распределение «Нераспределённых» по кластерам...");

                _candidateNames = new HashSet<string>(
                    clusters.Keys.Where(k => !IsUnclustered(k)),
                    StringComparer.OrdinalIgnoreCase);

                _progressLine = Console.CursorTop;
                _lineWidth = Console.WindowWidth - 1;

                var unclPlans = await ProcessClustersParallelAsync(
                    new List<KeyValuePair<string, List<string>>> { new(UnclusteredKey, unclFinal) },
                    systemPrompts);

                int unclMoved = ApplyPlans(clusters, unclPlans);
                totalRemoved += unclMoved;

                ClearProgressLine();

                if (unclMoved > 0)
                    Console.WriteLine($"  [Финальный проход] Перемещено запросов из «Нераспределённые»: {unclMoved}.");
                else
                    Console.WriteLine("  [Финальный проход] Изменений нет.");
            }

            // Удаляем пустые кластеры
            RemoveEmptyClusters(clusters);

            int unclusteredCount = clusters.TryGetValue(UnclusteredKey, out var uncl) ? uncl.Count : 0;
            ConsoleUtils.WriteLine(
                $"\n[Фаза 4.5] Готово: {clusters.Count} кластеров, перемещено запросов: {totalRemoved}, в «Нераспределённые»: {unclusteredCount}.",
                ConsoleColor.Cyan);

            return clusters;
        }

        /// <summary>
        /// Параллельно обрабатывает список кластеров (до MaxConcurrency одновременно).
        /// Возвращает планы перемещений, которые применяются вызывающим кодом последовательно.
        /// </summary>
        private async Task<List<ClusterMovePlan>> ProcessClustersParallelAsync(
            List<KeyValuePair<string, List<string>>> clusters,
            (string Step1, string Step2) systemPrompts)
        {
            if (clusters.Count == 0)
                return new List<ClusterMovePlan>();

            var maxConcurrency = Math.Max(1, _cleanSettings.MaxConcurrency);
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            int completed = 0;
            int total = clusters.Count;

            var tasks = clusters.Select(async kvp =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var plan = await AnalyzeClusterAsync(kvp.Key, kvp.Value, systemPrompts);
                    int done = Interlocked.Increment(ref completed);
                    WriteProgress(
                        $"  [Фаза 4.5] Обработано {done}/{total} кластеров... (текущий: «{kvp.Key}», вынесено {plan?.Removed.Count ?? 0})");
                    return plan;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(p => p != null).Select(p => p!).ToList();
        }

        /// <summary>
        /// Анализирует ОДИН кластер: шаг 1 (выявление лишних запросов) + шаг 2 (распределение).
        /// Не модифицирует словарь — только возвращает план перемещений.
        /// Для «Нераспределённых» шаг 1 пропускается (сразу распределение всех его запросов).
        /// </summary>
        private async Task<ClusterMovePlan?> AnalyzeClusterAsync(
            string clusterName, List<string> keywords, (string Step1, string Step2) systemPrompts)
        {
            if (keywords == null || keywords.Count == 0)
                return null;

            bool isUnclustered = IsUnclustered(clusterName);

            // Вынесенные запросы
            List<string> removed;
            if (isUnclustered)
            {
                removed = keywords.ToList();
            }
            else
            {
                removed = await AskRemoveAsync(clusterName, keywords, systemPrompts);
            }

            if (removed.Count == 0)
                return null;

            // Шаг 2: распределение вынесенных запросов по другим кластерам
            var targetCluster = await AskAssignAsync(removed, systemPrompts);

            return new ClusterMovePlan
            {
                Source = clusterName,
                Removed = removed,
                Targets = targetCluster
            };
        }

        /// <summary>
        /// Применяет планы перемещений последовательно (безопасно для общего словаря).
        /// Возвращает общее число перемещённых запросов.
        /// </summary>
        private int ApplyPlans(Dictionary<string, List<string>> clusters, List<ClusterMovePlan> plans)
        {
            int moved = 0;
            foreach (var plan in plans)
                moved += ApplyPlan(clusters, plan);
            return moved;
        }

        /// <summary>
        /// Применяет один план: удаляет вынесенные запросы из исходного кластера
        /// и добавляет их в целевые кластеры. Возвращает число перемещённых запросов.
        /// </summary>
        private int ApplyPlan(Dictionary<string, List<string>> clusters, ClusterMovePlan plan)
        {
            int movedToUnclustered = 0;

            if (!clusters.TryGetValue(plan.Source, out var source))
                return 0;

            foreach (var keyword in plan.Removed)
            {
                string norm = Normalize(keyword);
                string target = plan.Targets.TryGetValue(norm, out var t) ? t : UnclusteredKey;

                // Убираем из источника (удаляем по нормализованному совпадению)
                source.RemoveAll(k => Normalize(k) == norm);

                // Назначаем получателя
                bool isSelf = string.Equals(target, plan.Source, StringComparison.OrdinalIgnoreCase);
                bool hasTarget = clusters.TryGetValue(target, out var targetList);

                bool toUnclustered = IsUnclustered(target) || isSelf || !hasTarget;

                if (toUnclustered)
                {
                    movedToUnclustered++;
                    if (!clusters.TryGetValue(UnclusteredKey, out var uncl))
                    {
                        uncl = new List<string>();
                        clusters[UnclusteredKey] = uncl;
                    }
                    if (!uncl.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                        uncl.Add(keyword);
                }
                else if (targetList != null)
                {
                    if (!targetList.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                        targetList.Add(keyword);
                }
            }

            // Логируем перемещения по кластерам
            var byTarget = plan.Targets.GroupBy(x => x.Value)
                .Select(g => (Target: g.Key, Count: g.Count()))
                .Where(x => !IsUnclustered(x.Target) &&
                            !string.Equals(x.Target, plan.Source, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var (target, count) in byTarget)
                ConsoleUtils.WriteLine($"\n  [Шаг 2] «{plan.Source}» → «{target}»: {count} запросов.", ConsoleColor.DarkGray);

            return plan.Removed.Count;
        }

        /// <summary>
        /// Шаг 1: AI находит запросы, не подходящие кластеру.
        /// Валидация полноты: запросы возвращаются РОВНО из исходного списка кластера,
        /// ничего не теряется и не придумывается.
        /// </summary>
        private async Task<List<string>> AskRemoveAsync(
            string clusterName, List<string> keywords, (string Step1, string Step2) systemPrompts)
        {
            if (keywords.Count == 0)
                return new List<string>();

            // Индекс нормализованных ключей кластера → точная строка (для поиска ответа AI)
            var index = BuildKeywordIndex(keywords);

            var lines = new List<string> { $"Кластер: {clusterName}", $"Ключей: {keywords.Count}", "" };
            for (int i = 0; i < keywords.Count; i++)
                lines.Add($"{i + 1}. {keywords[i]}");

            string userMessage = string.Join("\n", lines);

            // Статус перед отправкой шага 1 — видно, что данные ушли и ждём ответ
            WriteProgress($"  [Шаг 1] «{clusterName}»: данные отправлены ({keywords.Count} ключей), ждём ответа нейросети...");
            var stopwatch1 = System.Diagnostics.Stopwatch.StartNew();

            var (response, error) = await DeepSeekHelper.SendWithRetryAsync<Phase4CleanRemoveResponse>(
                _client, systemPrompts.Step1, userMessage, BuildConfig(),
                maxRetries: 3, baseDelayMs: 5000,
                endpoint: Endpoint, apiKeyOverride: ApiKeyOverride, skipDeepSeekFields: UseOpenRouter);

            stopwatch1.Stop();
            WriteProgress($"  [Шаг 1] «{clusterName}»: ответ получен за {stopwatch1.Elapsed.TotalSeconds:F1}с.");

            if (response == null || response.Remove == null || response.Remove.Count == 0)
            {
                if (error != ApiErrorType.None && error != ApiErrorType.ParseError)
                    ConsoleUtils.WriteLine($"\n  [Шаг 1] Ошибка AI: {DeepSeekHelper.DescribeError(error)}. Кластер оставлен без изменений.", ConsoleColor.Yellow);
                return new List<string>();
            }

            // Сопоставляем ответ с реальными ключами кластера
            var result = new List<string>();
            var missing = new List<string>();
            foreach (var raw in response.Remove)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (index.TryGetValue(Normalize(raw), out var exact))
                {
                    if (!result.Contains(exact, StringComparer.OrdinalIgnoreCase))
                        result.Add(exact);
                }
                else
                {
                    missing.Add(raw); // AI вернула строку, которой нет в кластере — игнорируем
                }
            }

            if (missing.Count > 0)
                ConsoleUtils.WriteLine($"\n  [Шаг 1] «{clusterName}»: AI вернула {missing.Count} строк, которых нет в кластере — пропущены.", ConsoleColor.DarkYellow);

            return result;
        }

        /// <summary>
        /// Шаг 2: AI распределяет вынесенные запросы по кластерам-кандидатам.
        /// Возвращает словарь нормализованный запрос → имя целевого кластера.
        /// </summary>
        private async Task<Dictionary<string, string>> AskAssignAsync(
            List<string> removed, (string Step1, string Step2) systemPrompts)
        {
            var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (removed.Count == 0)
                return target;

            // Имена кандидатов без «Нераспределённые»
            var candidates = _candidateNames.ToList();
            if (candidates.Count == 0)
            {
                // Нет других кластеров — всё в «Нераспределённые»
                foreach (var k in removed)
                    target[Normalize(k)] = UnclusteredKey;
                return target;
            }

            var lines = new List<string>
            {
                "Вынесенные запросы:",
                ""
            };
            for (int i = 0; i < removed.Count; i++)
                lines.Add($"{i + 1}. {removed[i]}");

            lines.Add("");
            lines.Add("Доступные кластеры для распределения:");
            for (int i = 0; i < candidates.Count; i++)
                lines.Add($"{i + 1}. {candidates[i]}");

            string userMessage = string.Join("\n", lines);

            // Статус перед отправкой шага 2 — видно, что данные ушли (запросы + кандидаты) и ждём ответ
            WriteProgress($"  [Шаг 2] Данные отправлены ({removed.Count} запросов, {candidates.Count} кластеров-кандидатов), ждём ответа нейросети...");
            var stopwatch2 = System.Diagnostics.Stopwatch.StartNew();

            var (response, error) = await DeepSeekHelper.SendWithRetryAsync<Phase4CleanAssignResponse>(
                _client, systemPrompts.Step2, userMessage, BuildConfig(),
                maxRetries: 3, baseDelayMs: 5000,
                endpoint: Endpoint, apiKeyOverride: ApiKeyOverride, skipDeepSeekFields: UseOpenRouter);

            stopwatch2.Stop();
            WriteProgress($"  [Шаг 2] Ответ получен за {stopwatch2.Elapsed.TotalSeconds:F1}с.");

            if (response == null || response.Assignments == null || response.Assignments.Count == 0)
            {
                ConsoleUtils.WriteLine($"\n  [Шаг 2] Ошибка AI: {DeepSeekHelper.DescribeError(error)}. Запросы уходят в «Нераспределённые».", ConsoleColor.Yellow);
                foreach (var k in removed)
                    target[Normalize(k)] = UnclusteredKey;
                return target;
            }

            // Индекс вынесенных запросов
            var removedIndex = BuildKeywordIndex(removed);

            foreach (var assignment in response.Assignments)
            {
                if (string.IsNullOrWhiteSpace(assignment.Keyword))
                    continue;

                string normKeyword = Normalize(assignment.Keyword);
                if (!removedIndex.TryGetValue(normKeyword, out _))
                    continue; // AI вернула запрос, которого нет среди вынесенных — игнорируем

                string cluster = string.IsNullOrWhiteSpace(assignment.Cluster)
                    ? UnclusteredKey
                    : assignment.Cluster.Trim();

                // Если кластер не существует или это «Нераспределённые» — оставляем как есть
                if (IsUnclustered(cluster) || !_candidateNames.Contains(cluster))
                    cluster = UnclusteredKey;

                target[normKeyword] = cluster;
            }

            // Запросы, не упомянутые в ответе — в «Нераспределённые»
            foreach (var k in removed)
            {
                string norm = Normalize(k);
                if (!target.ContainsKey(norm))
                    target[norm] = UnclusteredKey;
            }

            return target;
        }

        /// <summary>Удаляет пустые кластеры из словаря (пустые не нужны в результате).</summary>
        private static void RemoveEmptyClusters(Dictionary<string, List<string>> clusters)
        {
            var empty = clusters.Where(kv => kv.Value == null || kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            foreach (var name in empty)
                clusters.Remove(name);
        }

        /// <summary>
        /// Строит индекс: нормализованная строка → точная строка из списка.
        /// Нужен для сопоставления ответа AI с реальными ключами (регистронезависимо, без учёта лишних пробелов).
        /// </summary>
        private static Dictionary<string, string> BuildKeywordIndex(List<string> keywords)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k))
                    continue;
                string norm = Normalize(k);
                if (!index.ContainsKey(norm))
                    index[norm] = k;
            }
            return index;
        }

        /// <summary>Нормализует ключ: схлопывает пробелы, приводит к нижнему регистру.</summary>
        private static string Normalize(string keyword) =>
            string.Join(' ', keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        /// <summary>true, если имя кластера — «Нераспределённые» (регистронезависимо).</summary>
        private static bool IsUnclustered(string name) =>
            string.Equals(name, UnclusteredKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>Загружает системные промпты из файлов инструкций.</summary>
        private (string Step1, string Step2) LoadSystemPrompts()
        {
            string step1 = LoadInstruction("instructions/phase4_clean_step1.txt");
            string step2 = LoadInstruction("instructions/phase4_clean_step2.txt");

            // Добавляем бизнес-контекст (ниша/логика) к обоим промптам, если он задан
            if (_businessSettings != null)
            {
                string ctx = $"\nНиша сайта: {_businessSettings.Niche}. Логика кластеризации: {_businessSettings.ClusteringLogic}.";
                step1 += ctx;
                step2 += ctx;
            }

            return (step1, step2);
        }

        /// <summary>Загружает содержимое файла инструкции. При отсутствии — возвращает базовую заглушку.</summary>
        private static string LoadInstruction(string filePath)
        {
            if (File.Exists(filePath))
                return File.ReadAllText(filePath).Trim();

            ConsoleUtils.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл '{filePath}' не найден.", ConsoleColor.Yellow);
            return "Верни ответ строго в формате JSON. Никакого текста до или после JSON.";
        }

        /// <summary>true, если выбран OpenRouter (провайдер из настроек Phase 4.5).</summary>
        private bool UseOpenRouter =>
            _cleanSettings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase);

        /// <summary>Endpoint для OpenRouter, иначе null (по умолчанию DeepSeek).</summary>
        private string? Endpoint => UseOpenRouter ? "https://openrouter.ai/api/v1/chat/completions" : null;

        /// <summary>API-ключ для OpenRouter, иначе null (используется ключ DeepSeek).</summary>
        private string? ApiKeyOverride => UseOpenRouter ? _openRouterSettings.ApiKey : null;

        /// <summary>
        /// Собирает DeepSeekSettings для вызова AI из настроек Phase 4.5,
        /// подставляя значения из phase4/deepseek, где не заданы свои.
        /// </summary>
        private DeepSeekSettings BuildConfig()
        {
            return new DeepSeekSettings
            {
                ApiKey = _deepSeekSettings.ApiKey,
                Model = !string.IsNullOrEmpty(_cleanSettings.Model)
                    ? _cleanSettings.Model : _deepSeekSettings.Model,
                Temperature = _cleanSettings.Temperature ?? _deepSeekSettings.Temperature,
                MaxTokens = _cleanSettings.MaxTokens ?? _deepSeekSettings.MaxTokens,
                TopP = _deepSeekSettings.TopP,
                EnableThinking = _cleanSettings.EnableThinking ?? _deepSeekSettings.EnableThinking,
                ReasoningEffort = _cleanSettings.ReasoningEffort ?? _deepSeekSettings.ReasoningEffort,
                Stream = _cleanSettings.Stream ?? _deepSeekSettings.Stream
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

        /// <summary>
        /// План перемещения одного кластера: из какого кластера, какие запросы,
        /// куда их назначить. Собирается параллельно, применяется последовательно.
        /// </summary>
        private sealed class ClusterMovePlan
        {
            public string Source { get; set; } = "";
            public List<string> Removed { get; set; } = new();
            public Dictionary<string, string> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
