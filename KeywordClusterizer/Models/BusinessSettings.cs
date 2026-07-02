using System.Text.RegularExpressions;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Бизнес-настройки кластеризации: ниша, логика, гранулярность, размер чанка.
    /// Служат «якорем», который передаётся в каждый запрос к нейросети.
    /// </summary>
    public class BusinessSettings
    {
        /// <summary>Ниша сайта (например, "спортивная обувь").</summary>
        public string Niche { get; set; } = "";

        /// <summary>Логика кластеризации (например, "по интенту пользователя").</summary>
        public string ClusteringLogic { get; set; } = "";

        /// <summary>Правило гранулярности (например, "кластеры от 2 до 10 ключей").</summary>
        public string GranularityRule { get; set; } = "";

        /// <summary>
        /// Собирает базовые правила в строку для подстановки в системный промпт.
        /// </summary>
        public string ToBaseRules() =>
            $"Ниша: {Niche}\nЛогика: {ClusteringLogic}\nРазмер: {GranularityRule}";

        /// <summary>
        /// Парсит максимальное количество ключей в кластере из строки GranularityRule.
        /// Ищет число после "до" (например, "от 1 до 3 ключей" → 3).
        /// Если не удалось распарсить — возвращает значение по умолчанию 10.
        /// </summary>
        public int ParseMaxClusterSize()
        {
            if (string.IsNullOrWhiteSpace(GranularityRule))
                return 10;

            // Ищем число после "до" (регистронезависимо)
            var match = Regex.Match(GranularityRule, @"до\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int result))
                return result;

            // Если "до" не нашли — ищем последнее число в строке
            var numbers = Regex.Matches(GranularityRule, @"\d+");
            if (numbers.Count > 0 && int.TryParse(numbers[^1].Value, out result))
                return result;

            return 10;
        }
    }
}
