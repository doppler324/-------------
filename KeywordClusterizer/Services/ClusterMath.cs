using System;
using System.Collections.Generic;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Статические методы для работы с эмбеддингами:
    /// вычисление L2-нормализованного центроида кластера.
    /// </summary>
    public static class ClusterMath
    {
        /// <summary>
        /// Вычисляет L2-нормализованный центроид для списка эмбеддингов.
        /// </summary>
        /// <param name="vectors">Список векторов-эмбеддингов фраз одного кластера.</param>
        /// <returns>L2-нормализованный центроид (длина = 1.0).</returns>
        public static float[] CalculateNormalizedCentroid(List<float[]> vectors)
        {
            if (vectors == null || vectors.Count == 0)
                throw new ArgumentException("Кластер не содержит векторов для усреднения.");

            int dimensions = vectors[0].Length;
            float[] centroid = new float[dimensions];

            // Шаги 1 и 2: Суммирование и усреднение
            int count = vectors.Count;
            foreach (var vec in vectors)
            {
                for (int i = 0; i < dimensions; i++)
                {
                    centroid[i] += vec[i];
                }
            }

            for (int i = 0; i < dimensions; i++)
            {
                centroid[i] /= count;
            }

            // Шаг 3: Расчет L2-нормы (магнитуды)
            float sumOfSquares = 0f;
            for (int i = 0; i < dimensions; i++)
            {
                sumOfSquares += centroid[i] * centroid[i];
            }

            float magnitude = (float)Math.Sqrt(sumOfSquares);

            // Шаг 4: Нормализация (защита от деления на ноль)
            if (magnitude > 1e-8f)
            {
                for (int i = 0; i < dimensions; i++)
                {
                    centroid[i] /= magnitude;
                }
            }

            return centroid;
        }
    }
}
