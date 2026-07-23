using System.Collections.Generic;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Публичный DTO для макро-бакета (результат Phase 3.5 Macro Merge).
    /// Содержит имя (медоид ядра), список ключей и representative-вектор.
    /// </summary>
    public class MacroBucket
    {
        /// <summary>Имя бакета = медоид ядра (реальная фраза из самого крупного микро-кластера).</summary>
        public string Name { get; set; } = "";

        /// <summary>Ключевые слова бакета.</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Representative-вектор (медоид или L2-нормализованный центроид).</summary>
        public float[] RepresentativeVector { get; set; } = System.Array.Empty<float>();

        /// <summary>Размер бакета (количество ключей).</summary>
        public int Size => Keywords?.Count ?? 0;
    }
}
