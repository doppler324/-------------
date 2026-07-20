namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Тип отбора ключевых запросов.
    /// Informational — оставляем информационные запросы (как, что, почему).
    /// Commercial — оставляем коммерческие запросы (купить, цена, заказать).
    /// </summary>
    public enum QueryType
    {
        Informational,
        Commercial
    }

    /// <summary>
    /// Куда отправлять брендовые запросы.
    /// </summary>
    public enum BrandHandling
    {
        /// <summary>В отдельный файл branded.txt.</summary>
        SeparateFile,
        /// <summary>В discarded.txt как мусор.</summary>
        ToDiscarded,
        /// <summary>Оставить в cleaned.txt.</summary>
        KeepAsIs
    }
}
