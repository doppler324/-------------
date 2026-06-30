using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Модель ответа DeepSeek — JSON структура, которую требует system_prompt.txt.
    /// </summary>
    public class DeepSeekResponse
    {
        /// <summary>Массив кластеров.</summary>
        [JsonPropertyName("clusters")]
        public List<ClusterItem> Clusters { get; set; } = new();

        /// <summary>Массив нераспределённых ключей.</summary>
        [JsonPropertyName("unclustered")]
        public List<string> Unclustered { get; set; } = new();
    }

    /// <summary>
    /// Один кластер в ответе нейросети.
    /// </summary>
    public class ClusterItem
    {
        /// <summary>Название кластера (тема группы).</summary>
        [JsonPropertyName("cluster_name")]
        public string ClusterName { get; set; } = "";

        /// <summary>Интент: commercial / informational / mixed.</summary>
        [JsonPropertyName("intent")]
        public string Intent { get; set; } = "";

        /// <summary>Тип страницы: категория / карточка товара / статья в блог / услуга.</summary>
        [JsonPropertyName("page_type")]
        public string PageType { get; set; } = "";

        /// <summary>Ключевые слова в этом кластере.</summary>
        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();
    }
}
