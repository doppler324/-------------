using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using KeywordClusterizer.Models;
using KeywordClusterizer.Services;

namespace KeywordClusterizer
{
    /// <summary>
    /// HTTP-клиент для работы с API XmlRiver (Yandex XML).
    /// Поддерживает параллельные запросы, retry при пустых ответах
    /// и кэширование результатов в JSON-файл.
    /// </summary>
    public class XmlRiverClient
    {
        private readonly HttpClient _client;
        private readonly XmlRiverSettings _settings;
        private readonly SerpCacheService? _cache;

        /// <summary>Ссылка на кэш (для доступа извне).</summary>
        public SerpCacheService? Cache => _cache;

        public XmlRiverClient(HttpClient client, XmlRiverSettings settings, SerpCacheService? cache = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _cache = cache;
        }

        /// <summary>
        /// Выполняет поиск по одному ключевому слову через XmlRiver.
        /// При пустом ответе делает до MaxRetries повторов с задержкой RetryDelayMs.
        /// При включённом кэше сначала проверяет кэш.
        /// Возвращает топ-N URL из выдачи Yandex.
        /// </summary>
        public async Task<KeywordSearchResult> SearchAsync(string keyword)
        {
            var result = new KeywordSearchResult { Keyword = keyword };

            // Проверка кэша
            if (_cache != null && _settings.EnableCache && _cache.TryGet(keyword, out var cached))
            {
                return cached!;
            }

            for (int attempt = 1; attempt <= _settings.MaxRetries; attempt++)
            {
                try
                {
                    string encodedQuery = Uri.EscapeDataString(keyword);
                    string url = $"http://xmlriver.com/yandex/xml?user={_settings.XmlriverUser}" +
                                 $"&key={_settings.XmlriverKey}" +
                                 $"&query={encodedQuery}";

                    using var response = await _client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    string xmlContent = await response.Content.ReadAsStringAsync();
                    ParseXmlResponse(xmlContent, result);

                    // Если есть URL — успех, сохраняем в кэш и выходим
                    if (result.Urls.Count > 0)
                    {
                        if (_cache != null && _settings.EnableCache)
                            _cache.Set(keyword, result);

                        return result;
                    }

                    // Пустой ответ — логируем и ждём перед retry
                    if (attempt < _settings.MaxRetries)
                    {
                        Console.WriteLine($"    [XmlRiver] Пустой ответ для \"{Truncate(keyword, 40)}\" " +
                            $"(попытка {attempt}/{_settings.MaxRetries}), " +
                            $"повтор через {_settings.RetryDelayMs}мс...");
                        await Task.Delay(_settings.RetryDelayMs);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < _settings.MaxRetries)
                    {
                        Console.WriteLine($"    [XmlRiver] Ошибка для \"{Truncate(keyword, 40)}\" " +
                            $"(попытка {attempt}/{_settings.MaxRetries}): {ex.GetType().Name}, " +
                            $"повтор через {_settings.RetryDelayMs}мс...");
                        await Task.Delay(_settings.RetryDelayMs);
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    [XmlRiver] Ошибка запроса для \"{Truncate(keyword, 40)}\" " +
                            $"после {_settings.MaxRetries} попыток: {ex.GetType().Name}");
                        Console.ResetColor();
                    }
                }
            }

            // Даже пустой результат кэшируем (чтобы не переспрашивать)
            if (_cache != null && _settings.EnableCache)
                _cache.Set(keyword, result);

            return result;
        }

        /// <summary>
        /// Параллельный опрос нескольких ключей через SearchAsync.
        /// Использует SemaphoreSlim для ограничения конкурентности
        /// (MaxConcurrency из настроек XmlRiverSettings).
        /// </summary>
        public async Task<Dictionary<string, KeywordSearchResult>> SearchBatchAsync(
            List<string> keywords,
            int maxConcurrency,
            int topResultsCount = 5)
        {
            if (keywords == null || keywords.Count == 0)
                return new Dictionary<string, KeywordSearchResult>();

            var semaphore = new SemaphoreSlim(maxConcurrency);
            var results = new ConcurrentDictionary<string, KeywordSearchResult>();

            // Проверяем, сколько ключей уже в кэше
            int cachedCount = 0;
            if (_cache != null && _settings.EnableCache)
            {
                foreach (var key in keywords)
                {
                    if (_cache.ContainsKey(key))
                        cachedCount++;
                }
            }

            if (cachedCount == keywords.Count)
            {
                // Все ключи из кэша — достаточно одной строки
                Console.WriteLine($"    [SERP] Загружено {keywords.Count} записей из кэша.");
            }
            else
            {
                int apiCount = keywords.Count - cachedCount;
                Console.WriteLine($"    [SERP] Кэш: {cachedCount}, API: {apiCount} запросов ({maxConcurrency} потоков)...");
            }

            await Parallel.ForEachAsync(keywords, async (key, ct) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var result = await SearchAsync(key);
                    results[key] = result;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            // Сохраняем кэш на диск после батча
            if (_cache != null && _settings.EnableCache)
                _cache.Save();

            return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Парсит XML-ответ от XmlRiver (формат Yandex XML),
        /// извлекает URL, домен, заголовок и сниппет.
        /// Если в XML есть <error> — логирует текст ошибки.
        /// </summary>
        private void ParseXmlResponse(string xml, KeywordSearchResult result)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;

            try
            {
                var doc = XDocument.Parse(xml);

                // Проверяем наличие <error> в XML (Yandex XML возвращает ошибки
                // с HTTP 200, но с <error code="..."> внутри)
                var errorElement = doc.Descendants("error").FirstOrDefault();
                if (errorElement != null)
                {
                    string errorCode = errorElement.Attribute("code")?.Value ?? "?";
                    string errorText = errorElement.Value.Trim();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [XmlRiver] Yandex XML error (code {errorCode}): \"{errorText}\"");
                    Console.ResetColor();
                    return; // ничего не парсим
                }

                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                // Ищем все <doc> внутри <group>
                var docs = doc.Descendants(ns + "doc")
                    .Take(_settings.TopResultsCount)
                    .ToList();

                foreach (var docElement in docs)
                {
                    var url = docElement.Element(ns + "url")?.Value?.Trim() ?? "";
                    var domain = docElement.Element(ns + "domain")?.Value?.Trim() ?? "";
                    var title = docElement.Element(ns + "title")?.Value?.Trim() ?? "";
                    var headline = docElement.Element(ns + "headline")?.Value?.Trim() ?? "";

                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    result.Urls.Add(url);

                    result.Results.Add(new SearchResultItem
                    {
                        Url = url,
                        Domain = domain,
                        Title = title,
                        Snippet = headline
                    });
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    [XmlRiver] Ошибка парсинга XML: {ex.GetType().Name}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Обрезает строку до указанной длины (для красивого логирования).
        /// </summary>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value[..maxLength] + "...";
        }
    }
}
