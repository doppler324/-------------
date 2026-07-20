using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI после чистки ключевых запросов.
    /// Содержит три списка: релевантные (cleaned), брендовые (branded) и отброшенные (discarded).
    /// </summary>
    public class CleanerResponse
    {
        [JsonPropertyName("cleaned")]
        public List<string> Cleaned { get; set; } = new();

        [JsonPropertyName("branded")]
        public List<string> Branded { get; set; } = new();

        [JsonPropertyName("discarded")]
        public List<string> Discarded { get; set; } = new();
    }
}
