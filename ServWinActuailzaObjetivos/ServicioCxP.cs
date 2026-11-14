using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WindowsServiceTestGeisha;

namespace ServWinActuailzaObjetivos.Services
{
    public partial class ServicioCxP : ServiceBase
    {
        private CancellationTokenSource _cancellationTokenSource;
        private Task _mainTask;
        private readonly string _logFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "ActualizaCxP_" + DateTime.Now.ToString("yyyy_MM_dd") + ".txt");

        public ServicioCxP()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            FileLogger.Log(_logFile, "---- Inicia Servicio de Sincronización de Cuentas por Pagar -------------------");
            _cancellationTokenSource = new CancellationTokenSource();
            _mainTask = Task.Run(() => EjecutarCicloPrincipalAsync(_cancellationTokenSource.Token));
        }

        private async Task EjecutarCicloPrincipalAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ProcesarTareaPrincipalAsync(token);
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Log(_logFile, "Ciclo principal cancelado.");
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"SE PRODUJO UNA EXCEPCIÓN GENERAL: {ex.Message}\nStackTrace: {ex.StackTrace}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ProcesarTareaPrincipalAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            FileLogger.Log(_logFile, "---- Iniciando ciclo de sincronización CxP ----");

            var conexionesIntelisis = new System.Collections.Generic.Dictionary<string, string>
            {
                { "Toque", "Data Source=192.168.89.3; Initial Catalog=Toque;Persist Security Info=False;Pwd=9GPgaPD#sce^HhP;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90" },
                //{ "Kique",  "Data Source=Kique.geisha.mx;  Initial Catalog=Kique; Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True" },
                //{ "Nicui",  "Data Source=192.168.89.1;      Initial Catalog=Nicui; Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90" },
                //{ "Reque",  "Data Source=Reque.geisha.mx;  Initial Catalog=reque; Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;Encrypt=False" },
                //{ "Nivil",  "Data Source=nivil.geisha.mx;  Initial Catalog=Nivil; Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;Encrypt=False" }
            };

            string destinationConnectionString = "Data Source = 192.168.89.3; Initial Catalog = GrupoGeisha; Persist Security Info = False; Pwd = 9GPgaPD#sce^HhP;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True";

            foreach (var entry in conexionesIntelisis)
            {
                if (token.IsCancellationRequested)
                {
                    FileLogger.Log(_logFile, "Se solicitó cancelación a mitad del ciclo. Deteniendo.");
                    break;
                }

                string nombreConexion = entry.Key;
                string sourceConnectionString = entry.Value;

                try
                {
                    FileLogger.Log(_logFile, $"-----> Procesando conexión: {nombreConexion}");

                    var repositorio = new RepositorioCxP(sourceConnectionString, destinationConnectionString, _logFile);
                    int procesados = await repositorio.SincronizarCxPAsync(nombreConexion, token);

                    FileLogger.Log(_logFile, procesados > 0
                        ? $"<----- Completado {nombreConexion}. Registros procesados: {procesados}."
                        : $"<----- Sin cambios en {nombreConexion}.");
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Log(_logFile, $"<----- Operación cancelada durante {nombreConexion}.");
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"<----- ERROR en {nombreConexion}: {ex.Message}");
                    FileLogger.Log(_logFile, $"StackTrace: {ex.StackTrace}");
                    if (ex.InnerException != null)
                        FileLogger.Log(_logFile, $"Inner Exception: {ex.InnerException.Message}");
                }
            }

            FileLogger.Log(_logFile, "---- Ciclo de sincronización CxP completado ----");
        }

        protected override void OnStop()
        {
            FileLogger.Log(_logFile, "---- Deteniendo el servicio CxP...");
            _cancellationTokenSource?.Cancel();

            try
            {
                _mainTask?.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                FileLogger.Log(_logFile, $"Excepción al esperar la tarea principal: {ex.Message}");
            }

            _cancellationTokenSource?.Dispose();
        }
    }
}
