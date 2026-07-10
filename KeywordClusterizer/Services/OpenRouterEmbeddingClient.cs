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

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Клиент для получения эмбеддингов через OpenRouter API (модель text-embedding-3-small).
    /// Результаты кэшируются на диск в JSON-файл (текст запроса → массив float[]).
    /// </summary>
    public class OpenRouterEmbeddingClient
    {
        private readonly HttpClient _client;
        private readonly OpenRouterSettings _settings;

        // Кэш в памяти: текст запроса → эмбеддинг (float[])
        private Dictionary<string, float[]>? _cache;
        private readonly object _lock = new();
        private bool _cacheLoaded;

        // URL эндпоинта OpenRouter для эмбеддингов
        private const string EmbeddingEndpoint = "https://openrouter.ai/api/v1/embeddings";

        public OpenRouterEmbeddingClient(HttpClient client, OpenRouterSettings settings)
        {
            _client = client;
            _settings = settings;
        }

        /// <summary>
        /// Загружает кэш из JSON-файла (при первом вызове).
        /// </summary>
        private void LoadCache()
        {
            if (_cacheLoaded)
                return;

            lock (_lock)
            {
                if (_cacheLoaded)
                    return;

                if (!File.Exists(_settings.CachePath))
                {
                    Console.WriteLine($"    [EmbedCache] Кэш '{_settings.CachePath}' не найден. Будет создан при первом сохранении.");
                    _cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                    _cacheLoaded = true;
                    return;
                }

                try
                {
                    string json = File.ReadAllText(_settings.CachePath);
                    // Храним как Dictionary<string, List<double>> для JSON-совместимости
                    var rawCache = JsonSerializer.Deserialize<Dictionary<string, List<float>>>(json);
                    if (rawCache != null)
                    {
                        _cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kvp in rawCache)
                            _cache[kvp.Key] = kvp.Value.ToArray();
                    }
                    else
                    {
                        _cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                    }

                    Console.WriteLine($"    [EmbedCache] Загружено {_cache.Count} эмбеддингов из '{_settings.CachePath}'.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [EmbedCache] Ошибка загрузки кэша: {ex.Message}. Начинаем с пустого.");
                    Console.ResetColor();
                    _cache = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                }

                _cacheLoaded = true;
            }
        }

        /// <summary>
        /// Сохраняет кэш на диск (перезаписывает файл целиком).
        /// </summary>
        public void SaveCache()
        {
            if (_cache == null)
                return;

            lock (_lock)
            {
                try
                {
                    // Преобразуем float[] → List<float> для JSON-сериализации
                    var serializable = new Dictionary<string, List<float>>();
                    foreach (var kvp in _cache)
                        serializable[kvp.Key] = kvp.Value.ToList();

                    var options = new JsonSerializerOptions { WriteIndented = false };
                    string json = JsonSerializer.Serialize(serializable, options);
                    File.WriteAllText(_settings.CachePath, json);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [EmbedCache] Ошибка сохранения кэша: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        /// <summary>
        /// Получает эмбеддинг для одного текста (с проверкой кэша).
        /// </summary>
        public async Task<float[]> GetEmbeddingAsync(string text)
        {
            LoadCache();

            // Проверка кэша
            lock (_lock)
            {
                if (_cache != null && _cache.TryGetValue(text, out var cached))
                    return cached;
            }

            // Если нет в кэше — запрос к API
            var embeddings = await RequestEmbeddingsAsync(new[] { text });

            if (embeddings.TryGetValue(text, out var result))
            {
                // Сохраняем в кэш
                lock (_lock)
                {
                    if (_cache != null)
                        _cache[text] = result;
                }
                return result;
            }

            // Если API не ответил — возвращаем нулевой вектор (fallback)
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"    [Embed] Не удалось получить эмбеддинг для '{text}'. Возвращаю нулевой вектор.");
            Console.ResetColor();
            return new float[_settings.EmbeddingDimensions];
        }

        /// <summary>
        /// Получает эмбеддинги для списка текстов батч-запросом.
        /// Пропускает тексты, уже имеющиеся в кэше.
        /// </summary>
        public async Task<Dictionary<string, float[]>> GetEmbeddingsBatchAsync(List<string> texts)
        {
            LoadCache();

            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            var missingTexts = new List<string>();

            // Сначала собираем из кэша
            lock (_lock)
            {
                foreach (var text in texts)
                {
                    if (_cache != null && _cache.TryGetValue(text, out var cached))
                        result[text] = cached;
                    else
                        missingTexts.Add(text);
                }
            }

            // Остальные запрашиваем через API
            if (missingTexts.Count > 0)
            {
                Console.WriteLine($"    [Embed] Запрос {missingTexts.Count} эмбеддингов через OpenRouter...");

                var apiResult = await RequestEmbeddingsAsync(missingTexts);

                // Сохраняем в кэш
                lock (_lock)
                {
                    foreach (var kvp in apiResult)
                    {
                        result[kvp.Key] = kvp.Value;
                        if (_cache != null)
                            _cache[kvp.Key] = kvp.Value;
                    }
                }

                // Для текстов, не вернувшихся из API — нулевой вектор
                foreach (var text in missingTexts)
                {
                    if (!result.ContainsKey(text))
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    [Embed] Не удалось получить эмбеддинг для '{text}'. Возвращаю нулевой вектор.");
                        Console.ResetColor();
                        result[text] = new float[_settings.EmbeddingDimensions];
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Выполняет HTTP-запрос к OpenRouter API для получения эмбеддингов.
        /// </summary>
        private async Task<Dictionary<string, float[]>> RequestEmbeddingsAsync(IReadOnlyList<string> texts)
        {
            try
            {
                var requestBody = new
                {
                    model = _settings.EmbeddingModel,
                    input = texts
                };

                string jsonContent = JsonSerializer.Serialize(requestBody);
                var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // OpenRouter может принимать DeepSeek-ключ, но ожидается отдельный
                _client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

                var response = await _client.PostAsync(EmbeddingEndpoint, httpContent);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\n[ОШИБКА OpenRouter] Код: {response.StatusCode}");
                    Console.WriteLine($"Ответ: {responseString}");
                    Console.ResetColor();
                    return new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
                }

                return ParseEmbeddingResponse(responseString, texts);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n[ОШИБКА] OpenRouter Embedding: {ex.GetType().Name}: {ex.Message}");
                Console.ResetColor();
                return new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Парсит ответ OpenRouter API и возвращает словарь текст → эмбеддинг.
        /// </summary>
        private Dictionary<string, float[]> ParseEmbeddingResponse(
            string json, IReadOnlyList<string> texts)
        {
            var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Ответ OpenRouter: { data: [{ object, index, embedding }], model, usage }
                if (!root.TryGetProperty("data", out var data))
                    return result;

                foreach (var item in data.EnumerateArray())
                {
                    int index = item.GetProperty("index").GetInt32();
                    var embeddingArray = item.GetProperty("embedding");

                    if (index < 0 || index >= texts.Count)
                        continue;

                    var vector = new float[_settings.EmbeddingDimensions];
                    int i = 0;
                    foreach (var val in embeddingArray.EnumerateArray())
                    {
                        if (i < vector.Length)
                            vector[i++] = (float)val.GetDouble();
                    }

                    result[texts[index]] = vector;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    [Embed] Ошибка парсинга ответа: {ex.Message}");
                Console.ResetColor();
            }

            return result;
        }
    }
}
