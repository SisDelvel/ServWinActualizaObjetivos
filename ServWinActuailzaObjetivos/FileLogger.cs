using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WindowsServiceTestGeisha
{
    public static class FileLogger
    {
        private static readonly object _lock = new object();

        public static void Log(string filePath, string message)
        {
            try
            {
                // 'lock' asegura que solo un hilo pueda escribir en el archivo a la vez, evitando conflictos.
                lock (_lock)
                {
                    // Asegura que el directorio exista.
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                    // Agrega la fecha y el mensaje al final del archivo.
                    File.AppendAllText(filePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // En un servicio real, podrías registrar este error en el Visor de Eventos de Windows.
            }
        }
    }
}
