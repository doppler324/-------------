using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// Статический helper для работы с DeepSeek API.
    /// Содержит методы отправки запроса к нейросети и очистки ответа от markdown-разметки.
    /// </summary>
    public static class DeepSeekHelper
    {
        /// <summary>
        /// Отправляет произвольный системный промпт и пользовательское сообщение в DeepSeek API.
        /// </summary>
        /// <param name="client">HTTP-клиент с настроенной авторизацией.</param>
        /// <param name="systemPrompt">Системный промпт (собранный конструктором).</param>
        /// <param name="userMessage">Пользовательское сообщение (ключи или JSON кластеров).</param>
        /// <param name="settings">Параметры модели DeepSeek.</param>
        /// <returns>Словарь кластеров или null при ошибке.</returns>
        public static async Task<Dictionary<string, List<string>>?> SendRequestAsync(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings)
        {
            var requestBody = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                temperature = settings.Temperature,
                max_tokens = settings.MaxTokens,
                top_p = settings.TopP
            };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Отправка POST запроса
            var response = await client.PostAsync(
                "https://api.deepseek.com/chat/completions", httpContent);

            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ОШИБКА API] Код: {response.StatusCode}\n{responseString}");
                Console.ResetColor();
                return null;
            }

            // Парсинг ответа API
            using JsonDocument doc = JsonDocument.Parse(responseString);
            var root = doc.RootElement;
            string rawAiResponse = root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            // Очистка текста от лишних символов (форматирования чата)
            string cleanJson = CleanJsonMarkdown(rawAiResponse);

            // Десериализация JSON в модель DeepSeekResponse (структура из system_prompt.txt)
            var responseObj = JsonSerializer.Deserialize<DeepSeekResponse>(cleanJson);

            if (responseObj == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ОШИБКА] Не удалось десериализовать ответ нейросети.");
                Console.WriteLine($"Первые 200 символов ответа:\n{rawAiResponse[..Math.Min(200, rawAiResponse.Length)]}");
                Console.ResetColor();
                return null;
            }

            // Преобразование DeepSeekResponse в Dictionary<string, List<string>>
            return ConvertToDictionary(responseObj);
        }

        /// <summary>
        /// Отправляет запрос в DeepSeek и возвращает сырой ответ, десериализованный
        /// в указанный тип T. Используется для кастомных JSON-форматов ответа,
        /// отличных от DeepSeekResponse (например, RefinedCluster).
        /// </summary>
        public static async Task<T?> SendRawRequestAsync<T>(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings) where T : class
        {
            var requestBody = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                temperature = settings.Temperature,
                max_tokens = settings.MaxTokens,
                top_p = settings.TopP
            };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                var response = await client.PostAsync(
                    "https://api.deepseek.com/chat/completions", httpContent);

                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[ОШИБКА API] Код: {response.StatusCode}");
                    Console.ResetColor();
                    return null;
                }

                using JsonDocument doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;
                string rawAiResponse = root
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                string cleanJson = CleanJsonMarkdown(rawAiResponse);
                return JsonSerializer.Deserialize<T>(cleanJson);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ОШИБКА] SendRawRequestAsync: {ex.GetType().Name}");
                Console.ResetColor();
                return null;
            }
        }

        /// <summary>
        /// Преобразует DeepSeekResponse (clusters + unclustered) в словарь, ожидаемый пайплайном.
        /// Нераспределённые ключи помещаются в служебный кластер "Нераспределённые".
        /// </summary>
        private static Dictionary<string, List<string>> ConvertToDictionary(DeepSeekResponse response)
        {
            var result = new Dictionary<string, List<string>>();

            foreach (var cluster in response.Clusters)
            {
                if (!string.IsNullOrWhiteSpace(cluster.ClusterName) && cluster.Keywords.Count > 0)
                {
                    result[cluster.ClusterName] = cluster.Keywords;
                }
            }

            // Нераспределённые ключи — в отдельный кластер
            if (response.Unclustered.Count > 0)
            {
                result["Нераспределённые"] = response.Unclustered;
            }

            return result;
        }

        /// <summary>
        /// Удаляет обертку ```json ... ```, если ИИ её вернул.
        /// </summary>
        public static string CleanJsonMarkdown(string input)
        {
            string cleaned = input.Trim();
            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(7);
            }
            else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(3);
            }

            if (cleaned.EndsWith("```"))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 3);
            }

            return cleaned.Trim();
        }
    }
}
