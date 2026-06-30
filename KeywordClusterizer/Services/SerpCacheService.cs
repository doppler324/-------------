using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Сервис кэширования SERP-результатов в JSON-файл.
    /// Позволяет не тратить API-лимиты XmlRiver при повторных запусках.
    /// Формат: Dictionary<string, KeywordSearchResult>, где ключ = поисковый запрос.
    /// </summary>
    public class SerpCacheService
    {
        private readonly string _cachePath;
        private Dictionary<string, KeywordSearchResult>? _cache;

        // Блокировка для потокобезопасного доступа к файлу
        private readonly object _lock = new();

        /// <summary>
        /// Количество записей в кэше (после загрузки).
        /// </summary>
        public int Count => _cache?.Count ?? 0;

        public SerpCacheService(string cachePath)
        {
            _cachePath = cachePath;
        }

        /// <summary>
        /// Загружает кэш из JSON-файла (при первом вызове).
        /// </summary>
        public void Load()
        {
            if (_cache != null)
                return;

            lock (_lock)
            {
                if (_cache != null)
                    return;

                if (!File.Exists(_cachePath))
                {
                    Console.WriteLine($"    [Cache] Кэш '{_cachePath}' не найден. Будет создан при первом сохранении.");
                    _cache = new Dictionary<string, KeywordSearchResult>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                try
                {
                    string json = File.ReadAllText(_cachePath);
                    _cache = JsonSerializer.Deserialize<Dictionary<string, KeywordSearchResult>>(json)
                             ?? new Dictionary<string, KeywordSearchResult>(StringComparer.OrdinalIgnoreCase);

                    Console.WriteLine($"    [Cache] Загружено {_cache.Count} записей из '{_cachePath}'.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [Cache] Ошибка загрузки кэша: {ex.Message}. Начинаем с пустого.");
                    Console.ResetColor();
                    _cache = new Dictionary<string, KeywordSearchResult>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>
        /// Сохраняет кэш в JSON-файл (перезаписывает полностью).
        /// </summary>
        public void Save()
        {
            if (_cache == null)
                return;

            lock (_lock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_cachePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var options = new JsonSerializerOptions { WriteIndented = false };
                    string json = JsonSerializer.Serialize(_cache, options);
                    File.WriteAllText(_cachePath, json);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"    [Cache] Ошибка сохранения кэша: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        /// <summary>
        /// Пытается получить SERP-результат из кэша.
        /// </summary>
        public bool TryGet(string keyword, out KeywordSearchResult? result)
        {
            Load();

            lock (_lock)
            {
                if (_cache != null && _cache.TryGetValue(keyword, out var cached))
                {
                    // Проверяем, что в кэше есть хотя бы один URL (результат не пустой)
                    if (cached.Urls.Count > 0 || cached.Results.Count > 0)
                    {
                        result = cached;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }

        /// <summary>
        /// Сохраняет SERP-результат в кэш (в памяти).
        /// Вызов Save() для записи на диск.
        /// </summary>
        public void Set(string keyword, KeywordSearchResult result)
        {
            Load();

            lock (_lock)
            {
                if (_cache != null)
                {
                    _cache[keyword] = result;
                }
            }
        }

        /// <summary>
        /// Сохраняет несколько результатов одной операцией.
        /// </summary>
        public void SetBatch(Dictionary<string, KeywordSearchResult> results)
        {
            Load();

            lock (_lock)
            {
                if (_cache != null)
                {
                    foreach (var kvp in results)
                        _cache[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>
        /// Проверяет, есть ли ключ в кэше (даже с пустым результатом).
        /// </summary>
        public bool ContainsKey(string keyword)
        {
            Load();

            lock (_lock)
            {
                return _cache?.ContainsKey(keyword) ?? false;
            }
        }
    }
}
