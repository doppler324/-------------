using System;
using System.Net.Http;
using System.Net.Http.Headers;
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
        /// Отправляет запрос в DeepSeek/OpenRouter с возможностью переопределить
        /// thinking/reasoning_effort и endpoint для конкретного вызова.
        /// </summary>
        /// <param name="endpoint">
        /// URL API endpoint. По умолчанию "https://api.deepseek.com/chat/completions".
        /// Для OpenRouter: "https://openrouter.ai/api/v1/chat/completions".
        /// </param>
        /// <param name="apiKeyOverride">
        /// Если передан — используется этот ключ вместо settings.ApiKey.
        /// Нужен для OpenRouter (у которого отдельный API-ключ).
        /// </param>
        /// <param name="skipDeepSeekFields">
        /// Если true — не добавляет DeepSeek-specific поля (thinking, reasoning_effort).
        /// Нужно для OpenRouter с не-DeepSeek моделями (Gemini, Claude, GPT).
        /// </param>
        public static async Task<T?> SendRawRequestAsync<T>(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            bool? overrideThinking = null,
            string? overrideReasoningEffort = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false) where T : class
        {
            // Для OpenRouter не шлём DeepSeek-specific поля,
            // но добавляем response_format для строгого JSON-вывода
            var requestBody = skipDeepSeekFields
                ? (object)new
                {
                    model = settings.Model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    temperature = settings.Temperature,
                    max_tokens = settings.MaxTokens,
                    top_p = settings.TopP,
                    stream = settings.Stream,
                    response_format = new { type = "json_object" }
                }
                : (object)new
                {
                    model = settings.Model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    temperature = settings.Temperature,
                    max_tokens = settings.MaxTokens,
                    top_p = settings.TopP,
                    stream = settings.Stream,
                    reasoning_effort = overrideReasoningEffort ?? settings.ReasoningEffort,
                    thinking = (overrideThinking ?? settings.EnableThinking)
                        ? new { type = "enabled" }
                        : new { type = "disabled" }
                };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            string targetUrl = endpoint ?? "https://api.deepseek.com/chat/completions";

            try
            {
                // Если передан apiKeyOverride — временно меняем заголовок
                var originalAuth = client.DefaultRequestHeaders.Authorization;
                if (!string.IsNullOrEmpty(apiKeyOverride))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKeyOverride);
                }

                var response = await client.PostAsync(targetUrl, httpContent);

                // Восстанавливаем оригинальный заголовок
                if (!string.IsNullOrEmpty(apiKeyOverride))
                {
                    client.DefaultRequestHeaders.Authorization = originalAuth;
                }

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

                // Если очищенный текст всё ещё не похож на JSON — пытаемся извлечь
                // JSON из markdown-кода (```json ... ```) — нужно для Claude и др.
                if (!cleanJson.TrimStart().StartsWith("{") && !cleanJson.TrimStart().StartsWith("["))
                {
                    var extracted = ExtractJsonFromMarkdown(rawAiResponse);
                    if (extracted != null)
                        cleanJson = extracted;
                }

                try
                {
                    return JsonSerializer.Deserialize<T>(cleanJson);
                }
                catch (JsonException)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[ОШИБКА] Не удалось распарсить ответ AI как {typeof(T).Name}.");
                    Console.WriteLine($"  Первые 500 символов ответа:");
                    Console.WriteLine($"  {cleanJson[..Math.Min(cleanJson.Length, 500)]}");
                    Console.ResetColor();
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ОШИБКА] SendRawRequestAsync: {ex.GetType().Name}");
                if (ex is JsonException)
                {
                    // Уже обработано выше
                }
                Console.ResetColor();
                return null;
            }
        }

        /// <summary>
        /// Возвращает сырой текст ответа AI (без попытки десериализации).
        /// Полезно, когда нужно попробовать несколько форматов парсинга.
        /// </summary>
        public static async Task<string?> GetRawAiContentAsync(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            bool? overrideThinking = null,
            string? overrideReasoningEffort = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false)
        {
            bool useThinking = overrideThinking ?? settings.EnableThinking;
            string useReasoning = overrideReasoningEffort ?? settings.ReasoningEffort;

            var requestBody = skipDeepSeekFields
                ? (object)new
                {
                    model = settings.Model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    temperature = settings.Temperature,
                    max_tokens = settings.MaxTokens,
                    top_p = settings.TopP,
                    stream = settings.Stream,
                    response_format = new { type = "json_object" }
                }
                : (object)new
                {
                    model = settings.Model,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userMessage }
                    },
                    temperature = settings.Temperature,
                    max_tokens = settings.MaxTokens,
                    top_p = settings.TopP,
                    stream = settings.Stream,
                    reasoning_effort = useReasoning,
                    thinking = useThinking
                        ? new { type = "enabled" }
                        : new { type = "disabled" }
                };

            string jsonContent = JsonSerializer.Serialize(requestBody);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            string targetUrl = endpoint ?? "https://api.deepseek.com/chat/completions";

            try
            {
                var originalAuth = client.DefaultRequestHeaders.Authorization;
                if (!string.IsNullOrEmpty(apiKeyOverride))
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKeyOverride);

                var response = await client.PostAsync(targetUrl, httpContent);

                if (!string.IsNullOrEmpty(apiKeyOverride))
                    client.DefaultRequestHeaders.Authorization = originalAuth;

                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[ОШИБКА API] Код: {response.StatusCode}");
                    Console.WriteLine($"  Ответ: {responseString[..Math.Min(responseString.Length, 300)]}");
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

                return CleanJsonMarkdown(rawAiResponse);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ОШИБКА] GetRawAiContentAsync: {ex.GetType().Name}");
                Console.ResetColor();
                return null;
            }
        }

        /// <summary>
        /// Ищет JSON в markdown-тексте по шаблону ```json ... ``` или ``` ... ```.
        /// Возвращает первый найденный JSON-блок или null.
        /// Нужно для моделей (Claude, Gemini), которые не поддерживают
        /// response_format: { type: "json_object" }.
        /// </summary>
        public static string? ExtractJsonFromMarkdown(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            // Ищем ```json ... ```
            int jsonStart = input.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (jsonStart >= 0)
            {
                jsonStart += 7; // пропускаем ```json
                int jsonEnd = input.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                    return input[jsonStart..jsonEnd].Trim();
            }

            // Ищем ``` ... ``` (без указания языка)
            jsonStart = input.IndexOf("```");
            if (jsonStart >= 0)
            {
                jsonStart += 3;
                int jsonEnd = input.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                    return input[jsonStart..jsonEnd].Trim();
            }

            // Пробуем найти { ... } или [ ... ] в тексте
            int braceStart = input.IndexOf('{');
            int bracketStart = input.IndexOf('[');
            int firstStart = braceStart >= 0 && (bracketStart < 0 || braceStart < bracketStart)
                ? braceStart : bracketStart;

            if (firstStart >= 0)
            {
                // Ищем закрывающий символ
                char open = input[firstStart];
                char close = open == '{' ? '}' : ']';
                int depth = 0;
                for (int i = firstStart; i < input.Length; i++)
                {
                    if (input[i] == open) depth++;
                    else if (input[i] == close) { depth--; if (depth == 0) return input[firstStart..(i + 1)].Trim(); }
                }
            }

            return null;
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
