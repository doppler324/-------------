using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KeywordClusterizer.Models;

namespace KeywordClusterizer.Services
{
    /// <summary>
    /// Хранение чекпойнтов завершённых фаз пайплайна (Phase 4 / 4.5 / 5).
    /// Каждая фаза сохраняется в отдельный JSON-файл рядом с exe (рабочая папка):
    ///   phase4.json   — результат после Phase 4 (AI Merge + Naming)
    ///   phase4_5.json — результат после Phase 4.5 (AI-чистка кластеров)
    ///   phase5.json   — финальный результат (после Phase 5, FAQ-отбор)
    /// Позволяет продолжить работу с последнего завершённого этапа при обрыве программы.
    /// </summary>
    public static class CheckpointStore
    {
        /// <summary>Порядок фаз по «глубине» (чем больше индекс, тем более поздняя фаза).</summary>
        private static readonly string[] PhaseOrder = { "phase4", "phase4_5", "phase5" };

        /// <summary>Рабочая папка, куда пишутся чекпойнты (там же, где settings.json/exe).</summary>
        public static string Dir { get; set; } = ".";

        /// <summary>Возвращает имя файла чекпойнта для фазы.</summary>
        private static string FilePath(string phase) => Path.Combine(Dir, $"{phase}.json");

        /// <summary>
        /// Сохраняет чекпойнт фазы (атомарно: пишет во временный файл, затем переименовывает).
        /// </summary>
        public static void Save(CheckpointData data)
        {
            try
            {
                if (data == null || string.IsNullOrWhiteSpace(data.Phase))
                    return;

                string path = FilePath(data.Phase);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(data, options);

                // Атомарная запись: временный файл + переименование
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);

                ConsoleUtils.WriteLine(
                    $"[Checkpoint] Сохранён чекпойнт {data.Phase}.json ({data.Clusters?.Count ?? 0} кластеров).",
                    ConsoleColor.DarkGray);
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine(
                    $"[Checkpoint] Ошибка сохранения чекпойнта {data?.Phase}: {ex.Message}",
                    ConsoleColor.Yellow);
            }
        }

        /// <summary>
        /// Загружает чекпойнт фазы. Возвращает null, если файла нет или он повреждён.
        /// </summary>
        public static CheckpointData? Load(string phase)
        {
            if (string.IsNullOrWhiteSpace(phase))
                return null;

            string path = FilePath(phase);
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<CheckpointData>(json);
                return data;
            }
            catch (Exception ex)
            {
                ConsoleUtils.WriteLine(
                    $"[Checkpoint] Ошибка загрузки {phase}.json: {ex.Message}",
                    ConsoleColor.Yellow);
                return null;
            }
        }

        /// <summary>
        /// Определяет самую свежую (самую позднюю по порядку фаз) сохранённую фазу.
        /// Возвращает имя фазы ("phase4"/"phase4_5"/"phase5") или null, если чекпойнтов нет.
        /// </summary>
        public static string? FindLatestPhase()
        {
            // Идём от самой поздней фазы к ранней — первая найденная и есть самая свежая
            for (int i = PhaseOrder.Length - 1; i >= 0; i--)
            {
                if (File.Exists(FilePath(PhaseOrder[i])))
                    return PhaseOrder[i];
            }
            return null;
        }

        /// <summary>
        /// Возвращает индекс фазы в порядке (0=phase4, 1=phase4_5, 2=phase5). -1 — неизвестная фаза.
        /// </summary>
        public static int PhaseIndex(string phase)
        {
            for (int i = 0; i < PhaseOrder.Length; i++)
                if (string.Equals(PhaseOrder[i], phase, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>
        /// Проверяет, существует ли чекпойнт фазы.
        /// </summary>
        public static bool Exists(string phase) => File.Exists(FilePath(phase));

        /// <summary>
        /// Удаляет все чекпойнты (для полного перезапуска с нуля).
        /// </summary>
        public static void Clear()
        {
            foreach (var phase in PhaseOrder)
            {
                try
                {
                    string path = FilePath(phase);
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    ConsoleUtils.WriteLine($"[Checkpoint] Не удалось удалить {phase}.json: {ex.Message}", ConsoleColor.Yellow);
                }
            }
            ConsoleUtils.WriteLine("[Checkpoint] Чекпойнты удалены.", ConsoleColor.DarkGray);
        }
    }
}
