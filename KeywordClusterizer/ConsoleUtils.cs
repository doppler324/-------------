using System;

namespace KeywordClusterizer
{
    /// <summary>
    /// Хелпер для цветного вывода в консоль.
    /// Избавляет от повторения тройки ForegroundColor/Write.../ResetColor по всему коду.
    /// </summary>
    internal static class ConsoleUtils
    {
        /// <summary>Выводит строку с переводом строки, опционально — заданным цветом.</summary>
        public static void WriteLine(string text, ConsoleColor? color = null)
        {
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.WriteLine(text);
            if (color.HasValue) Console.ResetColor();
        }

        /// <summary>Выводит строку без перевода строки, опционально — заданным цветом.</summary>
        public static void Write(string text, ConsoleColor? color = null)
        {
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.Write(text);
            if (color.HasValue) Console.ResetColor();
        }
    }
}
