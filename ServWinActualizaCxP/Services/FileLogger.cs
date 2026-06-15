using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinActualizaCxP.Services
{
    public static class FileLogger
    {
        private static readonly object _lock = new();

        public static void Log(string filePath, string message)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                    File.AppendAllText(filePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
