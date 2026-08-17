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
            // Регистрация кодовых страниц (Windows-1251 и др.) для .NET Core/.NET 5+
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            Console.WriteLine("=== Кластеризатор ключевых слов ===");

            // === Меню выбора режима ===
            Console.WriteLine("\nВыберите режим работы:");
            Console.WriteLine("  1 - Кластеризация ключевых слов");
            Console.WriteLine("  2 - Чистка ключевых запросов");
            Console.WriteLine("  3 - Объединить группы в CSV");
            Console.WriteLine("  4 - Отбор FAQ-кластеров (Phase 5 из clusters.csv)");
            Console.WriteLine("  5 - Продолжить с последнего этапа (чекпойнт)");
            Console.WriteLine("  6 - Наименование кластеров через ИИ (naming из clusters.csv)");
            Console.Write("\nВаш выбор (1/2/3/4/5/6): ");
            string modeChoice = Console.ReadLine()?.Trim() ?? "1";

            if (modeChoice == "2")
                await RunCleanerModeAsync();
            else if (modeChoice == "3")
                RunCsvGroupMerge();
            else if (modeChoice == "4")
                await RunFaqSelectionModeAsync();
            else if (modeChoice == "5")
                await RunResumeModeAsync();
            else if (modeChoice == "6")
                await RunNamingModeAsync();
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
            var phase4CleanSettings = new Phase4CleanSettings();
            var phase5FaqSettings = new Phase5FaqSettings();

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

                        if (openrouter.TryGetProperty("batchSize", out var orBatch))
                            openRouterSettings.BatchSize = orBatch.GetInt32();

                        if (openrouter.TryGetProperty("maxConcurrency", out var orConc))
                            openRouterSettings.MaxConcurrency = orConc.GetInt32();
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

                    // Чтение Phase 4.5 (AI-чистка кластеров) настроек
                    if (root.TryGetProperty("phase4Clean", out var phase4CleanEl))
                    {
                        if (phase4CleanEl.TryGetProperty("enabled", out var en))
                            phase4CleanSettings.Enabled = en.GetBoolean();

                        if (phase4CleanEl.TryGetProperty("provider", out var prov))
                            phase4CleanSettings.Provider = prov.GetString() ?? phase4CleanSettings.Provider;

                        if (phase4CleanEl.TryGetProperty("model", out var model))
                            phase4CleanSettings.Model = model.GetString() ?? "";

                        if (phase4CleanEl.TryGetProperty("temperature", out var temp))
                            phase4CleanSettings.Temperature = temp.GetDouble();

                        if (phase4CleanEl.TryGetProperty("maxTokens", out var mt))
                            phase4CleanSettings.MaxTokens = mt.GetInt32();

                        if (phase4CleanEl.TryGetProperty("enableThinking", out var et))
                            phase4CleanSettings.EnableThinking = et.GetBoolean();

                        if (phase4CleanEl.TryGetProperty("reasoningEffort", out var re))
                            phase4CleanSettings.ReasoningEffort = re.GetString();

                        if (phase4CleanEl.TryGetProperty("stream", out var st))
                            phase4CleanSettings.Stream = st.GetBoolean();

                        if (phase4CleanEl.TryGetProperty("maxIterations", out var mi))
                            phase4CleanSettings.MaxIterations = mi.GetInt32();

                        if (phase4CleanEl.TryGetProperty("maxConcurrency", out var mc))
                            phase4CleanSettings.MaxConcurrency = mc.GetInt32();
                    }

                    // Чтение Phase 5 (отбор FAQ-кластеров) настроек
                    if (root.TryGetProperty("phase5Faq", out var phase5FaqEl))
                    {
                        if (phase5FaqEl.TryGetProperty("enabled", out var en5))
                            phase5FaqSettings.Enabled = en5.GetBoolean();

                        if (phase5FaqEl.TryGetProperty("provider", out var prov5))
                            phase5FaqSettings.Provider = prov5.GetString() ?? phase5FaqSettings.Provider;

                        if (phase5FaqEl.TryGetProperty("model", out var model5))
                            phase5FaqSettings.Model = model5.GetString() ?? "";

                        if (phase5FaqEl.TryGetProperty("temperature", out var temp5))
                            phase5FaqSettings.Temperature = temp5.GetDouble();

                        if (phase5FaqEl.TryGetProperty("maxTokens", out var mt5))
                            phase5FaqSettings.MaxTokens = mt5.GetInt32();

                        if (phase5FaqEl.TryGetProperty("enableThinking", out var et5))
                            phase5FaqSettings.EnableThinking = et5.GetBoolean();

                        if (phase5FaqEl.TryGetProperty("reasoningEffort", out var re5))
                            phase5FaqSettings.ReasoningEffort = re5.GetString();

                        if (phase5FaqEl.TryGetProperty("stream", out var st5))
                            phase5FaqSettings.Stream = st5.GetBoolean();

                        if (phase5FaqEl.TryGetProperty("linkThreshold", out var lt))
                            phase5FaqSettings.LinkThreshold = lt.GetDouble();
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

            // 5.5 Автодетект чекпойнта: предлагаем продолжить с последнего завершённого этапа
            string? resumePhase = Services.CheckpointStore.FindLatestPhase();
            if (resumePhase != null)
            {
                ConsoleUtils.WriteLine(
                    $"[Checkpoint] Найден чекпойнт '{resumePhase}.json'.",
                    ConsoleColor.Cyan);
                Console.Write($"Продолжить с Phase {resumePhase.Replace("phase", "")}? (y/n): ");
                string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                bool resume = answer == "y" || answer == "yes" || answer == "д" || answer == "да";
                if (resume)
                {
                    Console.WriteLine($"\n[Resume] Продолжаю с чекпойнта '{resumePhase}'...");
                    var pipelineResume = new ClusterizationPipeline(client, deepSeekSettings, businessSettings, serpSettings, openRouterSettings, phase4Settings, phase4CleanSettings, phase5FaqSettings);
                    var resumeResult = await pipelineResume.RunAsync(keywords, resumeFromPhase: resumePhase);

                    if (resumeResult != null && resumeResult.Clusters.Count > 0)
                    {
                        if (!businessSettings.SuppressClusterDisplay)
                            DisplayClusters(resumeResult);
                        else
                            Console.WriteLine($"\n[INFO] Вывод кластеров подавлен (suppressClusterDisplay=true). Итого: {resumeResult.Clusters.Count} кластеров.");

                        SaveToCsv(resumeResult, "clusters.csv");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("\n[ОШИБКА] Возобновление не дало результатов.");
                        Console.ResetColor();
                    }
                    return;
                }
                else
                {
                    // Полный запуск с нуля — предлагаем удалить старые чекпойнты
                    Console.Write("Удалить старые чекпойнты (полный перезапуск)? (y/n): ");
                    string? clearAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (clearAnswer == "y" || clearAnswer == "yes" || clearAnswer == "д" || clearAnswer == "да")
                        Services.CheckpointStore.Clear();
                }
            }

            // 6. Запуск пайплайна кластеризации
            var pipeline = new ClusterizationPipeline(client, deepSeekSettings, businessSettings, serpSettings, openRouterSettings, phase4Settings, phase4CleanSettings, phase5FaqSettings);
            var result = await pipeline.RunAsync(keywords);

            // 6. Вывод и сохранение результатов
            if (result != null && result.Clusters.Count > 0)
            {
                if (!businessSettings.SuppressClusterDisplay)
                    DisplayClusters(result);
                else
                    Console.WriteLine($"\n[INFO] Вывод кластеров подавлен (suppressClusterDisplay=true). Итого: {result.Clusters.Count} кластеров.");

                SaveToCsv(result, "clusters.csv");
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
        /// Для FAQ-кластеров (Phase 5) добавляет пометку и ссылку на связанную статью.
        /// </summary>
        private static void DisplayClusters(Models.ClusteringResult result)
        {
            Console.WriteLine("\n=================================================");
            Console.WriteLine("              РЕЗУЛЬТАТ КЛАСТЕРИЗАЦИИ            ");
            Console.WriteLine("=================================================");

            int totalKeywords = 0;
            int faqCount = 0;
            foreach (var cluster in result.Clusters)
            {
                // FAQ-пометка из метаданных Phase 5
                result.Meta.TryGetValue(cluster.Key, out var meta);
                bool isFaq = meta?.IsFaq == true;

                Console.ForegroundColor = isFaq ? ConsoleColor.Magenta : ConsoleColor.Green;
                string suffix = isFaq
                    ? $" [FAQ{(!string.IsNullOrWhiteSpace(meta?.LinkedArticle) ? $" → \"{meta.LinkedArticle}\"" : "")}]"
                    : "";
                Console.WriteLine($"\n📁 Группа: {cluster.Key}{suffix}");
                Console.ResetColor();

                if (isFaq)
                    faqCount++;

                foreach (var word in cluster.Value)
                {
                    Console.WriteLine($"  - {word}");
                    totalKeywords++;
                }
            }

            Console.WriteLine("\n-------------------------------------------------");
            Console.WriteLine($"Всего кластеров: {result.Clusters.Count} (из них FAQ: {faqCount})");
            Console.WriteLine($"Всего распределено слов: {totalKeywords}");
        }

        /// <summary>
        /// Сохраняет результаты в CSV-файл (формат для Excel).
        /// Если файл занят — подбирает имя с суффиксом _1, _2 и т.д.
        /// Если среди кластеров есть FAQ (Phase 5) — результат делится на два файла:
        /// filePath — только статьи ("Кластер;Ключевое слово"),
        /// рядом "clusters_faq.csv" — все FAQ-вопросы ("Кластер;Ключевое слово;Связанная статья").
        /// Файл FAQ создаётся всегда (даже пустой с заголовком).
        /// </summary>
        private static void SaveToCsv(Models.ClusteringResult result, string filePath)
        {
            // Разделяем кластеры на статьи и FAQ по метаданным Phase 5
            var faqClusters = new List<KeyValuePair<string, List<string>>>();
            var articleClusters = new List<KeyValuePair<string, List<string>>>();
            foreach (var cluster in result.Clusters)
            {
                result.Meta.TryGetValue(cluster.Key, out var meta);
                bool isFaq = meta?.IsFaq == true;
                if (isFaq)
                    faqClusters.Add(cluster);
                else
                    articleClusters.Add(cluster);
            }

            try
            {
                // --- Файл статей: filePath (Кластер;Ключевое слово) ---
                string actualPath = GetWritableCsvPath(filePath);
                using (var writer = new StreamWriter(actualPath, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("Кластер;Ключевое слово");
                    foreach (var cluster in articleClusters)
                    {
                        string safeClusterName = cluster.Key.Replace("\"", "\"\"");
                        foreach (var word in cluster.Value)
                        {
                            string safeWord = word.Replace("\"", "\"\"");
                            writer.WriteLine($"\"{safeClusterName}\";\"{safeWord}\"");
                        }
                    }
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                string suffixMsg = actualPath != filePath ? $" (файл '{filePath}' был занят)" : "";
                Console.WriteLine($"\n[УСПЕХ] Статьи: {actualPath} ({articleClusters.Count} кластеров){suffixMsg}");
                Console.ResetColor();

                // --- Файл FAQ: clusters_faq.csv рядом с filePath (Кластер;Ключевое слово;Связанная статья) ---
                // Файл создаётся ВСЕГДА (даже без FAQ-кластеров — только с заголовком),
                // чтобы в режиме 3 файл гарантированно существовал.
                string? dir = Path.GetDirectoryName(filePath);
                string faqPath = string.IsNullOrEmpty(dir)
                    ? "clusters_faq.csv"
                    : Path.Combine(dir, "clusters_faq.csv");
                string actualFaqPath = GetWritableCsvPath(faqPath);

                using (var writer = new StreamWriter(actualFaqPath, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("Кластер;Ключевое слово;Связанная статья");
                    foreach (var cluster in faqClusters)
                    {
                        string safeClusterName = cluster.Key.Replace("\"", "\"\"");
                        result.Meta.TryGetValue(cluster.Key, out var meta);
                        string linked = meta?.LinkedArticle ?? "";
                        string safeLinked = linked.Replace("\"", "\"\"");
                        foreach (var word in cluster.Value)
                        {
                            string safeWord = word.Replace("\"", "\"\"");
                            writer.WriteLine($"\"{safeClusterName}\";\"{safeWord}\";\"{safeLinked}\"");
                        }
                    }
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                string faqSuffixMsg = actualFaqPath != faqPath ? $" (файл '{faqPath}' был занят)" : "";
                Console.WriteLine($"[УСПЕХ] FAQ: {actualFaqPath} ({faqClusters.Count} кластеров){faqSuffixMsg}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ОШИБКА] Не удалось сохранить CSV файл: {ex.Message}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Возвращает путь к CSV-файлу: если файл существует и не занят — сам путь
        /// (перезапись), если занят другим процессом — путь с суффиксом _1, _2 и т.д.
        /// </summary>
        private static string GetWritableCsvPath(string filePath)
        {
            if (!File.Exists(filePath))
                return filePath;

            try
            {
                // Пробуем открыть на запись — если занят, подбираем новый путь
                using var testStream = File.Open(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
                return filePath;
            }
            catch (IOException)
            {
                string dir = Path.GetDirectoryName(filePath) ?? ".";
                string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
                string ext = Path.GetExtension(filePath);
                int suffix = 1;
                string candidate;
                do
                {
                    candidate = Path.Combine(dir, $"{nameWithoutExt}_{suffix}{ext}");
                    suffix++;
                } while (File.Exists(candidate));
                return candidate;
            }
        }

        /// <summary>
        /// Возвращает уникальный путь к файлу: если файл уже существует —
        /// добавляет суффикс _1, _2 и т.д., пока не найдёт свободное имя.
        /// Используется для выходных merged-файлов режима 3 (не перезаписывает существующие).
        /// </summary>
        private static string GetUniquePath(string path)
        {
            if (!File.Exists(path))
                return path;

            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string dir = Path.GetDirectoryName(path) ?? ".";
            int suffix = 1;
            string candidate;
            do
            {
                candidate = Path.Combine(dir, $"{name}_{suffix}{ext}");
                suffix++;
            }
            while (File.Exists(candidate));
            return candidate;
        }

        /// <summary>
        /// Режим 4: Отбор FAQ-кластеров (Phase 5) из готового clusters.csv.
        /// По умолчанию читает clusters.csv (формат "Кластер;Ключевое слово"), группирует
        /// по кластеру, выполняет Phase 5 (AI-отбор + привязка по смыслу) и сохраняет
        /// результат обратно в clusters.csv с колонками Тип/Связанная статья.
        /// Эмбеддинги берутся из кэша embeddings_cache.json, недостающие — запрашиваются
        /// через OpenRouter (если ключ задан).
        /// </summary>
        static async Task RunFaqSelectionModeAsync()
        {
            Console.WriteLine("\n=== Отбор FAQ-кластеров (Phase 5) ===");

            // 1. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var openRouterSettings = new OpenRouterSettings();
            var businessSettings = new BusinessSettings();
            var phase5FaqSettings = new Phase5FaqSettings();

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // API ключ DeepSeek
                    if (root.TryGetProperty("apiKey", out var apiKeyEl))
                        deepSeekSettings.ApiKey = apiKeyEl.GetString()?.Trim() ?? "";

                    // deepseek
                    if (root.TryGetProperty("deepseek", out var deepseek))
                    {
                        if (deepseek.TryGetProperty("model", out var m))
                            deepSeekSettings.Model = m.GetString() ?? deepSeekSettings.Model;
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

                    // business (ниша/логика)
                    if (root.TryGetProperty("business", out var business))
                    {
                        if (business.TryGetProperty("niche", out var n))
                            businessSettings.Niche = n.GetString() ?? "";
                        if (business.TryGetProperty("clusteringLogic", out var cl))
                            businessSettings.ClusteringLogic = cl.GetString() ?? "";
                    }

                    // openrouter
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
                        if (openrouter.TryGetProperty("batchSize", out var orBatch))
                            openRouterSettings.BatchSize = orBatch.GetInt32();
                        if (openrouter.TryGetProperty("maxConcurrency", out var orConc))
                            openRouterSettings.MaxConcurrency = orConc.GetInt32();
                    }

                    // phase5Faq
                    if (root.TryGetProperty("phase5Faq", out var phase5Faq))
                    {
                        if (phase5Faq.TryGetProperty("enabled", out var en))
                            phase5FaqSettings.Enabled = en.GetBoolean();
                        if (phase5Faq.TryGetProperty("provider", out var prov))
                            phase5FaqSettings.Provider = prov.GetString() ?? phase5FaqSettings.Provider;
                        if (phase5Faq.TryGetProperty("model", out var model))
                            phase5FaqSettings.Model = model.GetString() ?? "";
                        if (phase5Faq.TryGetProperty("temperature", out var temp))
                            phase5FaqSettings.Temperature = temp.GetDouble();
                        if (phase5Faq.TryGetProperty("maxTokens", out var mt))
                            phase5FaqSettings.MaxTokens = mt.GetInt32();
                        if (phase5Faq.TryGetProperty("enableThinking", out var et))
                            phase5FaqSettings.EnableThinking = et.GetBoolean();
                        if (phase5Faq.TryGetProperty("reasoningEffort", out var re))
                            phase5FaqSettings.ReasoningEffort = re.GetString();
                        if (phase5Faq.TryGetProperty("stream", out var st))
                            phase5FaqSettings.Stream = st.GetBoolean();
                        if (phase5Faq.TryGetProperty("linkThreshold", out var lt))
                            phase5FaqSettings.LinkThreshold = lt.GetDouble();
                    }
                }
                catch
                {
                    // значения по умолчанию
                }
            }

            if (!phase5FaqSettings.Enabled)
            {
                ConsoleUtils.WriteLine(
                    $"[ПРЕДУПРЕЖДЕНИЕ] phase5Faq.enabled = false в settings.json. Фаза будет выполнена, но настройки отключения игнорируются в этом режиме.",
                    ConsoleColor.Yellow);
            }

            // 2. Чтение clusters.csv (по умолчанию — clusters.csv)
            string inputPath;
            while (true)
            {
                Console.Write($"\nПуть к входному CSV [clusters.csv]: ");
                inputPath = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(inputPath)) inputPath = "clusters.csv";

                if (File.Exists(inputPath))
                    break;

                ConsoleUtils.WriteLine($"[ОШИБКА] Файл '{inputPath}' не найден.", ConsoleColor.Red);
                Console.Write("  [Enter] — повторить, [Q] — выход: ");
                var key = Console.ReadLine()?.Trim().ToUpperInvariant();
                if (key == "Q") return;
            }

            // 3. Группировка по кластеру
            var encoding = DetectCsvEncoding(inputPath);
            var lines = File.ReadAllLines(inputPath, encoding);
            var clusters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = SplitCsvLine(line, ';');
                if (parts.Length < 2) continue;

                string groupName = parts[0].Trim().Trim('"');
                string keyword = parts[1].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(keyword)) continue;

                if (!clusters.TryGetValue(groupName, out var list))
                {
                    list = new List<string>();
                    clusters[groupName] = list;
                }
                if (!list.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    list.Add(keyword);
            }

            if (clusters.Count == 0)
            {
                ConsoleUtils.WriteLine("[ОШИБКА] Не найдено ни одного кластера.", ConsoleColor.Red);
                return;
            }

            int totalKeywords = clusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nЗагружено {clusters.Count} кластеров, {totalKeywords} ключей из '{inputPath}'.");

            // 4. HTTP-клиент и эмбеддинги (кэш + API)
            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            var phraseEmbeddings = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(openRouterSettings.ApiKey))
            {
                Console.WriteLine("\nЗагрузка эмбеддингов (кэш + OpenRouter)...");
                var embeddingClient = new Services.OpenRouterEmbeddingClient(client, openRouterSettings);

                var allPhrases = clusters.Values.SelectMany(v => v)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var missing = embeddingClient.GetMissingFromCache(allPhrases);
                if (missing.Count > 0)
                {
                    Console.WriteLine($"  [Embed] В кэше {allPhrases.Count - missing.Count}/{allPhrases.Count}. Нужно запросить {missing.Count}.");
                    bool apiOk = await embeddingClient.TestApiAsync();
                    if (!apiOk)
                    {
                        ConsoleUtils.WriteLine(
                            "  [Embed] OpenRouter API недоступен. Привязка FAQ к статьям будет пропущена (будут только пометки FAQ).",
                            ConsoleColor.Yellow);
                    }
                }
                else
                {
                    Console.WriteLine($"  [Embed] Все {allPhrases.Count} фраз уже в кэше. API-запрос не нужен.");
                }

                phraseEmbeddings = await embeddingClient.GetEmbeddingsBatchAsync(allPhrases);
                embeddingClient.SaveCache();
                Console.WriteLine($"  [Embed] Загружено эмбеддингов: {phraseEmbeddings.Count}.");
            }
            else
            {
                ConsoleUtils.WriteLine(
                    "[Предупреждение] OpenRouter apiKey не задан. Привязка FAQ к статьям будет пропущена (будут только пометки FAQ).",
                    ConsoleColor.Yellow);
            }

            // 5. Запуск Phase 5 (AI-отбор + привязка)
            var faqPass = new Services.FaqSelectionPass(
                client, deepSeekSettings, openRouterSettings, phase5FaqSettings, businessSettings);
            var meta = await faqPass.RunAsync(clusters, phraseEmbeddings);

            // 6. Сохранение результата обратно в clusters.csv
            SaveToCsv(new Models.ClusteringResult { Clusters = clusters, Meta = meta }, inputPath);
        }

        /// <summary>
        /// Режим 5: Продолжить с последнего этапа (чекпойнт).
        /// Загружает самый свежий чекпойнт (phase4 / phase4_5 / phase5) и выполняет
        /// только последующие фазы пайплайна, пропуская уже пройденные.
        /// </summary>
        static async Task RunResumeModeAsync()
        {
            Console.WriteLine("\n=== Продолжить с последнего этапа (чекпойнт) ===");

            // 1. Ищем самый свежий чекпойнт
            string? resumePhase = Services.CheckpointStore.FindLatestPhase();
            if (resumePhase == null)
            {
                ConsoleUtils.WriteLine(
                    "[Checkpoint] Чекпойнты не найдены (phase4/phase4_5/phase5.json). Сначала запустите полную кластеризацию (режим 1).",
                    ConsoleColor.Yellow);
                return;
            }

            var checkpoint = Services.CheckpointStore.Load(resumePhase);
            if (checkpoint == null || checkpoint.Clusters.Count == 0)
            {
                ConsoleUtils.WriteLine(
                    $"[ОШИБКА] Чекпойнт '{resumePhase}.json' повреждён или пуст.",
                    ConsoleColor.Red);
                return;
            }

            ConsoleUtils.WriteLine(
                $"[Checkpoint] Найден чекпойнт '{resumePhase}.json' ({checkpoint.Clusters.Count} кластеров, от {checkpoint.SavedAt:g}).",
                ConsoleColor.Cyan);
            Console.Write($"Продолжить с Phase {resumePhase.Replace("phase", "")}? (y/n): ");
            string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (!(answer == "y" || answer == "yes" || answer == "д" || answer == "да"))
            {
                Console.WriteLine("Отменено.");
                return;
            }

            // 2. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var businessSettings = new BusinessSettings();
            var serpSettings = new XmlRiverSettings();
            var openRouterSettings = new OpenRouterSettings();
            var phase4Settings = new Phase4Settings();
            var phase4CleanSettings = new Phase4CleanSettings();
            var phase5FaqSettings = new Phase5FaqSettings();

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("apiKey", out var apiKeyEl))
                        deepSeekSettings.ApiKey = apiKeyEl.GetString()?.Trim() ?? "";

                    if (root.TryGetProperty("deepseek", out var deepseek))
                    {
                        if (deepseek.TryGetProperty("model", out var m))
                            deepSeekSettings.Model = m.GetString() ?? deepSeekSettings.Model;
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

                    if (root.TryGetProperty("business", out var business))
                    {
                        if (business.TryGetProperty("suppressClusterDisplay", out var scd))
                            businessSettings.SuppressClusterDisplay = scd.GetBoolean();
                        if (business.TryGetProperty("niche", out var n))
                            businessSettings.Niche = n.GetString() ?? "";
                        if (business.TryGetProperty("clusteringLogic", out var cl))
                            businessSettings.ClusteringLogic = cl.GetString() ?? "";
                    }

                    if (root.TryGetProperty("serp", out var serp))
                    {
                        if (serp.TryGetProperty("xmlriverUser", out var xu))
                            serpSettings.XmlriverUser = xu.GetString() ?? "";
                        if (serp.TryGetProperty("xmlriverKey", out var xk))
                            serpSettings.XmlriverKey = xk.GetString() ?? "";
                        if (serp.TryGetProperty("overlapThreshold", out var ot))
                            serpSettings.OverlapThreshold = ot.GetInt32();
                        if (serp.TryGetProperty("topResultsCount", out var trc))
                            serpSettings.TopResultsCount = trc.GetInt32();
                        if (serp.TryGetProperty("enableCache", out var ec))
                            serpSettings.EnableCache = ec.GetBoolean();
                        if (serp.TryGetProperty("cachePath", out var cp))
                            serpSettings.CachePath = cp.GetString() ?? serpSettings.CachePath;
                        if (serp.TryGetProperty("maxRetries", out var mr))
                            serpSettings.MaxRetries = mr.GetInt32();
                        if (serp.TryGetProperty("retryDelayMs", out var rd))
                            serpSettings.RetryDelayMs = rd.GetInt32();
                        if (serp.TryGetProperty("maxConcurrency", out var mc))
                            serpSettings.MaxConcurrency = mc.GetInt32();
                    }

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
                        if (openrouter.TryGetProperty("batchSize", out var orBatch))
                            openRouterSettings.BatchSize = orBatch.GetInt32();
                        if (openrouter.TryGetProperty("maxConcurrency", out var orConc))
                            openRouterSettings.MaxConcurrency = orConc.GetInt32();
                    }

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

                    if (root.TryGetProperty("phase4Clean", out var phase4CleanEl))
                    {
                        if (phase4CleanEl.TryGetProperty("enabled", out var en))
                            phase4CleanSettings.Enabled = en.GetBoolean();
                        if (phase4CleanEl.TryGetProperty("provider", out var prov))
                            phase4CleanSettings.Provider = prov.GetString() ?? phase4CleanSettings.Provider;
                        if (phase4CleanEl.TryGetProperty("model", out var model))
                            phase4CleanSettings.Model = model.GetString() ?? "";
                        if (phase4CleanEl.TryGetProperty("temperature", out var temp))
                            phase4CleanSettings.Temperature = temp.GetDouble();
                        if (phase4CleanEl.TryGetProperty("maxTokens", out var mt))
                            phase4CleanSettings.MaxTokens = mt.GetInt32();
                        if (phase4CleanEl.TryGetProperty("enableThinking", out var et))
                            phase4CleanSettings.EnableThinking = et.GetBoolean();
                        if (phase4CleanEl.TryGetProperty("reasoningEffort", out var re))
                            phase4CleanSettings.ReasoningEffort = re.GetString();
                        if (phase4CleanEl.TryGetProperty("stream", out var st))
                            phase4CleanSettings.Stream = st.GetBoolean();
                        if (phase4CleanEl.TryGetProperty("maxIterations", out var mi))
                            phase4CleanSettings.MaxIterations = mi.GetInt32();
                        if (phase4CleanEl.TryGetProperty("maxConcurrency", out var mc))
                            phase4CleanSettings.MaxConcurrency = mc.GetInt32();
                    }

                    if (root.TryGetProperty("phase5Faq", out var phase5FaqEl))
                    {
                        if (phase5FaqEl.TryGetProperty("enabled", out var en))
                            phase5FaqSettings.Enabled = en.GetBoolean();
                        if (phase5FaqEl.TryGetProperty("provider", out var prov))
                            phase5FaqSettings.Provider = prov.GetString() ?? phase5FaqSettings.Provider;
                        if (phase5FaqEl.TryGetProperty("model", out var model))
                            phase5FaqSettings.Model = model.GetString() ?? "";
                        if (phase5FaqEl.TryGetProperty("temperature", out var temp))
                            phase5FaqSettings.Temperature = temp.GetDouble();
                        if (phase5FaqEl.TryGetProperty("maxTokens", out var mt))
                            phase5FaqSettings.MaxTokens = mt.GetInt32();
                        if (phase5FaqEl.TryGetProperty("enableThinking", out var et))
                            phase5FaqSettings.EnableThinking = et.GetBoolean();
                        if (phase5FaqEl.TryGetProperty("reasoningEffort", out var re))
                            phase5FaqSettings.ReasoningEffort = re.GetString();
                        if (phase5FaqEl.TryGetProperty("stream", out var st))
                            phase5FaqSettings.Stream = st.GetBoolean();
                        if (phase5FaqEl.TryGetProperty("linkThreshold", out var lt))
                            phase5FaqSettings.LinkThreshold = lt.GetDouble();
                    }
                }
                catch
                {
                    // значения по умолчанию
                }
            }

            // 3. Ключевые слова (для консистентности входа; кластеры берутся из чекпойнта)
            var keywords = new List<string>();
            if (File.Exists("keywords.txt"))
            {
                keywords = File.ReadAllLines("keywords.txt")
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .ToList();
            }

            // 4. HTTP клиент
            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            // 5. Запуск возобновления
            var pipeline = new ClusterizationPipeline(client, deepSeekSettings, businessSettings, serpSettings, openRouterSettings, phase4Settings, phase4CleanSettings, phase5FaqSettings);
            var result = await pipeline.RunAsync(keywords, resumeFromPhase: resumePhase);

            // 6. Вывод и сохранение
            if (result != null && result.Clusters.Count > 0)
            {
                if (!businessSettings.SuppressClusterDisplay)
                    DisplayClusters(result);
                else
                    Console.WriteLine($"\n[INFO] Вывод кластеров подавлен (suppressClusterDisplay=true). Итого: {result.Clusters.Count} кластеров.");

                SaveToCsv(result, "clusters.csv");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[ОШИБКА] Возобновление не дало результатов.");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Режим 6: Наименование кластеров через ИИ (naming из clusters.csv).
        /// Читает clusters.csv (с FAQ-колонками или без), для каждого кластера отправляет в ИИ
        /// название + все ключи, ИИ придумывает новый H1-заголовок. Результат — перезаписанный
        /// clusters.csv с новыми названиями кластеров; FAQ-колонки (Тип/Связанная статья) сохраняются.
        /// Обработка параллельная (naming.maxConcurrency потоков).
        /// </summary>
        static async Task RunNamingModeAsync()
        {
            Console.WriteLine("\n=== Наименование кластеров через ИИ ===");

            // 1. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var openRouterSettings = new OpenRouterSettings();
            var businessSettings = new BusinessSettings();
            var namingSettings = new NamingSettings();

            if (File.Exists(settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(settingsPath);
                    using JsonDocument doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("apiKey", out var apiKeyEl))
                        deepSeekSettings.ApiKey = apiKeyEl.GetString()?.Trim() ?? "";

                    if (root.TryGetProperty("deepseek", out var deepseek))
                    {
                        if (deepseek.TryGetProperty("model", out var m))
                            deepSeekSettings.Model = m.GetString() ?? deepSeekSettings.Model;
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

                    if (root.TryGetProperty("business", out var business))
                    {
                        if (business.TryGetProperty("niche", out var n))
                            businessSettings.Niche = n.GetString() ?? "";
                        if (business.TryGetProperty("clusteringLogic", out var cl))
                            businessSettings.ClusteringLogic = cl.GetString() ?? "";
                    }

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
                        if (openrouter.TryGetProperty("batchSize", out var orBatch))
                            openRouterSettings.BatchSize = orBatch.GetInt32();
                        if (openrouter.TryGetProperty("maxConcurrency", out var orConc))
                            openRouterSettings.MaxConcurrency = orConc.GetInt32();
                    }

                    // Блок naming (наименование кластеров)
                    if (root.TryGetProperty("naming", out var namingEl))
                    {
                        if (namingEl.TryGetProperty("enabled", out var en))
                            namingSettings.Enabled = en.GetBoolean();
                        if (namingEl.TryGetProperty("provider", out var prov))
                            namingSettings.Provider = prov.GetString() ?? namingSettings.Provider;
                        if (namingEl.TryGetProperty("model", out var model))
                            namingSettings.Model = model.GetString() ?? "";
                        if (namingEl.TryGetProperty("temperature", out var temp))
                            namingSettings.Temperature = temp.GetDouble();
                        if (namingEl.TryGetProperty("maxTokens", out var mt))
                            namingSettings.MaxTokens = mt.GetInt32();
                        if (namingEl.TryGetProperty("enableThinking", out var et))
                            namingSettings.EnableThinking = et.GetBoolean();
                        if (namingEl.TryGetProperty("reasoningEffort", out var re))
                            namingSettings.ReasoningEffort = re.GetString();
                        if (namingEl.TryGetProperty("stream", out var st))
                            namingSettings.Stream = st.GetBoolean();
                        if (namingEl.TryGetProperty("maxConcurrency", out var mc))
                            namingSettings.MaxConcurrency = mc.GetInt32();
                    }
                }
                catch
                {
                    // значения по умолчанию
                }
            }

            if (!namingSettings.Enabled)
            {
                ConsoleUtils.WriteLine(
                    "[ПРЕДУПРЕЖДЕНИЕ] naming.enabled = false в settings.json.",
                    ConsoleColor.Yellow);
            }

            // 2. Запрос входного файла (по умолчанию clusters.csv)
            string inputPath;
            while (true)
            {
                Console.Write($"\nПуть к входному CSV [clusters.csv]: ");
                inputPath = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(inputPath)) inputPath = "clusters.csv";

                if (File.Exists(inputPath))
                    break;

                ConsoleUtils.WriteLine($"[ОШИБКА] Файл '{inputPath}' не найден.", ConsoleColor.Red);
                Console.Write("  [Enter] — повторить, [Q] — выход: ");
                var key = Console.ReadLine()?.Trim().ToUpperInvariant();
                if (key == "Q") return;
            }

            // 3. Чтение CSV: группировка по кластеру + FAQ-колонки + сохранение исходных строк
            var encoding = DetectCsvEncoding(inputPath);
            var rawLines = File.ReadAllLines(inputPath, encoding);

            var clusters = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var rowTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // кластер → Тип
            var rowLinked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // кластер → Связанная статья
            var rowOrder = new List<string>(); // порядок появления кластеров

            for (int i = 1; i < rawLines.Length; i++)
            {
                string line = rawLines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = SplitCsvLine(line, ';');
                if (parts.Length < 2) continue;

                string groupName = parts[0].Trim().Trim('"');
                string keyword = parts[1].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(keyword)) continue;

                if (!clusters.TryGetValue(groupName, out var list))
                {
                    list = new List<string>();
                    clusters[groupName] = list;
                    rowOrder.Add(groupName);
                }
                if (!list.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    list.Add(keyword);

                // FAQ-колонки (если есть): 2 — Тип, 3 — Связанная статья
                if (parts.Length > 2)
                {
                    string type = parts[2].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(type))
                        rowTypes[groupName] = type;
                }
                if (parts.Length > 3)
                {
                    string linked = parts[3].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(linked))
                        rowLinked[groupName] = linked;
                }
            }

            if (clusters.Count == 0)
            {
                ConsoleUtils.WriteLine("[ОШИБКА] Не найдено ни одного кластера.", ConsoleColor.Red);
                return;
            }

            int totalKeywords = clusters.Sum(c => c.Value.Count);
            Console.WriteLine($"\nЗагружено {clusters.Count} кластеров, {totalKeywords} ключей из '{inputPath}'.");

            // 4. HTTP клиент
            // Таймаут 60с на один запрос: если нейросеть «молчит» (не отвечает),
            // программа не висит по 30 минут, а распознаёт недоступность как сетевую ошибку.
            using var client = new HttpClient() { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            // 5. Наименование кластеров через ИИ
            var namingPass = new Services.ClusterNamingPass(
                client, deepSeekSettings, openRouterSettings, namingSettings, businessSettings);
            var renames = await namingPass.RunAsync(clusters);

            if (renames.Count == 0)
            {
                ConsoleUtils.WriteLine("[Naming] Ни один кластер не переименован. Файл не изменён.", ConsoleColor.Yellow);
                return;
            }

            // 6. Перезапись входного файла: подменяем имена кластеров, FAQ-колонки сохраняем
            try
            {
                var sb = new StringBuilder();
                // Заголовок: сохраняем как был
                sb.AppendLine(rawLines.Length > 0 ? rawLines[0] : "Кластер;Ключевое слово");

                for (int i = 1; i < rawLines.Length; i++)
                {
                    string line = rawLines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var parts = SplitCsvLine(line, ';');
                    if (parts.Length < 2) continue;

                    string oldName = parts[0].Trim().Trim('"');
                    string keyword = parts[1].Trim().Trim('"');

                    // Новое имя кластера (если переименован)
                    string newName = renames.TryGetValue(oldName, out var n) ? n : oldName;

                    string safeName = EscapeCsvField(newName, ';');
                    string safeKeyword = EscapeCsvField(keyword, ';');

                    // Сохраняем оставшиеся колонки (Тип/Связанная статья), если были
                    var tail = new List<string>();
                    for (int c = 2; c < parts.Length; c++)
                    {
                        string raw = parts[c].Trim().Trim('"');
                        if (c == 2)
                        {
                            // Тип: если кластер был FAQ и переименован — тип сохраняем
                            raw = rowTypes.TryGetValue(oldName, out var t) ? t : raw;
                        }
                        else if (c == 3)
                        {
                            raw = rowLinked.TryGetValue(oldName, out var l) ? l : raw;
                        }
                        tail.Add(EscapeCsvField(raw, ';'));
                    }

                    if (tail.Count > 0)
                        sb.AppendLine($"{safeName};{safeKeyword};{string.Join(";", tail)}");
                    else
                        sb.AppendLine($"{safeName};{safeKeyword}");
                }

                File.WriteAllText(inputPath, sb.ToString(), new UTF8Encoding(true));

                ConsoleUtils.WriteLine($"\n[УСПЕХ] Сохранено: {inputPath} (переименовано {renames.Count} кластеров)", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine($"\n[ОШИБКА] Не удалось сохранить: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// Режим 3: объединение строк одного кластера в одну строку CSV.
        /// Вход — один из файлов, созданных при отборе FAQ (Phase 5):
        /// clusters.csv (статьи, "Кластер;Ключевое слово") или clusters_faq.csv
        /// (FAQ, "Кластер;Ключевое слово;Связанная статья"). Также можно указать свой путь.
        /// Группирует по кластеру и сохраняет merged-файл с суффиксом "_merged"
        /// (clusters_merged.csv / clusters_faq_merged.csv) с колонками "Группа;Ключевые слова"
        /// + доп. колонки исходника (например Связанная статья).
        /// </summary>
        static void RunCsvGroupMerge()
        {
            Console.WriteLine("\n=== Объединение групп в CSV ===");

            // 1. Выбор входного файла: статьи / FAQ / свой путь
            string inputPath;
            while (true)
            {
                Console.WriteLine("\nКакой файл объединить?");
                Console.WriteLine("  1 - clusters.csv (статьи)");
                Console.WriteLine("  2 - clusters_faq.csv (FAQ)");
                Console.WriteLine("  3 - свой путь");
                Console.Write("Ваш выбор (1/2/3): ");
                string choice = Console.ReadLine()?.Trim() ?? "";

                if (choice == "1")
                    inputPath = "clusters.csv";
                else if (choice == "2")
                    inputPath = "clusters_faq.csv";
                else
                {
                    Console.Write($"\nПуть к входному CSV [clusters.csv]: ");
                    inputPath = Console.ReadLine()?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(inputPath)) inputPath = "clusters.csv";
                }

                if (File.Exists(inputPath))
                    break;

                ConsoleUtils.WriteLine($"[ОШИБКА] Файл '{inputPath}' не найден.", ConsoleColor.Red);
                Console.Write("  [Enter] — повторить, [Q] — выход: ");
                var key = Console.ReadLine()?.Trim().ToUpperInvariant();
                if (key == "Q") return;
            }

            // 2. Разделитель — всегда точка с запятой
            char separator = ';';

            // 3. Чтение с автоопределением кодировки (BOM → UTF-8, иначе система/1251)
            var encoding = DetectCsvEncoding(inputPath);
            var lines = File.ReadAllLines(inputPath, encoding);

            // Заголовок (первая строка) — для имён доп. колонок в выходном файле
            string[] headerParts = lines.Length > 0
                ? SplitCsvLine(lines[0].Trim(), separator)
                : Array.Empty<string>();

            // Группа: имя → ключи; доп. колонки (со 2-й, например Связанная статья) — свойства кластера
            var groups = new Dictionary<string, List<string>>();
            var groupExtra = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var groupOrder = new List<string>(); // порядок появления групп

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] parts = SplitCsvLine(line, separator);
                if (parts.Length < 2) continue;

                string groupName = parts[0].Trim().Trim('"');
                string keyword = parts[1].Trim().Trim('"');

                if (!groups.ContainsKey(groupName))
                {
                    groups[groupName] = new List<string>();
                    groupOrder.Add(groupName);

                    // Доп. колонки (2+) берём из первой строки группы (свойства кластера одинаковы)
                    var extra = new List<string>();
                    for (int c = 2; c < parts.Length; c++)
                        extra.Add(parts[c].Trim().Trim('"'));
                    groupExtra[groupName] = extra;
                }

                groups[groupName].Add(keyword);
            }

            if (groups.Count == 0)
            {
                ConsoleUtils.WriteLine("[ОШИБКА] Не найдено ни одной группы.", ConsoleColor.Red);
                return;
            }

            int totalKeywords = groups.Sum(g => g.Value.Count);
            Console.WriteLine($"\nПрочитано {totalKeywords} ключей, {groups.Count} групп.");

            // 4. Сохранение единого merged-файла
            // Выходной файл — с суффиксом "_merged": clusters_merged.csv / clusters_faq_merged.csv.
            // Если такой файл уже существует — подбирается суффикс _1, _2 и т.д.
            string basePath = Path.Combine(
                Path.GetDirectoryName(inputPath) ?? ".",
                Path.GetFileNameWithoutExtension(inputPath));
            string actualOutput = GetUniquePath(basePath + "_merged.csv");

            try
            {
                // Заголовок: Группа;Ключевые слова + доп. колонки из заголовка исходника (3+)
                var header = new List<string> { "Группа", "Ключевые слова" };
                for (int c = 2; c < headerParts.Length; c++)
                {
                    string h = headerParts[c].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(h)) header.Add(h);
                }
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(separator.ToString(), header));

                foreach (var name in groupOrder)
                {
                    string mergedKeywords = string.Join(", ", groups[name]);
                    string safeGroup = EscapeCsvField(name, separator);
                    string safeKeywords = EscapeCsvField(mergedKeywords, separator);

                    var row = new List<string> { safeGroup, safeKeywords };
                    if (groupExtra.TryGetValue(name, out var extra))
                    {
                        foreach (var val in extra)
                            row.Add(EscapeCsvField(val, separator));
                    }
                    sb.AppendLine(string.Join(separator.ToString(), row));
                }

                File.WriteAllText(actualOutput, sb.ToString(), new UTF8Encoding(true));
                ConsoleUtils.WriteLine($"\n[УСПЕХ] Сохранено: {actualOutput} ({groups.Count} строк)", ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine($"\n[ОШИБКА] Не удалось сохранить: {ex.Message}", ConsoleColor.Red);
            }
        }

        /// <summary>
        /// Разбивает CSV-строку на поля с учётом кавычек.
        /// Поддерживает экранирование "" внутри кавычек.
        /// </summary>
        private static string[] SplitCsvLine(string line, char separator)
        {
            var fields = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    // Поле в кавычках
                    i++; // пропускаем открывающую кавычку
                    var sb = new StringBuilder();
                    while (i < line.Length)
                    {
                        if (line[i] == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                sb.Append('"');
                                i += 2;
                            }
                            else
                            {
                                i++; // закрывающая кавычка
                                break;
                            }
                        }
                        else
                        {
                            sb.Append(line[i]);
                            i++;
                        }
                    }
                    fields.Add(sb.ToString());
                    // Пропускаем разделитель после поля
                    if (i < line.Length && line[i] == separator) i++;
                }
                else
                {
                    // Обычное поле до разделителя или конца строки
                    int start = i;
                    while (i < line.Length && line[i] != separator)
                        i++;
                    fields.Add(line[start..i]);
                    if (i < line.Length && line[i] == separator) i++;
                }
            }
            return fields.ToArray();
        }

        /// <summary>
        /// Экранирует поле для CSV: если содержит разделитель, кавычку или перевод строки — обрамляет в кавычки.
        /// </summary>
        private static string EscapeCsvField(string field, char separator)
        {
            if (field.Contains(separator) || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        /// <summary>
        /// Определяет кодировку CSV-файла:
        /// 1. BOM (EF BB BF) -> UTF-8
        /// 2. Иначе пробует UTF-8, проверяет на наличие замен (U+FFFD)
        /// 3. Если есть замены или нечитаемые символы — fallback на Windows-1251
        /// </summary>
        private static Encoding DetectCsvEncoding(string path)
        {
            byte[] header = new byte[3];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Read(header, 0, 3) < 3)
                    return Encoding.UTF8;
            }

            // UTF-8 BOM: EF BB BF
            if (header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
                return Encoding.UTF8;

            // Пробуем UTF-8. Если после декодирования есть символы замены (U+FFFD) —
            // значит, файл не в UTF-8, пробуем Windows-1251.
            byte[] sample = new byte[Math.Min(4096, new FileInfo(path).Length)];
            using (var fs = File.OpenRead(path))
            {
                fs.Read(sample, 0, sample.Length);
            }

            string utf8Result = Encoding.UTF8.GetString(sample);
            if (utf8Result.Contains('\uFFFD'))
            {
                try { return Encoding.GetEncoding(1251); }
                catch { return Encoding.UTF8; }
            }

            return Encoding.UTF8;
        }
    }
}
