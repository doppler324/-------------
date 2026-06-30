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
            Console.WriteLine("=== Кластеризатор ключевых слов (DeepSeek) ===");

            // 1. Загрузка настроек
            var settingsPath = "settings.json";
            var deepSeekSettings = new DeepSeekSettings();
            var businessSettings = new BusinessSettings();
            var serpSettings = new XmlRiverSettings();

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

                        if (business.TryGetProperty("chunkSize", out var cs))
                            businessSettings.ChunkSize = cs.GetInt32();
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

                        if (serp.TryGetProperty("enableValidation", out var ev))
                            serpSettings.EnableValidation = ev.GetBoolean();

                        if (serp.TryGetProperty("minOverlap", out var mo))
                            serpSettings.MinOverlap = mo.GetDouble();

                        if (serp.TryGetProperty("topResultsCount", out var trc))
                            serpSettings.TopResultsCount = trc.GetInt32();

                        if (serp.TryGetProperty("sampleSize", out var ss))
                            serpSettings.SampleSize = ss.GetInt32();

                        if (serp.TryGetProperty("maxRetries", out var mr))
                            serpSettings.MaxRetries = mr.GetInt32();

                        if (serp.TryGetProperty("retryDelayMs", out var rd))
                            serpSettings.RetryDelayMs = rd.GetInt32();

                        if (serp.TryGetProperty("enableFinalValidation", out var efv))
                            serpSettings.EnableFinalValidation = efv.GetBoolean();
                    }
                }
                catch
                {
                    // используем значения по умолчанию
                }
            }

            // 2. Проверка API ключа
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

            // 4. Настройка HTTP клиента
            using var client = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", deepSeekSettings.ApiKey);

            // 5. Запуск пайплайна кластеризации
            var pipeline = new ClusterizationPipeline(client, deepSeekSettings, businessSettings, serpSettings);
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
