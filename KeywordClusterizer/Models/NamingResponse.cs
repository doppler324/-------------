using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI на наименование одного кластера.
    /// Формат JSON: { "name": "Новый H1-заголовок" }.
    /// </summary>
    public class NamingResponse
    {
        /// <summary>Новый H1-заголовок для кластера (без ключевых слов, только название).</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }
}
