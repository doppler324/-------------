using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// Сервис для чистки ключевых запросов с помощью AI.
    /// Разбивает ключи на пулы, отправляет каждый пул нейросети (многопоточно),
    /// собирает результаты и сохраняет в файлы.
    /// </summary>
    public class KeywordCleanerService
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly CleanerSettings _cleanerSettings;

        /// <summary>Блокировка для атомарной дописки результатов пула в файлы из разных потоков.</summary>
        private static readonly object FileWriteLock = new();

        /// <summary>Результаты обработки всех пулов, собираемые многопоточно.</summary>
        private class PoolResults
        {
            public readonly ConcurrentBag<string> Cleaned = new();
            public readonly ConcurrentBag<string> Discarded = new();
            public readonly ConcurrentBag<string> Branded = new();
            public readonly ConcurrentBag<string> Failed = new(); // провалившиеся пулы + missed-ключи
            private int _processedCount;

            public int ProcessedCount => _processedCount;
            public void AddProcessed(int amount) => Interlocked.Add(ref _processedCount, amount);
        }

        public KeywordCleanerService(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            OpenRouterSettings openRouterSettings,
            CleanerSettings cleanerSettings)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _cleanerSettings = cleanerSettings;
        }

        /// <summary>
        /// Запускает процесс чистки ключевых запросов.
        /// Обрабатывает пулы многопоточно с ограничением по maxConcurrency.
        /// </summary>
        /// <param name="keywords">Список ключевых слов для чистки.</param>
        /// <param name="queryType">Тип запроса (Informational / Commercial).</param>
        /// <param name="topic">Тема/ниша ключей. Будет вставлена в промпт для контекста.</param>
        /// <param name="additionalPrompt">Дополнительные инструкции от пользователя для AI.</param>
        /// <param name="maxConcurrency">Максимальное количество одновременных потоков (по умолчанию 10).</param>
        /// <param name="brandHandling">Куда отправлять брендовые запросы.</param>
        /// <param name="selectedModel">Модель нейросети (переопределяет cleaner.defaultModel).</param>
        /// <param name="endpoint">API endpoint (null = автоопределение).</param>
        /// <param name="apiKeyOverride">Ключ API (null = используется apiKey из настроек).</param>
        /// <param name="skipDeepSeekFields">true — не отправлять DeepSeek-specific поля (для OpenRouter).</param>
        public async Task RunAsync(
            List<string> keywords,
            QueryType queryType,
            string? topic = null,
            string? additionalPrompt = null,
            int maxConcurrency = 10,
            BrandHandling brandHandling = BrandHandling.SeparateFile,
            string? selectedModel = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false)
        {
            ConsoleUtils.WriteLine("\n=== Чистка ключевых запросов ===", ConsoleColor.Cyan);

            string? systemPrompt = BuildSystemPrompt(queryType, topic, additionalPrompt, brandHandling);
            if (systemPrompt == null)
                return;

            string queryTypeLabel = queryType == QueryType.Informational ? "информационных" : "коммерческих";
            keywords = DeduplicateWithLog(keywords);

            string model = selectedModel ?? _cleanerSettings.DefaultModel;
            _deepSeekSettings.Model = model;
            Console.WriteLine($"Модель: {model}");

            var pools = SplitIntoPools(keywords, _cleanerSettings.DefaultPoolSize);
            Console.WriteLine($"Пулов по {_cleanerSettings.DefaultPoolSize} ключей: {pools.Count}");
            Console.WriteLine($"Потоков: {maxConcurrency}");

            PrepareOutputFiles(); // очищаем выходные файлы перед новым запуском, чтобы не смешивать со старыми данными

            var results = new PoolResults();
            await ProcessAllPoolsAsync(pools, systemPrompt, endpoint, skipDeepSeekFields,
                apiKeyOverride, brandHandling, maxConcurrency, results);

            SaveResults(results, queryTypeLabel);
            PrintSummary(results);
        }

        /// <summary>
        /// Собирает системный промпт из базовой инструкции, темы, доп. инструкций пользователя
        /// и правил обработки брендов. Возвращает null, если файл инструкции не найден.
        /// </summary>
        private string? BuildSystemPrompt(QueryType queryType, string? topic, string? additionalPrompt, BrandHandling brandHandling)
        {
            string? basePrompt = _cleanerSettings.LoadPrompt(queryType);
            if (basePrompt == null)
                return null;

            var parts = new List<string> { basePrompt };

            if (!string.IsNullOrWhiteSpace(topic))
            {
                parts.Add($"\nТема/ниша ключевых запросов: {topic}.");
                parts.Add("Учитывай эту тему при анализе — запросы, не соответствующие теме, отправляй в discarded.");
            }

            if (!string.IsNullOrWhiteSpace(additionalPrompt))
                parts.Add($"\nДОПОЛНИТЕЛЬНЫЕ ИНСТРУКЦИИ ОТ ПОЛЬЗОВАТЕЛЯ:\n{additionalPrompt}");

            string? brandInstruction = _cleanerSettings.LoadBrandInstruction(brandHandling);
            if (brandInstruction != null)
                parts.Add($"\n{brandInstruction}");

            Console.WriteLine($"Тип отбора: {(queryType == QueryType.Informational ? "информационных" : "коммерческих")}");
            if (!string.IsNullOrWhiteSpace(topic)) Console.WriteLine($"Тема: {topic}");
            if (!string.IsNullOrWhiteSpace(additionalPrompt)) Console.WriteLine("Доп. инструкции: добавлены");

            return string.Join("\n", parts);
        }

        /// <summary>Удаляет дубликаты (без учёта регистра) и печатает предупреждение, если что-то удалено.</summary>
        private static List<string> DeduplicateWithLog(List<string> keywords)
        {
            var unique = keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int removed = keywords.Count - unique.Count;
            if (removed > 0)
                ConsoleUtils.WriteLine($"[ВНИМАНИЕ] Удалено {removed} дубликатов из входного файла.", ConsoleColor.DarkYellow);

            Console.WriteLine($"Всего уникальных ключей: {unique.Count}");
            return unique;
        }

        /// <summary>Запускает обработку всех пулов параллельно с ограничением по maxConcurrency.</summary>
        private async Task ProcessAllPoolsAsync(
            List<List<string>> pools, string systemPrompt, string? endpoint, bool skipDeepSeekFields,
            string? apiKeyOverride, BrandHandling brandHandling, int maxConcurrency, PoolResults results)
        {
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var originalAuth = _client.DefaultRequestHeaders.Authorization;

            if (!string.IsNullOrEmpty(apiKeyOverride))
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);

            var tasks = pools.Select(async (pool, index) =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await ProcessPoolAsync(index, pool, systemPrompt, endpoint, skipDeepSeekFields, brandHandling, results, pools.Count);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (!string.IsNullOrEmpty(apiKeyOverride))
                _client.DefaultRequestHeaders.Authorization = originalAuth;
        }

        /// <summary>
        /// Нормализует ключевое слово для сравнения: схлопывает повторяющиеся пробелы, приводит к нижнему регистру.
        /// Нужно, чтобы незначительные различия в пунктуации/пробелах в ответе AI не считались "пропущенным" ключом.
        /// </summary>
        private static string NormalizeKeyword(string keyword) =>
            string.Join(' ', keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        /// <summary>
        /// Обрабатывает один пул: отправляет запрос AI (с retry), валидирует ответ,
        /// маршрутизирует ключи по спискам cleaned/branded/discarded/failed.
        /// </summary>
        private async Task ProcessPoolAsync(
            int poolIndex, List<string> poolKeywords, string systemPrompt,
            string? endpoint, bool skipDeepSeekFields, BrandHandling brandHandling,
            PoolResults results, int totalPools)
        {
            ConsoleUtils.Write($"\n[{poolIndex + 1}/{totalPools}] Отправка пула ({poolKeywords.Count} ключей)... ", ConsoleColor.Yellow);

            string userMessage = string.Join("\n", poolKeywords);
            var (response, error) = await DeepSeekHelper.SendWithRetryAsync<CleanerResponse>(
                _client, systemPrompt, userMessage, _deepSeekSettings,
                maxRetries: 3, baseDelayMs: 5000,
                endpoint: endpoint, apiKeyOverride: null, skipDeepSeekFields: skipDeepSeekFields);

            if (response == null)
            {
                FailPool(poolIndex, poolKeywords, error, results);
                return;
            }

            var classification = ClassifyPoolResponse(poolKeywords, response, brandHandling);
            AddRange(results.Cleaned, classification.Cleaned);
            AddRange(results.Branded, classification.Branded);
            AddRange(results.Discarded, classification.Discarded);
            AddRange(results.Failed, classification.Missed); // missed при SeparateFile идут сюда же, что и провалившиеся пулы

            // Сразу после обработки пула дописываем результаты в файлы — защита от потери при сбое
            AppendToFile(_cleanerSettings.OutputCleaned, classification.Cleaned);
            AppendToFile(_cleanerSettings.OutputBranded, classification.Branded);
            AppendToFile(_cleanerSettings.OutputDiscarded, classification.Discarded);
            AppendToFile(_cleanerSettings.OutputFailed, classification.Missed);

            results.AddProcessed(poolKeywords.Count);
            LogPoolOutcome(classification, results.ProcessedCount);
        }

        /// <summary>Помещает все ключи провалившегося пула в failed и печатает причину.</summary>
        private void FailPool(int poolIndex, List<string> poolKeywords, ApiErrorType error, PoolResults results)
        {
            string reason = error == ApiErrorType.Unauthorized
                ? "неверный API ключ (401) — дальнейшая обработка бессмысленна"
                : $"все попытки исчерпаны ({DeepSeekHelper.DescribeError(error)})";

            ConsoleUtils.WriteLine($"\n[ОШИБКА] Пул {poolIndex + 1} — {reason}. Ключи сохранены в failed.txt.", ConsoleColor.Red);

            AddRange(results.Failed, poolKeywords);
            AppendToFile(_cleanerSettings.OutputFailed, poolKeywords); // дописываем сразу, чтобы не потерять при сбое
            results.AddProcessed(poolKeywords.Count);
        }

        private static void AddRange(ConcurrentBag<string> bag, IEnumerable<string> items)
        {
            foreach (var item in items) bag.Add(item);
        }

        /// <summary>Итог классификации одного пула — для передачи между валидацией и логированием.</summary>
        private readonly struct PoolClassification
        {
            public readonly List<string> Cleaned, Branded, Discarded, Missed;
            public PoolClassification(List<string> cleaned, List<string> branded, List<string> discarded, List<string> missed)
            {
                Cleaned = cleaned; Branded = branded; Discarded = discarded; Missed = missed;
            }
        }

        /// <summary>
        /// Валидирует и маршрутизирует ответ AI по пулу:
        /// 1) сравнивает нормализованные версии ключей, чтобы не терять их из-за мелких различий в пунктуации;
        /// 2) убирает пересечения между списками (branded приоритетнее discarded);
        /// 3) оставляет только ключи, реально присутствовавшие во входном пуле;
        /// 4) ключи, не упомянутые AI вообще ("missed"), маршрутизирует согласно brandHandling.
        /// </summary>
        private static PoolClassification ClassifyPoolResponse(List<string> poolKeywords, CleanerResponse response, BrandHandling brandHandling)
        {
            // original -> normalized, плюс обратная карта normalized -> первый оригинал (на случай дублей после нормализации)
            var originalToNormalized = poolKeywords.Select(k => (Original: k, Normalized: NormalizeKeyword(k))).ToList();
            var normalizedToOriginal = originalToNormalized
                .GroupBy(x => x.Normalized, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Original, StringComparer.OrdinalIgnoreCase);
            var poolNormalizedSet = new HashSet<string>(normalizedToOriginal.Keys, StringComparer.OrdinalIgnoreCase);

            // Нормализуем и фильтруем каждый список ответа AI, оставляя только ключи из входного пула
            List<string> NormalizeAndFilter(IEnumerable<string>? aiList) =>
                (aiList ?? Enumerable.Empty<string>())
                    .Select(NormalizeKeyword)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Intersect(poolNormalizedSet, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            var cleanedNorm = NormalizeAndFilter(response.Cleaned);
            var brandedNorm = NormalizeAndFilter(response.Branded).Except(cleanedNorm, StringComparer.OrdinalIgnoreCase).ToList();
            var discardedNorm = NormalizeAndFilter(response.Discarded).Except(brandedNorm, StringComparer.OrdinalIgnoreCase).ToList();

            var aiReturnedNorm = new HashSet<string>(cleanedNorm, StringComparer.OrdinalIgnoreCase);
            aiReturnedNorm.UnionWith(brandedNorm);
            aiReturnedNorm.UnionWith(discardedNorm);

            var missed = originalToNormalized
                .Where(k => !aiReturnedNorm.Contains(k.Normalized))
                .Select(k => k.Original)
                .ToList();

            List<string> ToOriginal(List<string> normalizedList) =>
                normalizedList.Select(n => normalizedToOriginal.GetValueOrDefault(n, n)).ToList();

            var cleaned = ToOriginal(cleanedNorm);
            var branded = ToOriginal(brandedNorm);
            var discarded = ToOriginal(discardedNorm);

            // Маршрутизация брендов и "потерянных" AI ключей согласно выбору пользователя
            switch (brandHandling)
            {
                case BrandHandling.ToDiscarded:
                    discarded.AddRange(branded);
                    discarded.AddRange(missed);
                    branded.Clear();
                    missed = new List<string>();
                    break;
                case BrandHandling.KeepAsIs:
                    cleaned.AddRange(branded);
                    cleaned.AddRange(missed);
                    branded.Clear();
                    missed = new List<string>();
                    break;
                case BrandHandling.SeparateFile:
                default:
                    // missed остаётся как есть — уйдёт в failed.txt для ручной проверки/повторной обработки
                    break;
            }

            return new PoolClassification(cleaned, branded, discarded, missed);
        }

        /// <summary>Выводит итог обработки одного пула. Вывод синхронизирован, чтобы строки не перемешивались между потоками.</summary>
        private static void LogPoolOutcome(PoolClassification c, int totalProcessed)
        {
            lock (typeof(Console))
            {
                ConsoleUtils.WriteLine($"OK (clean: {c.Cleaned.Count}, brand: {c.Branded.Count}, discard: {c.Discarded.Count})", ConsoleColor.Green);
                Console.WriteLine($"  Прогресс: {totalProcessed} ключей");
                if (c.Missed.Count > 0 || c.Branded.Count > 0)
                    ConsoleUtils.WriteLine($"  [ВАЛИДАЦИЯ] пропущено AI: {c.Missed.Count}, брендов: {c.Branded.Count}", ConsoleColor.DarkYellow);
            }
        }

        /// <summary>Разбивает список ключей на пулы указанного размера.</summary>
        private static List<List<string>> SplitIntoPools(List<string> keywords, int poolSize)
        {
            var pools = new List<List<string>>();
            for (int i = 0; i < keywords.Count; i += poolSize)
                pools.Add(keywords.Skip(i).Take(poolSize).ToList());
            return pools;
        }

        /// <summary>
        /// Очищает выходные файлы результатов перед началом чистки.
        /// Нужно, чтобы результаты нового запуска не смешивались с данными от предыдущих.
        /// </summary>
        private void PrepareOutputFiles()
        {
            try
            {
                File.WriteAllLines(_cleanerSettings.OutputCleaned, Array.Empty<string>());
                File.WriteAllLines(_cleanerSettings.OutputBranded, Array.Empty<string>());
                File.WriteAllLines(_cleanerSettings.OutputDiscarded, Array.Empty<string>());
                File.WriteAllLines(_cleanerSettings.OutputFailed, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine($"[ОШИБКА] Не удалось подготовить файлы результатов: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// Дописывает список ключей в конец файла под блокировкой (вызывается из каждого потока сразу после обработки пула).
        /// Защита от потери результатов при сбое: обработанные пулы уже лежат в файлах до завершения всей чистки.
        /// </summary>
        private static void AppendToFile(string path, IEnumerable<string> items)
        {
            var list = items as List<string> ?? items.ToList();
            if (list.Count == 0) return;

            lock (FileWriteLock)
            {
                try
                {
                    File.AppendAllLines(path, list);
                }
                catch (Exception ex)
                {
                    ConsoleUtils.WriteLine($"[ОШИБКА] Не удалось дописать {path}: {ex.Message}", ConsoleColor.Red);
                }
            }
        }

        /// <summary>Сохраняет один список ключей в файл, печатая результат операции.</summary>
        private static void SaveList(string path, List<string> items, string label)
        {
            if (items.Count == 0) return;

            try
            {
                File.WriteAllLines(path, items);
                ConsoleUtils.WriteLine($"[УСПЕХ] {label} сохранены: {path} ({items.Count} ключей)", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine($"[ОШИБКА] Не удалось сохранить {path}: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>Сохраняет все категории результатов в соответствующие файлы.</summary>
        private void SaveResults(PoolResults results, string queryTypeLabel)
        {
            var cleaned = results.Cleaned.ToList();
            var branded = results.Branded.ToList();
            var discarded = results.Discarded.ToList();
            var failed = results.Failed.ToList();

            Console.WriteLine(); // визуальный отступ перед блоком сохранения
            SaveList(_cleanerSettings.OutputCleaned, cleaned, $"Релевантные ({queryTypeLabel})");
            SaveList(_cleanerSettings.OutputBranded, branded, "Брендовые");
            SaveList(_cleanerSettings.OutputDiscarded, discarded, "Отброшенные");

            if (failed.Count > 0)
            {
                SaveList(_cleanerSettings.OutputFailed, failed, "[FAIL] Необработанные");
                Console.WriteLine("  Запустите чистку с этим файлом для повторной обработки.");
            }
        }

        /// <summary>Печатает итоговую статистику чистки.</summary>
        private void PrintSummary(PoolResults results)
        {
            int cleaned = results.Cleaned.Count, branded = results.Branded.Count;
            int discarded = results.Discarded.Count, failed = results.Failed.Count;
            int processed = results.ProcessedCount;
            int classified = cleaned + branded + discarded + failed;

            ConsoleUtils.WriteLine("\n=================================================", ConsoleColor.Green);
            ConsoleUtils.WriteLine("              РЕЗУЛЬТАТ ЧИСТКИ                   ", ConsoleColor.Green);
            ConsoleUtils.WriteLine("=================================================", ConsoleColor.Green);

            Console.WriteLine($"Всего обработано:   {processed} ключей");
            Console.WriteLine($"Из них классифицировано: {classified}");
            if (classified != processed)
                ConsoleUtils.WriteLine($"  (расхождение: {processed - classified} — дубликаты в пулах)", ConsoleColor.DarkYellow);

            ConsoleUtils.WriteLine($"Релевантных:        {cleaned}", ConsoleColor.Green);
            if (branded > 0)
                ConsoleUtils.WriteLine($"Брендовых:          {branded}", ConsoleColor.Cyan);
            ConsoleUtils.WriteLine($"Отброшено:          {discarded}", ConsoleColor.DarkYellow);

            if (failed > 0)
            {
                ConsoleUtils.WriteLine($"НЕ ОБРАБОТАНО:      {failed} ключей", ConsoleColor.Red);
                ConsoleUtils.WriteLine($"  (сохранены в {_cleanerSettings.OutputFailed} — можно повторно запустить чистку с этим файлом)", ConsoleColor.Red);
            }
        }
    }
}
