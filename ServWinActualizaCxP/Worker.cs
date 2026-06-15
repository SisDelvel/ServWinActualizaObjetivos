using ServWinActualizaCxP.Services;

namespace ServWinActualizaCxP
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly RepositorioCxP _repositorio;

        private const int INTERVALO_MINUTOS = 5;

        private readonly string _logFile = Path.Combine(
            @"C:\Logs\GeishaCxP",
            $"ActualizaCxP_{DateTime.Now:yyyy_MM_dd}.txt");

        private readonly Dictionary<string, string> _conexionesIntelisis = new()
    {
        { "Toque", "Data Source=192.168.89.3;Initial Catalog=Toque;Persist Security Info=False;Pwd=9GPgaPD#sce^HhP;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90" },
        //{ "Kique", "Data Source=Kique.geisha.mx;Initial Catalog=Kique;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True" },
        //{ "Nicui", "Data Source=192.168.89.1;Initial Catalog=Nicui;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90" },
        //{ "Reque", "Data Source=Reque.geisha.mx;Initial Catalog=reque;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;Encrypt=False" },
        //{ "Nivil", "Data Source=nivil.geisha.mx;Initial Catalog=Nivil;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;Encrypt=False" }
    };

        public Worker(ILogger<Worker> logger, RepositorioCxP repositorio)
        {
            _logger = logger;
            _repositorio = repositorio;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            FileLogger.Log(_logFile, "Inicia Servicio de Sincronización de Cuentas por Pagar");
            LimpiarLogsAntiguos();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcesarCicloAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"EXCEPCIÓN GENERAL: {ex.Message}\nStackTrace: {ex.StackTrace}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(INTERVALO_MINUTOS), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            FileLogger.Log(_logFile, "Servicio detenido");
        }

        private async Task ProcesarCicloAsync(CancellationToken token)
        {
            FileLogger.Log(_logFile, "Iniciando ciclo de sincronización CxP");

            foreach (var entry in _conexionesIntelisis)
            {
                if (token.IsCancellationRequested) break;

                string nombre = entry.Key;

                try
                {
                    FileLogger.Log(_logFile, $"Procesando conexión: {nombre}");

                    int procesados = await _repositorio.SincronizarCxPAsync(nombre, _logFile, token);

                    FileLogger.Log(_logFile, procesados > 0
                        ? $"<----- Completado {nombre}. Registros procesados: {procesados}."
                        : $"<----- Sin cambios en {nombre}.");
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Log(_logFile, $"<----- Cancelado durante {nombre}.");
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"<----- ERROR en {nombre}: {ex.Message}");
                    if (ex.InnerException != null)
                        FileLogger.Log(_logFile, $"Inner: {ex.InnerException.Message}");
                }
            }

            FileLogger.Log(_logFile, "Ciclo completado");
        }

        private void LimpiarLogsAntiguos()
        {
            try
            {
                string carpetaLogs = Path.GetDirectoryName(_logFile)!;
                if (!Directory.Exists(carpetaLogs)) return;

                var archivosViejos = Directory.GetFiles(carpetaLogs, "ActualizaCxP_*.txt")
                    .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-15));

                foreach (var archivo in archivosViejos)
                {
                    File.Delete(archivo);
                    FileLogger.Log(_logFile, $"Log eliminado: {Path.GetFileName(archivo)}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(_logFile, $"Error limpiando logs antiguos: {ex.Message}");
            }
        }
    }
}
