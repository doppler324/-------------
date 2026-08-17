using System;
using System.IO;

namespace KeywordClusterizer.Models
{
    /// <summary>
    /// Настройки для режима чистки ключевых запросов (Keyword Cleaner).
    /// Загружаются из секции "cleaner" в settings.json.
    /// </summary>
    public class CleanerSettings
    {
        /// <summary>Модель нейросети по умолчанию.</summary>
        public string DefaultModel { get; set; } = "deepseek-v4-pro";

        /// <summary>Максимальное количество ключевых слов в одном пуле (батче). По умолчанию 100.</summary>
        public int DefaultPoolSize { get; set; } = 100;

        /// <summary>Файл для записи чистых (релевантных) ключей.</summary>
        public string OutputCleaned { get; set; } = "cleaned.txt";

        /// <summary>Файл для записи отброшенных ключей.</summary>
        public string OutputDiscarded { get; set; } = "discarded.txt";

        /// <summary>Файл для записи брендовых запросов.</summary>
        public string OutputBranded { get; set; } = "branded.txt";

        /// <summary>Файл для записи необработанных ключей (провалившиеся пулы + missed).</summary>
        public string OutputFailed { get; set; } = "failed.txt";

        /// <summary>Путь к файлу инструкции для информационных запросов.</summary>
        public string InstructionsInformational { get; set; } = "instructions/cleaner_informational.txt";

        /// <summary>Путь к файлу инструкции для коммерческих запросов.</summary>
        public string InstructionsCommercial { get; set; } = "instructions/cleaner_commercial.txt";

        /// <summary>Путь к файлу инструкции по брендовым запросам.</summary>
        public string InstructionsBranded { get; set; } = "instructions/cleaner_branded.txt";

        /// <summary>
        /// Загружает промпт из файла инструкции в зависимости от типа запроса.
        /// </summary>
        /// <param name="queryType">Тип запроса (Informational / Commercial).</param>
        /// <returns>Содержимое файла инструкции или null, если файл не найден.</returns>
        public string? LoadPrompt(QueryType queryType)
        {
            var path = queryType == QueryType.Informational
                ? InstructionsInformational
                : InstructionsCommercial;

            if (!File.Exists(path))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ОШИБКА] Файл инструкции не найден: {path}");
                Console.ResetColor();
                return null;
            }

            return File.ReadAllText(path);
        }

        /// <summary>
        /// Загружает инструкцию по брендовым запросам и подставляет цель (branded/discarded).
        /// Возвращает null, если файл не найден или brandHandling == KeepAsIs.
        /// </summary>
        public string? LoadBrandInstruction(BrandHandling brandHandling)
        {
            if (brandHandling == BrandHandling.KeepAsIs)
                return null;

            if (!File.Exists(InstructionsBranded))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ОШИБКА] Файл инструкции не найден: {InstructionsBranded}");
                Console.ResetColor();
                return null;
            }

            string template = File.ReadAllText(InstructionsBranded);
            string target = brandHandling == BrandHandling.SeparateFile
                ? "Помещай их в отдельный список \"branded\"."
                : "Помещай их в список \"discarded\".";

            return template.Replace("{TARGET}", target);
        }
    }
}
