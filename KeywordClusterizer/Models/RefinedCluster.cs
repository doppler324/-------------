using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI после именования и чистки одного SERP-кластера.
    /// Используется в Фазе 3 SERP-first пайплайна.
    /// </summary>
    public class RefinedCluster
    {
        /// <summary>Название кластера (будущий H1).</summary>
        [JsonPropertyName("cluster_name")]
        public string ClusterName { get; set; } = "";

        /// <summary>Тип страницы: категория / карточка товара / статья / услуга.</summary>
        [JsonPropertyName("page_type")]
        public string PageType { get; set; } = "";

        /// <summary>Ключевые слова в этом кластере.</summary>
        [JsonPropertyName("keywords")]
        public List<string> Keywords { get; set; } = new();

        /// <summary>Ключи, не подходящие по смыслу.</summary>
        [JsonPropertyName("unclustered")]
        public List<string> Unclustered { get; set; } = new();
    }
}
