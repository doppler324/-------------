using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// Тип ошибки при запросе к API нейросети.
    /// Используется для принятия решения о retry.
    /// </summary>
    public enum ApiErrorType
    {
        /// <summary>Успешный ответ, ошибки нет.</summary>
        None,

        /// <summary>401 Unauthorized — неверный API ключ. Retry бесполезен.</summary>
        Unauthorized,

        /// <summary>400 Bad Request — битый промпт или некорректный запрос. Retry бесполезен.</summary>
        BadRequest,

        /// <summary>429 Too Many Requests — превышен rate limit. Retry с backoff.</summary>
        RateLimited,

        /// <summary>5xx — временная ошибка сервера. Retry с backoff.</summary>
        ServerError,

        /// <summary>Сетевая ошибка (таймаут, сброс соединения). Retry с backoff.</summary>
        NetworkError,

        /// <summary>Ошибка парсинга ответа AI (невалидный JSON). Retry обычно бесполезен.</summary>
        ParseError
    }

    /// <summary>
    /// Статический helper для работы с DeepSeek/OpenRouter API.
    /// Содержит методы отправки запроса к нейросети, retry-логику и очистку ответа от markdown-разметки.
    /// </summary>
    public static class DeepSeekHelper
    {
        private const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";

        /// <summary>
        /// Ошибки, при которых повторная попытка бессмысленна:
        /// неверный ключ, битый запрос или ответ, который AI в любом случае воспроизведёт снова.
        /// </summary>
        private static bool IsNonRetryable(ApiErrorType error) =>
            error is ApiErrorType.Unauthorized or ApiErrorType.BadRequest or ApiErrorType.ParseError;

        /// <summary>
        /// Отправляет запрос в DeepSeek/OpenRouter и десериализует JSON-ответ в тип T.
        /// Возвращает кортеж (результат, тип_ошибки); Error == None означает успех.
        /// </summary>
        /// <param name="endpoint">URL API endpoint. По умолчанию — DeepSeek. Для OpenRouter передайте свой URL.</param>
        /// <param name="apiKeyOverride">Если передан — используется вместо settings.ApiKey (нужно для OpenRouter).</param>
        /// <param name="skipDeepSeekFields">true — не добавлять DeepSeek-specific поля (для моделей на OpenRouter).</param>
        public static async Task<(T? Result, ApiErrorType Error)> SendRawRequestAsync<T>(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            bool? overrideThinking = null,
            string? overrideReasoningEffort = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false) where T : class
        {
            var (rawContent, error) = await PostAndGetContentAsync(
                client, systemPrompt, userMessage, settings,
                overrideThinking, overrideReasoningEffort, endpoint, apiKeyOverride, skipDeepSeekFields);

            if (error != ApiErrorType.None || rawContent == null)
                return (null, error);

            string cleanJson = ExtractJson(rawContent);

            try
            {
                return (JsonSerializer.Deserialize<T>(cleanJson), ApiErrorType.None);
            }
            catch (JsonException ex)
            {
                ConsoleUtils.WriteLine($"\n[ОШИБКА] Не удалось распарсить ответ AI как {typeof(T).Name}.", ConsoleColor.Yellow);
                ConsoleUtils.WriteLine($"  Позиция ошибки: Path='{ex.Path}', Line={ex.LineNumber}, Column={ex.BytePositionInLine}", ConsoleColor.Yellow);
                // Фрагмент вокруг позиции ошибки (по 80 символов влево/вправо) — чтобы увидеть проблемное место
                string fragment = GetFragmentAround(cleanJson, ex.LineNumber, ex.BytePositionInLine);
                ConsoleUtils.WriteLine($"  Фрагмент вокруг ошибки:\n  {fragment}", ConsoleColor.Yellow);
                ConsoleUtils.WriteLine($"  Первые 500 символов ответа:\n  {cleanJson[..Math.Min(cleanJson.Length, 500)]}", ConsoleColor.Yellow);
                return (null, ApiErrorType.ParseError);
            }
        }

        /// <summary>
        /// Возвращает сырой (очищенный от markdown) текст ответа AI без десериализации.
        /// Полезно, когда нужно попробовать несколько форматов парсинга.
        /// </summary>
        public static async Task<(string? Result, ApiErrorType Error)> GetRawAiContentAsync(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            bool? overrideThinking = null,
            string? overrideReasoningEffort = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false)
        {
            return await PostAndGetContentAsync(
                client, systemPrompt, userMessage, settings,
                overrideThinking, overrideReasoningEffort, endpoint, apiKeyOverride, skipDeepSeekFields);
        }

        /// <summary>
        /// Отправляет запрос с автоматическим retry.
        /// Не ретраит 401/400/ParseError (бессмысленно) — сразу возвращает ошибку.
        /// Для 429/5xx/сетевых ошибок — exponential backoff с jitter.
        /// </summary>
        /// <param name="maxRetries">Максимальное количество попыток (включая первую).</param>
        /// <param name="baseDelayMs">Базовая задержка перед повтором в мс (удваивается с каждой попыткой).</param>
        public static async Task<(T? Result, ApiErrorType Error)> SendWithRetryAsync<T>(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            int maxRetries = 3,
            int baseDelayMs = 5000,
            bool? overrideThinking = null,
            string? overrideReasoningEffort = null,
            string? endpoint = null,
            string? apiKeyOverride = null,
            bool skipDeepSeekFields = false) where T : class
        {
            (T? Result, ApiErrorType Error) last = (null, ApiErrorType.NetworkError);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                last = await SendRawRequestAsync<T>(
                    client, systemPrompt, userMessage, settings,
                    overrideThinking, overrideReasoningEffort,
                    endpoint, apiKeyOverride, skipDeepSeekFields);

                if (last.Error == ApiErrorType.None || IsNonRetryable(last.Error))
                    return last;

                if (attempt < maxRetries)
                    await Task.Delay(NextDelayWithJitter(baseDelayMs, attempt, last.Error, maxRetries));
            }

            return last;
        }

        /// <summary>
        /// Вычисляет задержку перед следующей попыткой: exponential backoff (baseDelay * 2^(attempt-1)) ± 20% jitter.
        /// Одновременно печатает сообщение о повторе в консоль.
        /// </summary>
        private static int NextDelayWithJitter(int baseDelayMs, int attempt, ApiErrorType error, int maxRetries)
        {
            int delayMs = (int)(baseDelayMs * Math.Pow(2, attempt - 1));
            int jitter = Random.Shared.Next(-(delayMs / 5), delayMs / 5 + 1); // ±20%
            int actualDelay = Math.Max(100, delayMs + jitter);

            ConsoleUtils.WriteLine(
                $"\n  [RETRY] Попытка {attempt}/{maxRetries} — {DescribeError(error)}. Повтор через {actualDelay / 1000}с...",
                ConsoleColor.Yellow);

            return actualDelay;
        }

        /// <summary>Человекочитаемое описание типа ошибки для лога.</summary>
        public static string DescribeError(ApiErrorType error) => error switch
        {
            ApiErrorType.Unauthorized => "неверный API ключ",
            ApiErrorType.BadRequest => "битый запрос",
            ApiErrorType.RateLimited => "rate limit",
            ApiErrorType.ServerError => "ошибка сервера",
            ApiErrorType.NetworkError => "сетевая ошибка",
            ApiErrorType.ParseError => "ошибка парсинга ответа",
            _ => "неизвестная ошибка"
        };

        /// <summary>
        /// Единая точка отправки HTTP-запроса к chat/completions API.
        /// Собирает тело запроса, временно подменяет Authorization (если передан apiKeyOverride),
        /// обрабатывает сетевые ошибки/таймауты и HTTP-статусы, извлекает text-контент ответа.
        /// </summary>
        private static async Task<(string? Content, ApiErrorType Error)> PostAndGetContentAsync(
            HttpClient client, string systemPrompt, string userMessage,
            DeepSeekSettings settings,
            bool? overrideThinking, string? overrideReasoningEffort,
            string? endpoint, string? apiKeyOverride, bool skipDeepSeekFields)
        {
            var requestBody = BuildRequestBody(settings, systemPrompt, userMessage, skipDeepSeekFields, overrideThinking, overrideReasoningEffort);
            var httpContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            string targetUrl = endpoint ?? DefaultEndpoint;

            try
            {
                HttpResponseMessage response;
                using (TemporaryAuthHeader(client, apiKeyOverride))
                {
                    try
                    {
                        response = await client.PostAsync(targetUrl, httpContent);
                    }
                    catch (TaskCanceledException)
                    {
                        return (null, ApiErrorType.NetworkError); // таймаут запроса
                    }
                    catch (HttpRequestException)
                    {
                        return (null, ApiErrorType.NetworkError); // обрыв соединения, DNS и т.п.
                    }
                }

                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    ConsoleUtils.WriteLine($"\n[ОШИБКА API] Код: {response.StatusCode}", ConsoleColor.Red);
                    return (null, MapStatusToError(response.StatusCode));
                }

                using JsonDocument doc = JsonDocument.Parse(responseString);
                string content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "";

                return (content, ApiErrorType.None);
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine($"\n[ОШИБКА] Запрос к API: {ex.GetType().Name}", ConsoleColor.Yellow);
                return (null, ApiErrorType.NetworkError);
            }
        }

        /// <summary>
        /// Временно подменяет Authorization-заголовок клиента на apiKeyOverride
        /// и восстанавливает исходный при Dispose(). Если override не задан — no-op.
        /// </summary>
        private static IDisposable TemporaryAuthHeader(HttpClient client, string? apiKeyOverride)
        {
            if (string.IsNullOrEmpty(apiKeyOverride))
                return NullScope.Instance;

            var original = client.DefaultRequestHeaders.Authorization;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKeyOverride);
            return new RestoreAuthScope(client, original);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }

        private sealed class RestoreAuthScope : IDisposable
        {
            private readonly HttpClient _client;
            private readonly AuthenticationHeaderValue? _original;

            public RestoreAuthScope(HttpClient client, AuthenticationHeaderValue? original)
            {
                _client = client;
                _original = original;
            }

            public void Dispose() => _client.DefaultRequestHeaders.Authorization = _original;
        }

        /// <summary>Сопоставляет HTTP-статус ответа с типом ошибки для retry-логики.</summary>
        private static ApiErrorType MapStatusToError(HttpStatusCode statusCode) => (int)statusCode switch
        {
            401 => ApiErrorType.Unauthorized,
            400 => ApiErrorType.BadRequest,
            429 => ApiErrorType.RateLimited,
            >= 500 => ApiErrorType.ServerError,
            _ => ApiErrorType.NetworkError
        };

        /// <summary>
        /// Собирает тело запроса к chat/completions.
        /// Для OpenRouter (skipDeepSeekFields=true) не добавляет DeepSeek-specific поля
        /// (thinking, reasoning_effort), но включает response_format для строгого JSON-вывода.
        /// </summary>
        private static object BuildRequestBody(
            DeepSeekSettings settings, string systemPrompt, string userMessage,
            bool skipDeepSeekFields, bool? overrideThinking, string? overrideReasoningEffort)
        {
            var messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            };

            if (skipDeepSeekFields)
            {
                return new
                {
                    model = settings.Model,
                    messages,
                    temperature = settings.Temperature,
                    max_tokens = settings.MaxTokens,
                    top_p = settings.TopP,
                    stream = settings.Stream,
                    response_format = new { type = "json_object" }
                };
            }

            return new
            {
                model = settings.Model,
                messages,
                temperature = settings.Temperature,
                max_tokens = settings.MaxTokens,
                top_p = settings.TopP,
                stream = settings.Stream,
                reasoning_effort = overrideReasoningEffort ?? settings.ReasoningEffort,
                thinking = (overrideThinking ?? settings.EnableThinking)
                    ? new { type = "enabled" }
                    : new { type = "disabled" }
            };
        }

        /// <summary>
        /// Очищает ответ AI от markdown-обёртки и возвращает готовый к парсингу JSON-текст.
        /// Если после базовой очистки текст всё ещё не похож на JSON — пытается извлечь
        /// JSON-блок из markdown (нужно для моделей вроде Claude/Gemini).
        /// </summary>
        private static string ExtractJson(string rawAiResponse)
        {
            string cleanJson = CleanJsonMarkdown(rawAiResponse);

            bool looksLikeJson = cleanJson.TrimStart().StartsWith("{") || cleanJson.TrimStart().StartsWith("[");
            if (looksLikeJson)
                return cleanJson;

            return ExtractJsonFromMarkdown(rawAiResponse) ?? cleanJson;
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

            string? fenced = ExtractFencedBlock(input, "```json") ?? ExtractFencedBlock(input, "```");
            if (fenced != null)
                return fenced;

            return ExtractBalancedBraces(input);
        }

        /// <summary>Извлекает содержимое между парой ```fence ... ``` (используется для json/plain fences).</summary>
        private static string? ExtractFencedBlock(string input, string openFence)
        {
            int start = input.IndexOf(openFence, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            start += openFence.Length;
            int end = input.IndexOf("```", start);
            return end > start ? input[start..end].Trim() : null;
        }

        /// <summary>Извлекает первый сбалансированный блок {...} или [...] из произвольного текста.</summary>
        private static string? ExtractBalancedBraces(string input)
        {
            int braceStart = input.IndexOf('{');
            int bracketStart = input.IndexOf('[');
            int start = braceStart >= 0 && (bracketStart < 0 || braceStart < bracketStart) ? braceStart : bracketStart;
            if (start < 0) return null;

            char open = input[start];
            char close = open == '{' ? '}' : ']';
            int depth = 0;

            for (int i = start; i < input.Length; i++)
            {
                if (input[i] == open) depth++;
                else if (input[i] == close && --depth == 0) return input[start..(i + 1)].Trim();
            }

            return null;
        }

        /// <summary>Удаляет обёртку ```json ... ``` или ``` ... ```, если ИИ её вернул.</summary>
        public static string CleanJsonMarkdown(string input)
        {
            string cleaned = input.Trim();

            if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[7..];
            else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[3..];

            if (cleaned.EndsWith("```"))
                cleaned = cleaned[..^3];

            return cleaned.Trim();
        }

        /// <summary>
        /// Возвращает фрагмент текста вокруг позиции ошибки парсинга JSON
        /// (по 80 символов влево и вправо от позиции), чтобы увидеть проблемное место.
        /// Используется в диагностике при JsonException.
        /// </summary>
        private static string GetFragmentAround(string json, long? line, long? column)
        {
            // JsonException.LineNumber/BytePositionInLine нумеруются с 0
            if (line.HasValue && line.Value >= 0 && column.HasValue && column.Value >= 0)
            {
                string[] lines = json.Split('\n');
                if (line.Value < lines.Length)
                {
                    string targetLine = lines[line.Value];
                    int pos = (int)Math.Min(column.Value, targetLine.Length);
                    int start = Math.Max(0, pos - 80);
                    int len = Math.Min(targetLine.Length - start, 160);
                    if (len > 0)
                        return targetLine.Substring(start, len);
                }
            }

            // Fallback: первые 200 символов
            return json.Length <= 200 ? json : json[..200];
        }
    }
}
