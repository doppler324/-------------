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
        /// Если true — полностью пропускает Фазу 4 (включая проход по кластерам).
        /// Кластеры возвращаются как есть с тех-именами. Полезно для отладки Phase 3.
        /// </summary>
        public bool SkipPhase4 { get; set; } = false;

        /// <summary>
        /// Если true — пропускает AI-именование кластеров (Фаза 4).
        /// Вместо этого генерирует технические имена "Кластер 1", "Кластер 2", ...
        /// Полезно для быстрой отладки центроидного слияния без затрат на API.
        /// </summary>
        public bool SkipNaming { get; set; } = false;

        /// <summary>
        /// Если true — пропускает AI-перегруппировку (merge), но оставляет AI-именование (naming).
        /// Кластеры сохраняют свой состав, AI только придумывает H1-заголовки.
        /// Работает только если SkipPhase4=false и SkipNaming=false.
        /// </summary>
        public bool SkipMerge { get; set; } = false;

        /// <summary>
        /// Если true — не выводит подробный список кластеров в консоль после завершения.
        /// Полезно для больших наборов ключей (1500+), где вывод занимает много времени.
        /// </summary>
        public bool SuppressClusterDisplay { get; set; } = false;

        // ═══════════════════════════════════════════════════════
        // Macro Merge (Phase 3.5)
        // Greedy Merge микро-кластеров в макро-бакеты.
        // representativeMode: "medoid" (реальная фраза) или "centroid" (L2-нормализованный центр).
        // Порог = sentenceHacThreshold - 0.05 (рекомендуется 0.77 при HAC=0.82).
        // ═══════════════════════════════════════════════════════

        /// <summary>Включает Phase 3.5 (Macro Merge).</summary>
        public bool MacroMergeEnabled { get; set; } = true;

        /// <summary>
        /// Режим representative-вектора для слияния:
        /// "medoid" — реальная фраза (медоид кластера),
        /// "centroid" — L2-нормализованный центроид всех фраз кластера.
        /// </summary>
        public string RepresentativeMode { get; set; } = "centroid";

        /// <summary>
        /// Порог cosine similarity для Greedy Merge (0.0-1.0).
        /// Рекомендуется: sentenceHacThreshold - 0.05.
        /// </summary>
        public float MacroMergeThreshold { get; set; } = 0.77f;

        // ═══════════════════════════════════════════════════════
        // Rescue Pass V2 (Phase 3.6)
        // Nearest Centroid Assignment для сирот (одиночки + unclustered).
        // Порог должен быть мягче, чем macroMergeThreshold (рекомендуется 0.78).
        // ═══════════════════════════════════════════════════════

        /// <summary>Включает Rescue Pass V2 (Phase 3.6).</summary>
        public bool RescuePassV2Enabled { get; set; } = true;

        /// <summary>
        /// Порог cosine similarity для прикрепления сироты к ядру (0.0-1.0).
        /// Рекомендуется: macroMergeThreshold - 0.05 (например, 0.78 при macroMergeThreshold=0.83).
        /// </summary>
        public float RescueThreshold { get; set; } = 0.78f;

        // ═══════════════════════════════════════════════════════
        // Sentence-Level Clustering (Phase 3)
        // SERP-first → внутри каждого SERP-кластера:
        // sentence embeddings (text-embedding-3-small) + cosine similarity + HAC
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Включает sentence-level кластеризацию внутри SERP-кластеров.
        /// Использует cosine similarity между sentence embeddings + HAC.
        /// </summary>
        public bool SentenceLevelClusteringEnabled { get; set; } = true;

        /// <summary>
        /// Порог cosine similarity для остановки HAC (0.0-1.0).
        /// Рекомендуется: 0.82 для text-embedding-3-small.
        /// Выше = мельче кластеры, ниже = крупнее.
        /// </summary>
        public float SentenceHacThreshold { get; set; } = 0.82f;

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
