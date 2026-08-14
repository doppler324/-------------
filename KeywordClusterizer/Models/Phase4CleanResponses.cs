using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Ответ AI на Шаг 1 чистки кластера: какие запросы не подходят кластеру.
    /// Формат JSON: { "remove": ["запрос 1", "запрос 2"] }.
    /// remove — точные строки запросов (не номера), которые надо вынести.
    /// Пустой/отсутствующий remove означает, что кластер чист.
    /// </summary>
    public class Phase4CleanRemoveResponse
    {
        /// <summary>Список запросов (точные строки), которые не подходят кластеру.</summary>
        [JsonPropertyName("remove")]
        public List<string> Remove { get; set; } = new();
    }

    /// <summary>
    /// Одно назначение запроса на Шаге 2: куда его добавить.
    /// </summary>
    public class Phase4CleanAssignment
    {
        /// <summary>Точная строка запроса.</summary>
        [JsonPropertyName("keyword")]
        public string Keyword { get; set; } = "";

        /// <summary>
        /// Имя существующего кластера-получателя или специальное значение «Нераспределённые».
        /// </summary>
        [JsonPropertyName("cluster")]
        public string Cluster { get; set; } = "";
    }

    /// <summary>
    /// Ответ AI на Шаг 2 чистки кластера: распределение вынесенных запросов по кластерам.
    /// Формат JSON: { "assignments": [ { "keyword": "...", "cluster": "..." } ] }.
    /// </summary>
    public class Phase4CleanAssignResponse
    {
        /// <summary>Список назначений вынесенных запросов по кластерам.</summary>
        [JsonPropertyName("assignments")]
        public List<Phase4CleanAssignment> Assignments { get; set; } = new();
    }
}
