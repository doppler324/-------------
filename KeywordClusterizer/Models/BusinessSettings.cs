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
        /// Режим слияния кластеров (Phase 4.5):
        /// "off" — пропустить,
        /// "ai" — DeepSeek Merge Pass,
        /// "centroid" — Tanimoto Coefficient центроидов (бесплатно, без API).
        /// </summary>
        public string MergeMode { get; set; } = "centroid";

        /// <summary>Порог Tanimoto Coefficient для centroid-режима (0.0-1.0). Рекомендуется 0.85 (эквивалент Cosine ~0.92).</summary>
        public float MergeThreshold { get; set; } = 0.85f;

        /// <summary>Если false — отключает Phase 3.5 (Centroid Merge).</summary>
        public bool CentroidMergeEnabled { get; set; } = true;

        /// <summary>
        /// Если true — пропускает AI-именование кластеров (Фаза 4).
        /// Вместо этого генерирует технические имена "Кластер 1", "Кластер 2", ...
        /// Полезно для быстрой отладки центроидного слияния без затрат на API.
        /// </summary>
        public bool SkipNaming { get; set; } = false;

        // ═══════════════════════════════════════════════════════
        // Word-Level Clustering (Phase 3)
        // SERP-first → внутри каждого SERP-кластера:
        // IDF weighting + Weighted Soft Jaccard + HAC
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Включает word-level кластеризацию внутри SERP-кластеров.
        /// Использует IDF-взвешенный Soft Jaccard с word embeddings + HAC.
        /// </summary>
        public bool WordLevelClusteringEnabled { get; set; } = true;

        /// <summary>
        /// Порог cosine similarity между word embeddings для засчитывания совпадения (0.0-1.0).
        /// Рекомендуется: 0.85 — ловит морфологию "поплавок"≈"поплавка".
        /// </summary>
        public float WordSimThreshold { get; set; } = 0.85f;

        /// <summary>
        /// Порог Weighted Jaccard для остановки HAC (0.0-1.0).
        /// Рекомендуется: 0.35.
        /// Ниже = мельче кластеры, выше = крупнее.
        /// </summary>
        public float HacThreshold { get; set; } = 0.35f;

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
