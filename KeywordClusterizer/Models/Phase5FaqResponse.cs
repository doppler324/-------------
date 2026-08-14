using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI на Phase 5: для каждого кластера определяется, подходит ли он
    /// для отдельной статьи или как FAQ-блок.
    /// Формат JSON: { "clusters": [ { "name": "Точное название", "type": "article" }, ... ] }.
    /// type: "article" | "faq". AI обязана классифицировать КАЖДЫЙ переданный кластер.
    /// </summary>
    public class Phase5FaqResponse
    {
        /// <summary>Список решений по каждому кластеру (имя → тип).</summary>
        [JsonPropertyName("clusters")]
        public List<Phase5FaqClusterDecision> Clusters { get; set; } = new();
    }

    /// <summary>
    /// Решение AI по одному кластеру: точное название + тип ("article" или "faq").
    /// </summary>
    public class Phase5FaqClusterDecision
    {
        /// <summary>Название кластера ТОЧНО как во входных данных.</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        /// <summary>
        /// Тип кластера: "article" (отдельная статья) или "faq" (блок FAQ).
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "article";
    }
}
