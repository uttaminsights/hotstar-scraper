namespace HotstarScraper.Services
{
    internal static class Log
    {
        private static readonly object Gate = new();

        public static void Info(string message) => Write(Console.Out, "", message);

        public static void Warn(string message) => Write(Console.Out, "WARN ", message);

        public static void Error(string message) => Write(Console.Error, "ERROR ", message);

        private static void Write(TextWriter writer, string level, string message)
        {
            lock (Gate) writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {level}{message}");
        }
    }
}
