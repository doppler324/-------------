using System;
using System.Collections.Generic;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Чекпойнт завершённой фазы пайплайна (Phase 4 / 4.5 / 5).
    /// Содержит имя фазы, время сохранения и полный результат (кластеры + метаданные FAQ).
    /// Позволяет продолжить работу с последнего завершённого этапа при обрыве программы.
    /// </summary>
    public class CheckpointData
    {
        /// <summary>Имя завершённой фазы: "phase4" | "phase4_5" | "phase5".</summary>
        public string Phase { get; set; } = "";

        /// <summary>Время сохранения чекпойнта.</summary>
        public DateTime SavedAt { get; set; } = DateTime.Now;

        /// <summary>Кластеры: имя → ключи (результат фазы).</summary>
        public Dictionary<string, List<string>> Clusters { get; set; } = new();

        /// <summary>Метаданные кластеров (FAQ-пометки и привязки из Phase 5).</summary>
        public Dictionary<string, ClusterMeta> Meta { get; set; } = new();
    }
}
