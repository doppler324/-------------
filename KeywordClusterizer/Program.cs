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

                        if (business.TryGetProperty("mergeMode", out var mm))
                            businessSettings.MergeMode = mm.GetString() ?? businessSettings.MergeMode;

                        if (business.TryGetProperty("mergeThreshold", out var mt))
                            businessSettings.MergeThreshold = (float)mt.GetDouble();

                        if (business.TryGetProperty("centroidMergeEnabled", out var cme))
                            businessSettings.CentroidMergeEnabled = cme.GetBoolean();

                        if (business.TryGetProperty("skipNaming", out var sn))
                            businessSettings.SkipNaming = sn.GetBoolean();

                        if (business.TryGetProperty("wordLevelClustering", out var wlc))
                        {
                            if (wlc.TryGetProperty("enabled", out var wlce))
                                businessSettings.WordLevelClusteringEnabled = wlce.GetBoolean();

                            if (wlc.TryGetProperty("wordSimThreshold", out var wst))
                                businessSettings.WordSimThreshold = (float)wst.GetDouble();

                            if (wlc.TryGetProperty("hacThreshold", out var ht))
                                businessSettings.HacThreshold = (float)ht.GetDouble();
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
                deepSeekSettings.ApiKey = Console.ReadLine()?.Trim() ?? "";
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
                DisplayClusters(clusters);
                SaveToCsv(clusters, "clusters.csv");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[ОШИБКА] Кластеризация не дала результатов.");
                Console.ResetColor();
            }

            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
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
        /// </summary>
        private static void SaveToCsv(Dictionary<string, List<string>> clusters, string filePath)
        {
            try
            {
                // Используем UTF8 с BOM, чтобы Excel корректно понимал кириллицу
                using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));

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
                Console.WriteLine($"\n[УСПЕХ] Результаты успешно сохранены в файл: {filePath}");
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
