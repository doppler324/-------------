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
    /// Отдельный режим «Наименование кластеров через ИИ» (naming из clusters.csv).
    /// Каждый кластер отправляется в нейросеть ПО ОДНОМУ (название + все ключи),
    /// AI придумывает новый H1-заголовок. Обработка параллельная (до MaxConcurrency потоков).
    /// Возвращает словарь: старое имя кластера → новое имя (только для успешно переименованных).
    /// </summary>
    public class ClusterNamingPass
    {
        private readonly HttpClient _client;
        private readonly DeepSeekSettings _deepSeekSettings;
        private readonly OpenRouterSettings _openRouterSettings;
        private readonly NamingSettings _namingSettings;
        private readonly BusinessSettings? _businessSettings;

        /// <summary>Строка консоли для перезаписываемого прогресса + блокировка записи.</summary>
        private int _progressLine;
        private int _lineWidth;
        private readonly object _consoleLock = new();

        /// <param name="namingSettings">Настройки наименования (провайдер, модель, число потоков).</param>
        /// <param name="businessSettings">Опционально: ниша/логика — добавляется в системный промпт.</param>
        public ClusterNamingPass(
            HttpClient client,
            DeepSeekSettings deepSeekSettings,
            OpenRouterSettings openRouterSettings,
            NamingSettings namingSettings,
            BusinessSettings? businessSettings = null)
        {
            _client = client;
            _deepSeekSettings = deepSeekSettings;
            _openRouterSettings = openRouterSettings;
            _namingSettings = namingSettings;
            _businessSettings = businessSettings;
        }

        /// <summary>
        /// Запускает наименование кластеров: параллельно отправляет каждый кластер в ИИ.
        /// </summary>
        /// <param name="clusters">Кластеры: имя → ключи.</param>
        /// <returns>Словарь старое имя → новое имя (только для успешно переименованных кластеров).</returns>
        public async Task<Dictionary<string, string>> RunAsync(
            Dictionary<string, List<string>> clusters)
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (clusters == null || clusters.Count == 0)
                return renames;

            ConsoleUtils.WriteLine(
                $"\n--- Наименование кластеров через ИИ (потоков: {_namingSettings.MaxConcurrency}) ---",
                ConsoleColor.Cyan);
            ConsoleUtils.WriteLine(
                $"  [Naming] Запуск наименования {clusters.Count} кластеров...",
                ConsoleColor.DarkGray);

            string systemPrompt = LoadInstruction("instructions/naming_instruction.txt");

            // Бизнес-контекст (ниша/логика), если задан
            if (_businessSettings != null)
            {
                systemPrompt += $"\nНиша сайта: {_businessSettings.Niche}. Логика кластеризации: {_businessSettings.ClusteringLogic}.";
            }

            var items = clusters.ToList();
            int maxConcurrency = Math.Max(1, _namingSettings.MaxConcurrency);
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            int completed = 0;
            int total = items.Count;

            _progressLine = Console.CursorTop;
            _lineWidth = Console.WindowWidth - 1;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Фоновая задача: каждые 10 секунд обновляет строку «прошло Nс, обработано i/N»,
            // чтобы было видно, что идёт работа, даже если первый запрос долгий.
            using var progressCts = new CancellationTokenSource();
            var progressTask = Task.Run(async () =>
            {
                try
                {
                    while (!progressCts.IsCancellationRequested)
                    {
                        await Task.Delay(10000, progressCts.Token);
                        if (progressCts.IsCancellationRequested) break;
                        int doneNow = Volatile.Read(ref completed);
                        WriteProgress($"  [Naming] Обработано {doneNow}/{total}, прошло {stopwatch.Elapsed.TotalSeconds:F0}с...");
                    }
                }
                catch (TaskCanceledException) { }
                catch (ObjectDisposedException) { }
            });

            var tasks = items.Select(async kvp =>
            {
                await semaphore.WaitAsync();
                try
                {
                    string? newName = await NameClusterAsync(kvp.Key, kvp.Value, systemPrompt);
                    int done = Interlocked.Increment(ref completed);

                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        lock (_consoleLock)
                        {
                            renames[kvp.Key] = newName.Trim();
                        }
                        WriteProgress($"  [Naming] {done}/{total} — «{kvp.Key}» → «{newName.Trim()}»");
                    }
                    else
                    {
                        WriteProgress($"  [Naming] {done}/{total} — «{kvp.Key}» (без изменений)");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            progressCts.Cancel();
            try { await progressTask; } catch { }
            stopwatch.Stop();
            ClearProgressLine();

            ConsoleUtils.WriteLine(
                $"[Naming] Готово за {stopwatch.Elapsed.TotalSeconds:F1}с: переименовано {renames.Count} из {total} кластеров.",
                ConsoleColor.Cyan);

            return renames;
        }

        /// <summary>
        /// Отправляет ОДИН кластер в ИИ и возвращает новый H1-заголовок (или null при ошибке).
        /// </summary>
        private async Task<string?> NameClusterAsync(
            string clusterName, List<string> keywords, string systemPrompt)
        {
            // Формируем вход: название кластера + все ключи (нумерованные)
            var lines = new List<string>
            {
                $"Кластер: {clusterName}",
                $"Ключей: {keywords.Count}",
                ""
            };
            for (int i = 0; i < keywords.Count; i++)
                lines.Add($"{i + 1}. {keywords[i]}");

            string userMessage = string.Join("\n", lines);

            var (response, error) = await DeepSeekHelper.SendWithRetryAsync<NamingResponse>(
                _client, systemPrompt, userMessage, BuildConfig(),
                maxRetries: 3, baseDelayMs: 5000,
                endpoint: Endpoint, apiKeyOverride: ApiKeyOverride, skipDeepSeekFields: UseOpenRouter);

            if (response == null || string.IsNullOrWhiteSpace(response.Name))
            {
                if (error != ApiErrorType.None && error != ApiErrorType.ParseError)
                {
                    lock (_consoleLock)
                    {
                        // Для сетевых ошибок явно указываем, что нейросеть не отвечает
                        string reason = error == ApiErrorType.NetworkError
                            ? "нейросеть не отвечает"
                            : DeepSeekHelper.DescribeError(error);
                        ConsoleUtils.WriteLine(
                            $"  [Naming] «{clusterName}»: {reason}. Оставлено без изменений.",
                            ConsoleColor.Yellow);
                    }
                }
                return null;
            }

            return response.Name.Trim();
        }

        /// <summary>Загружает содержимое файла инструкции. При отсутствии — возвращает базовую заглушку.</summary>
        private static string LoadInstruction(string filePath)
        {
            if (File.Exists(filePath))
                return File.ReadAllText(filePath).Trim();

            ConsoleUtils.WriteLine($"[ПРЕДУПРЕЖДЕНИЕ] Файл '{filePath}' не найден.", ConsoleColor.Yellow);
            return "Верни ответ строго в формате JSON. Никакого текста до или после JSON.";
        }

        /// <summary>true, если выбран OpenRouter (провайдер из настроек наименования).</summary>
        private bool UseOpenRouter =>
            _namingSettings.Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase);

        /// <summary>Endpoint для OpenRouter, иначе null (по умолчанию DeepSeek).</summary>
        private string? Endpoint => UseOpenRouter ? "https://openrouter.ai/api/v1/chat/completions" : null;

        /// <summary>API-ключ для OpenRouter, иначе null (используется ключ DeepSeek).</summary>
        private string? ApiKeyOverride => UseOpenRouter ? _openRouterSettings.ApiKey : null;

        /// <summary>
        /// Собирает DeepSeekSettings для вызова AI из настроек наименования,
        /// подставляя значения из deepseek, где не заданы свои.
        /// </summary>
        private DeepSeekSettings BuildConfig()
        {
            bool thinking = _namingSettings.EnableThinking ?? _deepSeekSettings.EnableThinking;
            string reasoningEffort = _namingSettings.ReasoningEffort ?? _deepSeekSettings.ReasoningEffort;

            // Уважаем настройку: выключен thinking → не заставляем модель долго думать
            if (!thinking)
                reasoningEffort = "low";

            return new DeepSeekSettings
            {
                ApiKey = _deepSeekSettings.ApiKey,
                Model = !string.IsNullOrEmpty(_namingSettings.Model)
                    ? _namingSettings.Model : _deepSeekSettings.Model,
                Temperature = _namingSettings.Temperature ?? _deepSeekSettings.Temperature,
                MaxTokens = _namingSettings.MaxTokens ?? _deepSeekSettings.MaxTokens,
                TopP = _deepSeekSettings.TopP,
                EnableThinking = thinking,
                ReasoningEffort = reasoningEffort,
                Stream = _namingSettings.Stream ?? _deepSeekSettings.Stream
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
    }
}
