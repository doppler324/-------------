using System.Collections.Generic;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Метаданные одного кластера после Phase 5 (отбор FAQ-кластеров).
    /// Кластеры, которых нет в Meta (или IsFaq=false) — обычные статьи.
    /// FAQ-кластеры остаются в Clusters с тем же составом ключей (не удаляются, не меняются).
    /// </summary>
    public class ClusterMeta
    {
        /// <summary>true — кластер отобран AI как FAQ-блок (не тянет на отдельную статью).</summary>
        public bool IsFaq { get; set; } = false;

        /// <summary>Имя статьи (другого кластера), к которой привязан FAQ по смыслу. null — без привязки.</summary>
        public string? LinkedArticle { get; set; }

        /// <summary>Cosine similarity при привязке (для отображения в консоли).</summary>
        public double LinkSimilarity { get; set; } = 0.0;
    }

    /// <summary>
    /// Полный результат пайплайна кластеризации (после Phase 5).
    /// Clusters — сами кластеры (имя → ключи, как раньше),
    /// Meta — метаданные FAQ для кластеров (IsFaq, LinkedArticle).
    /// </summary>
    public class ClusteringResult
    {
        /// <summary>Кластеры: имя → список ключей.</summary>
        public Dictionary<string, List<string>> Clusters { get; set; } = new();

        /// <summary>Метаданные кластеров: имя → ClusterMeta (FAQ-пометки и привязки).</summary>
        public Dictionary<string, ClusterMeta> Meta { get; set; } = new();
    }
}
