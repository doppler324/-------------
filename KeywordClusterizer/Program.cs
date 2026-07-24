using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Кластеризатор ключевых слов ===");

            // === Меню выбора режима ===
            Console.WriteLine("\nВыберите режим работы:");
            Console.WriteLine("  1 - Кластеризация ключевых слов");
            Console.WriteLine("  2 - Чистка ключевых запросов");
            Console.Write("\nВаш выбор (1/2): ");
            string modeChoice = Console.ReadLine()?.Trim() ?? "1";

            if (modeChoice == "2")
                await RunCleanerModeAsync();
            else
                await RunClusterizationModeAsync();

            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
        }

        /// <summary>
        /// Режим кластеризации (существующий функционал).
        /// </summary>
        static async Task RunClusterizationModeAsync()
        {
            // 1. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var businessSettings = new BusinessSettings();
            var serpSettings = new XmlRiverSettings();
            var openRouterSettings = new OpenRouterSettings();
            var phase4Settings = new Phase4Settings();

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Чтение API ключа
                    deepSeekSettings.ApiKey = root.GetProperty("apiKey").GetString()?.Trim() ?? "";

                    // Чтение параметров нейросети
                    if (root.TryGetProperty("deepseek", out var deepseek))
                    {
                        if (deepseek.TryGetProperty("model", out var m))
                            deepSeekSettings.Model = m.GetString() ?? deepSeekSettings.Model;

                        if (deepseek.TryGetProperty("refactoringModel", out var rm))
                            deepSeekSettings.RefactoringModel = rm.GetString() ?? "";

                        if (deepseek.TryGetProperty("temperature", out var t))
                            deepSeekSettings.Temperature = t.GetDouble();

                        if (deepseek.TryGetProperty("maxTokens", out var mt))
                            deepSeekSettings.MaxTokens = mt.GetInt32();

                        if (deepseek.TryGetProperty("topP", out var tp))
                            deepSeekSettings.TopP = tp.GetDouble();

                        if (deepseek.TryGetProperty("enableThinking", out var et))
                            deepSeekSettings.EnableThinking = et.GetBoolean();

                        if (deepseek.TryGetProperty("reasoningEffort", out var re))
                            deepSeekSettings.ReasoningEffort = re.GetString() ?? deepSeekSettings.ReasoningEffort;

                        if (deepseek.TryGetProperty("stream", out var st))
                            deepSeekSettings.Stream = st.GetBoolean();
                    }

                    // Чтение бизнес-настроек
                    if (root.TryGetProperty("business", out var business))
                    {
                        if (business.TryGetProperty("niche", out var n))
                            businessSettings.Niche = n.GetString() ?? "";

                        if (business.TryGetProperty("clusteringLogic", out var cl))
                            businessSettings.ClusteringLogic = cl.GetString() ?? "";

                        if (business.TryGetProperty("granularityRule", out var gr))
                            businessSettings.GranularityRule = gr.GetString() ?? "";

                        if (business.TryGetProperty("skipPhase4", out var sp4))
                            businessSettings.SkipPhase4 = sp4.GetBoolean();

                        if (business.TryGetProperty("suppressClusterDisplay", out var scd))
                            businessSettings.SuppressClusterDisplay = scd.GetBoolean();

                        if (business.TryGetProperty("skipNaming", out var sn))
                            businessSettings.SkipNaming = sn.GetBoolean();

                        if (business.TryGetProperty("skipMerge", out var sm))
                            businessSettings.SkipMerge = sm.GetBoolean();

                        if (business.TryGetProperty("sentenceLevelClustering", out var slc))
                        {
                            if (slc.TryGetProperty("enabled", out var slce))
                                businessSettings.SentenceLevelClusteringEnabled = slce.GetBoolean();

                            if (slc.TryGetProperty("sentenceHacThreshold", out var sht))
                                businessSettings.SentenceHacThreshold = (float)sht.GetDouble();
                        }

                        if (business.TryGetProperty("macroMerge", out var macroM))
                        {
                            if (macroM.TryGetProperty("enabled", out var en))
                                businessSettings.MacroMergeEnabled = en.GetBoolean();

                            if (macroM.TryGetProperty("representativeMode", out var rm))
                                businessSettings.RepresentativeMode = rm.GetString() ?? "centroid";

                            if (macroM.TryGetProperty("similarityThreshold", out var st))
                                businessSettings.MacroMergeThreshold = (float)st.GetDouble();
                        }

                        if (business.TryGetProperty("rescuePassV2", out var rp2))
                        {
                            if (rp2.TryGetProperty("enabled", out var en2))
                                businessSettings.RescuePassV2Enabled = en2.GetBoolean();

                            if (rp2.TryGetProperty("rescueThreshold", out var rt))
                                businessSettings.RescueThreshold = (float)rt.GetDouble();
                        }
                    }

                    // Чтение SERP-настроек
                    if (root.TryGetProperty("serp", out var serp))
                    {
                        if (serp.TryGetProperty("provider", out var p))
                            serpSettings.Provider = p.GetString() ?? serpSettings.Provider;

                        if (serp.TryGetProperty("xmlriverUser", out var xu))
                            serpSettings.XmlriverUser = xu.GetString() ?? "";

                        if (serp.TryGetProperty("xmlriverKey", out var xk))
                            serpSettings.XmlriverKey = xk.GetString() ?? "";

                        if (serp.TryGetProperty("topResultsCount", out var trc))
                            serpSettings.TopResultsCount = trc.GetInt32();

                        if (serp.TryGetProperty("maxRetries", out var mr))
                            serpSettings.MaxRetries = mr.GetInt32();

                        if (serp.TryGetProperty("retryDelayMs", out var rd))
                            serpSettings.RetryDelayMs = rd.GetInt32();

                        if (serp.TryGetProperty("maxConcurrency", out var mc))
                            serpSettings.MaxConcurrency = mc.GetInt32();

                        if (serp.TryGetProperty("enableSerpFirst", out var esf))
                            serpSettings.EnableSerpFirst = esf.GetBoolean();

                        if (serp.TryGetProperty("overlapThreshold", out var ot))
                            serpSettings.OverlapThreshold = ot.GetInt32();

                        if (serp.TryGetProperty("enableCache", out var ec))
                            serpSettings.EnableCache = ec.GetBoolean();

                        if (serp.TryGetProperty("cachePath", out var cp))
                            serpSettings.CachePath = cp.GetString() ?? serpSettings.CachePath;
                    }

                    // Чтение OpenRouter-настроек
                    if (root.TryGetProperty("openrouter", out var openrouter))
                    {
                        if (openrouter.TryGetProperty("apiKey", out var orKey))
                            openRouterSettings.ApiKey = orKey.GetString()?.Trim() ?? "";

                        if (openrouter.TryGetProperty("embeddingModel", out var orModel))
                            openRouterSettings.EmbeddingModel = orModel.GetString() ?? openRouterSettings.EmbeddingModel;

                        if (openrouter.TryGetProperty("embeddingDimensions", out var orDim))
                            openRouterSettings.EmbeddingDimensions = orDim.GetInt32();

                        if (openrouter.TryGetProperty("cachePath", out var orCache))
                            openRouterSettings.CachePath = orCache.GetString() ?? openRouterSettings.CachePath;
                    }

                    // Чтение Phase 4 настроек
                    if (root.TryGetProperty("phase4", out var phase4El))
                    {
                        if (phase4El.TryGetProperty("provider", out var prov))
                            phase4Settings.Provider = prov.GetString() ?? phase4Settings.Provider;

                        if (phase4El.TryGetProperty("model", out var model))
                            phase4Settings.Model = model.GetString() ?? "";

                        if (phase4El.TryGetProperty("temperature", out var temp))
                            phase4Settings.Temperature = temp.GetDouble();

                        if (phase4El.TryGetProperty("maxTokens", out var mt))
                            phase4Settings.MaxTokens = mt.GetInt32();

                        if (phase4El.TryGetProperty("enableThinking", out var et))
                            phase4Settings.EnableThinking = et.GetBoolean();

                        if (phase4El.TryGetProperty("reasoningEffort", out var re))
                            phase4Settings.ReasoningEffort = re.GetString();

                        if (phase4El.TryGetProperty("stream", out var st))
                            phase4Settings.Stream = st.GetBoolean();
                    }
                }
                catch
                {
                    // используем значения по умолчанию
                }
            }

            if (string.IsNullOrWhiteSpace(deepSeekSettings.ApiKey) ||
                deepSeekSettings.ApiKey == "ВАШ_DEEPSEEK_API_KEY")
            {
                Console.Write("Введите ваш API ключ DeepSeek: ");
                deepSeekSettings.ApiKey = ReadPassword();
            }

            if (string.IsNullOrWhiteSpace(deepSeekSettings.ApiKey))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("API ключ не предоставлен. Выход...");
                Console.ResetColor();
                return;
            }

            // 3. Выбор файла с ключевыми словами
            Console.WriteLine("\nВыберите файл с ключевыми словами:");
            Console.WriteLine("  1 - keywords.txt (исходные)");
            Console.WriteLine("  2 - cleaned.txt  (после чистки)");
            Console.Write("\nВаш выбор (1/2): ");
            string fileChoice = Console.ReadLine()?.Trim() ?? "1";

            string filePath = fileChoice == "2" ? "cleaned.txt" : "keywords.txt";

            if (!File.Exists(filePath))
            {
                // Если выбранного файла нет — пробуем другой
                string altPath = fileChoice == "2" ? "keywords.txt" : "cleaned.txt";

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл '{filePath}' не найден.");
                Console.ResetColor();

                if (File.Exists(altPath))
                {
                    Console.Write($"Использовать '{altPath}'? (y/n): ");
                    string? answer = Console.ReadLine()?.Trim().ToLower();
                    if (answer == "y" || answer == "yes" || answer == "д" || answer == "да")
                    {
                        filePath = altPath;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Выход...");
                        Console.ResetColor();
                        return;
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[ОШИБКА] Файл '{altPath}' тоже не найден. Нечего кластеризовать.");
                    Console.ResetColor();
                    return;
                }
            }

            var lines = File.ReadAllLines(filePath);
            var keywords = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            if (keywords.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ОШИБКА] Файл '{filePath}' пуст или не содержит ключевых слов.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine($"\nЗагружено {keywords.Count} ключевых слов из файла '{filePath}'.");

            // 4. Проверка OpenRouter API ключа
            if (string.IsNullOrWhiteSpace(openRouterSettings.ApiKey))
            {
                Console.Write("Введите ваш API ключ OpenRouter (для эмбеддингов): ");
                openRouterSettings.ApiKey = Console.ReadLine()?.Trim() ?? "";
            }

            if (string.IsNullOrWhiteSpace(openRouterSettings.ApiKey))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("OpenRouter API ключ не предоставлен. Кластеризация по эмбеддингам невозможна.");
                Console.ResetColor();
                return;
            }

            // 5. Настройка HTTP клиента
            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            // 6. Запуск пайплайна кластеризации
            var pipeline = new ClusterizationPipeline(client, deepSeekSettings, businessSettings, serpSettings, openRouterSettings, phase4Settings);
            var clusters = await pipeline.RunAsync(keywords);

            // 6. Вывод и сохранение результатов
            if (clusters != null && clusters.Count > 0)
            {
                if (!businessSettings.SuppressClusterDisplay)
                    DisplayClusters(clusters);
                else
                    Console.WriteLine($"\n[INFO] Вывод кластеров подавлен (suppressClusterDisplay=true). Итого: {clusters.Count} кластеров.");

                SaveToCsv(clusters, "clusters.csv");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[ОШИБКА] Кластеризация не дала результатов.");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Режим чистки ключевых запросов (новый функционал).
        /// Показывает меню выбора типа запроса, модели, затем запускает чистку.
        /// </summary>
        static async Task RunCleanerModeAsync()
        {
            // 1. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var openRouterSettings = new OpenRouterSettings();
            var cleanerSettings = new CleanerSettings();

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // API ключ DeepSeek (из settings.json, только если нет — запросить ввод)
                    if (root.TryGetProperty("apiKey", out var apiKeyEl))
                        deepSeekSettings.ApiKey = apiKeyEl.GetString()?.Trim() ?? "";

                    // Параметры нейросети
                    if (root.TryGetProperty("deepseek", out var deepseek))
                    {
                        if (deepseek.TryGetProperty("temperature", out var t))
                            deepSeekSettings.Temperature = t.GetDouble();

                        if (deepseek.TryGetProperty("maxTokens", out var mt))
                            deepSeekSettings.MaxTokens = mt.GetInt32();

                        if (deepseek.TryGetProperty("topP", out var tp))
                            deepSeekSettings.TopP = tp.GetDouble();

                        if (deepseek.TryGetProperty("stream", out var st))
                            deepSeekSettings.Stream = st.GetBoolean();
                    }

                    // OpenRouter настройки (нужны для моделей через OpenRouter)
                    if (root.TryGetProperty("openrouter", out var openrouter))
                    {
                        if (openrouter.TryGetProperty("apiKey", out var orKey))
                            openRouterSettings.ApiKey = orKey.GetString()?.Trim() ?? "";
                    }

                    // Cleaner настройки
                    if (root.TryGetProperty("cleaner", out var cleanerEl))
                    {
                        if (cleanerEl.TryGetProperty("defaultModel", out var dm))
                            cleanerSettings.DefaultModel = dm.GetString() ?? cleanerSettings.DefaultModel;

                        if (cleanerEl.TryGetProperty("defaultPoolSize", out var dps))
                            cleanerSettings.DefaultPoolSize = dps.GetInt32();

                        if (cleanerEl.TryGetProperty("outputCleaned", out var oc))
                            cleanerSettings.OutputCleaned = oc.GetString() ?? cleanerSettings.OutputCleaned;

                        if (cleanerEl.TryGetProperty("outputDiscarded", out var od))
                            cleanerSettings.OutputDiscarded = od.GetString() ?? cleanerSettings.OutputDiscarded;

                        if (cleanerEl.TryGetProperty("instructionsInformational", out var ii))
                            cleanerSettings.InstructionsInformational = ii.GetString() ?? cleanerSettings.InstructionsInformational;

                        if (cleanerEl.TryGetProperty("instructionsCommercial", out var ic))
                            cleanerSettings.InstructionsCommercial = ic.GetString() ?? cleanerSettings.InstructionsCommercial;
                    }
                }
                catch
                {
                    // используем значения по умолчанию
                }
            }

            // 2. API ключ DeepSeek
            if (string.IsNullOrWhiteSpace(deepSeekSettings.ApiKey) ||
                deepSeekSettings.ApiKey == "ВАШ_DEEPSEEK_API_KEY")
            {
                Console.Write("Введите ваш API ключ DeepSeek: ");
                deepSeekSettings.ApiKey = ReadPassword();
            }

            if (string.IsNullOrWhiteSpace(deepSeekSettings.ApiKey))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("API ключ не предоставлен. Выход...");
                Console.ResetColor();
                return;
            }

            // 3. Загрузка ключевых слов
            string filePath = "keywords.txt";
            if (!File.Exists(filePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ОШИБКА] Файл '{filePath}' не найден.");
                Console.ResetColor();
                return;
            }

            var lines = File.ReadAllLines(filePath);
            var keywords = lines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();

            if (keywords.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ОШИБКА] Файл '{filePath}' пуст или не содержит ключевых слов.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine($"\nЗагружено {keywords.Count} ключевых слов из файла '{filePath}'.");

            // 4. Выбор типа запроса
            Console.WriteLine("\nВыберите тип отбора:");
            Console.WriteLine("  1 - Информационные запросы (как, что, почему)");
            Console.WriteLine("  2 - Коммерческие запросы (купить, цена, заказать)");
            Console.Write("\nВаш выбор (1/2): ");
            string typeChoice = Console.ReadLine()?.Trim() ?? "1";
            var queryType = typeChoice == "2" ? QueryType.Commercial : QueryType.Informational;
            string queryTypeLabel = queryType == QueryType.Informational ? "информационные" : "коммерческие";
            Console.WriteLine($"Выбран тип: {queryTypeLabel}");

            // 4.b Выбор обработки брендов
            Console.WriteLine("\nКак обрабатывать брендовые запросы:");
            Console.WriteLine("  1 - Удалить в отдельный файл (branded.txt)");
            Console.WriteLine("  2 - Удалить в discarded.txt");
            Console.WriteLine("  3 - Оставить в очищенных");
            Console.Write("\nВаш выбор (1/2/3): ");
            string brandChoice = Console.ReadLine()?.Trim() ?? "1";
            var brandHandling = brandChoice switch
            {
                "2" => BrandHandling.ToDiscarded,
                "3" => BrandHandling.KeepAsIs,
                _ => BrandHandling.SeparateFile
            };

            // 5. Выбор модели
            Console.WriteLine("\nВыберите модель нейросети:");
            Console.WriteLine("  1 - DeepSeek V4 Pro    (deepseek-v4-pro,  прямой API)");
            Console.WriteLine("  2 - DeepSeek V4 Flash  (deepseek-v4-flash, прямой API)");
            Console.Write("\nВаш выбор (1-2): ");
            string modelChoice = Console.ReadLine()?.Trim() ?? "1";

            string selectedModel = modelChoice == "2" ? "deepseek-v4-flash" : "deepseek-v4-pro";

            // 6. Запрос темы и доп. инструкций
            Console.WriteLine("\n--- Дополнительные настройки чистки ---");
            Console.Write("Введите тему/нишу ключевых запросов (например: сантехника, унитазы) [Enter — пропустить]: ");
            string? topic = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(topic)) topic = null;

            Console.Write("Введите дополнительные инструкции для нейросети (Enter — пропустить): ");
            string? additionalPrompt = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(additionalPrompt)) additionalPrompt = null;

            Console.Write("Количество потоков (по умолчанию 10): ");
            string? threadsInput = Console.ReadLine()?.Trim();
            int maxConcurrency = 10;
            if (int.TryParse(threadsInput, out int parsedThreads) && parsedThreads > 0)
                maxConcurrency = parsedThreads;

            // 7. Настройка HTTP клиента (30 мин — большие пулы до 1000 ключей обрабатываются долго)
            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            // 8. Запуск чистки
            var cleaner = new KeywordCleanerService(client, deepSeekSettings, openRouterSettings, cleanerSettings);
            await cleaner.RunAsync(
                keywords, queryType,
                topic: topic,
                additionalPrompt: additionalPrompt,
                maxConcurrency: maxConcurrency,
                brandHandling: brandHandling,
                selectedModel: selectedModel);
        }

        /// <summary>
        /// Читает пароль/ключ из консоли без отображения символов.
        /// </summary>
        private static string ReadPassword()
        {
            var password = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true); // intercept = true — не выводим символ
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                }
            }
            Console.WriteLine();
            return password.ToString().Trim();
        }

        /// <summary>
        /// Выводит результаты кластеризации красиво в консоль.
        /// </summary>
        private static void DisplayClusters(Dictionary<string, List<string>> clusters)
        {
            Console.WriteLine("\n=================================================");
            Console.WriteLine("              РЕЗУЛЬТАТ КЛАСТЕРИЗАЦИИ            ");
            Console.WriteLine("=================================================");

            int totalKeywords = 0;
            foreach (var cluster in clusters)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n📁 Группа: {cluster.Key}");
                Console.ResetColor();

                foreach (var word in cluster.Value)
                {
                    Console.WriteLine($"  - {word}");
                    totalKeywords++;
                }
            }

            Console.WriteLine("\n-------------------------------------------------");
            Console.WriteLine($"Всего кластеров: {clusters.Count}");
            Console.WriteLine($"Всего распределено слов: {totalKeywords}");
        }

        /// <summary>
        /// Сохраняет результаты в CSV-файл (формат для Excel).
        /// Если файл занят — подбирает имя с суффиксом _1, _2 и т.д.
        /// </summary>
        private static void SaveToCsv(Dictionary<string, List<string>> clusters, string filePath)
        {
            // Если файл занят — подбираем свободное имя
            string actualPath = filePath;
            if (File.Exists(filePath))
            {
                try
                {
                    // Пробуем открыть на запись — если занят, подбираем новый путь
                    using var testStream = File.Open(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                }
                catch (IOException)
                {
                    string dir = Path.GetDirectoryName(filePath) ?? ".";
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
                    string ext = Path.GetExtension(filePath);
                    int suffix = 1;
                    do
                    {
                        actualPath = Path.Combine(dir, $"{nameWithoutExt}_{suffix}{ext}");
                        suffix++;
                    } while (File.Exists(actualPath));
                }
            }

            try
            {
                // Используем UTF8 с BOM, чтобы Excel корректно понимал кириллицу
                using var writer = new StreamWriter(actualPath, false, new UTF8Encoding(true));

                // Заголовки (используем точку с запятой, стандартно для русского Excel)
                writer.WriteLine("Кластер;Ключевое слово");

                foreach (var cluster in clusters)
                {
                    string safeClusterName = cluster.Key.Replace("\"", "\"\"");
                    foreach (var word in cluster.Value)
                    {
                        string safeWord = word.Replace("\"", "\"\"");
                        writer.WriteLine($"\"{safeClusterName}\";\"{safeWord}\"");
                    }
                }

                Console.ForegroundColor = ConsoleColor.Cyan;
                string suffixMsg = actualPath != filePath ? $" (файл '{filePath}' был занят)" : "";
                Console.WriteLine($"\n[УСПЕХ] Результаты сохранены: {actualPath}{suffixMsg}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ОШИБКА] Не удалось сохранить CSV файл: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}
