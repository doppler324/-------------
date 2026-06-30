using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using KeywordClusterizer.Models;

namespace KeywordClusterizer
{
    /// <summary>
    /// HTTP-клиент для запросов к XmlRiver API (Yandex XML).
    /// Отправляет поисковый запрос, парсит XML-ответ, возвращает топ URL.
    /// </summary>
    public class XmlRiverClient
    {
        private readonly HttpClient _client;
        private readonly XmlRiverSettings _settings;

        public XmlRiverClient(HttpClient client, XmlRiverSettings settings)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Выполняет поиск по одному ключевому слову через XmlRiver.
        /// При пустом ответе делает до MaxRetries повторов с задержкой RetryDelayMs.
        /// Возвращает топ-N URL из выдачи Yandex.
        /// </summary>
        public async Task<KeywordSearchResult> SearchAsync(string keyword)
        {
            var result = new KeywordSearchResult { Keyword = keyword };

            for (int attempt = 1; attempt <= _settings.MaxRetries; attempt++)
            {
                try
                {
                    string encodedQuery = Uri.EscapeDataString(keyword);
                    string url = $"https://xmlriver.com/search/xml?user={_settings.XmlriverUser}" +
                                 $"&key={_settings.XmlriverKey}" +
                                 $"&query={encodedQuery}";

                    using var response = await _client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    string xmlContent = await response.Content.ReadAsStringAsync();
                    ParseXmlResponse(xmlContent, result);

                    // Если есть URL — успех, выходим
                    if (result.Urls.Count > 0)
                        return result;

                    // Пустой ответ — логируем и ждём перед retry
                    if (attempt < _settings.MaxRetries)
                    {
                        Console.WriteLine($"    [XmlRiver] Пустой ответ для \"{keyword}\" " +
                            $"(попытка {attempt}/{_settings.MaxRetries}), " +
                            $"повтор через {_settings.RetryDelayMs}мс...");
                        await Task.Delay(_settings.RetryDelayMs);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < _settings.MaxRetries)
                    {
                        Console.WriteLine($"    [XmlRiver] Ошибка для \"{keyword}\" " +
                            $"(попытка {attempt}/{_settings.MaxRetries}): {ex.GetType().Name}, " +
                            $"повтор через {_settings.RetryDelayMs}мс...");
                        await Task.Delay(_settings.RetryDelayMs);
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"    [XmlRiver] Ошибка запроса для \"{keyword}\" " +
                            $"после {_settings.MaxRetries} попыток: {ex.GetType().Name}");
                        Console.ResetColor();
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Выполняет параллельные поисковые запросы к XmlRiver для списка ключей.
        /// Использует SemaphoreSlim для ограничения количества одновременных запросов
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

            Console.WriteLine($"    [XmlRiver] Параллельный опрос {keywords.Count} ключей " +
                $"({maxConcurrency} потоков)...");

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

            Console.WriteLine($"    [XmlRiver] Готово: {results.Count} ключей опрошено.");
            return results.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Парсит XML-ответ от XmlRiver (формат Yandex XML),
        /// извлекает URL, домен, заголовок и сниппет.
        /// </summary>
        private void ParseXmlResponse(string xml, KeywordSearchResult result)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;

            try
            {
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

                // Ищем все <doc> внутри <group>
                var docs = doc.Descendants(ns + "doc")
                    .Take(_settings.TopResultsCount)
                    .ToList();

                foreach (var docElement in docs)
                {
                    var item = new SearchResultItem
                    {
                        Url = docElement.Element(ns + "url")?.Value?.Trim() ?? "",
                        Domain = docElement.Element(ns + "domain")?.Value?.Trim() ?? "",
                        Title = docElement.Element(ns + "title")?.Value?.Trim() ?? "",
                        Snippet = docElement.Element(ns + "headline")?.Value?.Trim() ?? ""
                    };

                    // Пропускаем пустые URL
                    if (string.IsNullOrWhiteSpace(item.Url))
                        continue;

                    result.Urls.Add(item.Url);
                    result.Results.Add(item);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    [XmlRiver] Ошибка парсинга XML для \"{result.Keyword}\": {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}
