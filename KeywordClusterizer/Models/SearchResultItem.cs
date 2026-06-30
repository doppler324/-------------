namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Один результат поисковой выдачи (SERP).
    /// </summary>
    public class SearchResultItem
    {
        /// <summary>URL страницы в выдаче.</summary>
        public string Url { get; set; } = "";

        /// <summary>Домен.</summary>
        public string Domain { get; set; } = "";

        /// <summary>Заголовок результата.</summary>
        public string Title { get; set; } = "";

        /// <summary>Сниппет (описание).</summary>
        public string Snippet { get; set; } = "";
    }

    /// <summary>
    /// Результат поиска по одному ключевому слову (ключ -> список URL).
    /// </summary>
    public class KeywordSearchResult
    {
        /// <summary>Поисковый запрос.</summary>
        public string Keyword { get; set; } = "";

        /// <summary>Список URL из топа выдачи.</summary>
        public List<string> Urls { get; set; } = new();

        /// <summary>Полные результаты (с заголовками/сниппетами).</summary>
        public List<SearchResultItem> Results { get; set; } = new();
    }
}
