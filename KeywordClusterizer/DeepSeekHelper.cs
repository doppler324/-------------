using System;
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
        /// Отправляет запрос в DeepSeek и возвращает сырой ответ, десериализованный
        /// в указанный тип T. Используется для кастомных JSON-форматов ответа.
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
