using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI после Phase 4: единый DeepSeek call для всех кластеров.
    /// AI сам решает: какие кластеры склеить, какие общие запросы извлечь в обзорную статью.
    /// </summary>
    public class SeoArticleResponse
    {
        [JsonPropertyName("seo_articles")]
        public List<SeoArticle> SeoArticles { get; set; } = new();
    }

    /// <summary>
    /// Одна SEO-статья в результате финальной кластеризации.
    /// </summary>
    public class SeoArticle
    {
        /// <summary>Заголовок H1 для будущей статьи.</summary>
        [JsonPropertyName("h1_title")]
        public string H1Title { get; set; } = "";

        /// <summary>
        /// Откуда взяты ключи:
        /// - "Кластер N" — кластер целиком
        /// - "Извлечено из Кластер N" — широкий запрос, вытащенный из узкого кластера
        /// </summary>
        [JsonPropertyName("source_clusters")]
        public List<string> SourceClusters { get; set; } = new();

        /// <summary>Ключевые слова для статьи (оригинальные, без изменений).</summary>
        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();
    }

    /// <summary>
    /// Формат ответа Gemini: [{ "name": "Название", "keywords": [ключи] }]
    /// </summary>
    public class GeminiArticle
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();
    }
}
