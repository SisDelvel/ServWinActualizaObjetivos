using ServWinActualizaObj.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace WindowsServiceTestGeisha
{
    public partial class Service1 : ServiceBase
    {
        private CancellationTokenSource _cancellationTokenSource;
        private Task _mainTask;
        private readonly string _logFile = @"C:\Log\ActualizaObjetivo_" + DateTime.Now.ToString("yyyy_MM_dd") + ".txt";

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            FileLogger.Log(_logFile, "---- Inicia Servicio de Transferencia de Objetivos -------------------");
            _cancellationTokenSource = new CancellationTokenSource();
            _mainTask = Task.Run(() => EjecutarCicloPrincipalAsync(_cancellationTokenSource.Token));
        }

        private async Task EjecutarCicloPrincipalAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Ejecuta el procesamiento de TODAS las conexiones
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
                    // Espera 5 minutos antes de volver a procesar TODAS las conexiones
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
            FileLogger.Log(_logFile, "---- Iniciando ciclo de transferencia (procesará TODAS las conexiones) ----");

            // 1. Definir todas las conexiones de origen
            var conexionesDeOrigen = new Dictionary<string, string>
            {
                { "Toque", "Data Source=toque.geisha.mx; Initial Catalog=Toque;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True;Connection Timeout=90" },
                //{ "Toque", "Data Source=192.168.89.3; Initial Catalog=Toque;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90"},
                { "Kique", "Data Source=Kique.geisha.mx; Initial Catalog=Kique;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True" },
                //{ "Kique", "Data Source=192.168.89.3; Initial Catalog=Kique;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90"},
                //{ "Nicui", "Data Source=nicui.geisha.mx; Initial Catalog=Nicui;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True"},
                { "Nicui", "Data Source=192.168.89.1; Initial Catalog=Nicui;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90"},
                //{ "Nicui", "Data Source=192.168.89.3; Initial Catalog=Nicui;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90"},
                { "Reque", "Data Source=Reque.geisha.mx; Initial Catalog=reque;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb; Encrypt=False"},
                //{ "Reque", "Data Source=192.168.89.3; Initial Catalog=Reque;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90"},
                { "Nivil", "Data Source=nivil.geisha.mx; Initial Catalog=Nivil;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb; Encrypt=False" }
                //{ "Nivil", "Data Source=192.168.89.3; Initial Catalog=Nivil;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa; Encrypt=False"}
            };

            string destinationConnectionString = "Data Source=web.geisha.mx; Initial Catalog=GrupoGeisha;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True";
            //string destinationConnectionString = "Data Source = 192.168.89.3; Initial Catalog = GrupoGeisha2; Persist Security Info = False; Pwd = Geish@; MultipleActiveResultSets = True; Connect Timeout = 1000; User ID = sa; TrustServerCertificate = True";

            // 3. Iterar sobre cada conexión de origen
            foreach (var entry in conexionesDeOrigen)
            {
                string nombreConexion = entry.Key;
                string sourceConnectionString = entry.Value;

                // Verificar si se solicitó cancelar antes de procesar la siguiente conexión
                if (token.IsCancellationRequested)
                {
                    FileLogger.Log(_logFile, "Se solicitó cancelación a mitad del ciclo. Deteniendo.");
                    break;
                }

                try
                {
                    FileLogger.Log(_logFile, $"-----> Procesando Conexión: {nombreConexion}");

                    // 5. Instanciar el repositorio y procesar
                    var repositorio = new RepositorioObjetivos(sourceConnectionString, destinationConnectionString, _logFile);
                    int registrosProcesados = await repositorio.ProcesarTransferenciaObjetivosAsync(token);

                    // 6. Registrar el resultado para esta conexión específica
                    if (registrosProcesados > 0)
                    {
                        FileLogger.Log(_logFile, $"<----- Transferencia completada para {nombreConexion}. Se procesaron {registrosProcesados} registros.");
                    }
                    else
                    {
                        FileLogger.Log(_logFile, $"<----- No se encontraron registros nuevos para {nombreConexion}.");
                    }
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Log(_logFile, $"<----- Operación cancelada durante el procesamiento de {nombreConexion}.");
                    break; // Salir del bucle foreach si se cancela el servicio
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"<----- ERROR al procesar {nombreConexion}: {ex.Message}");
                    FileLogger.Log(_logFile, $"StackTrace: {ex.StackTrace}");

                    // Agregar información de la excepción interna si existe
                    if (ex.InnerException != null)
                    {
                        FileLogger.Log(_logFile, $"Inner Exception: {ex.InnerException.Message}");
                        FileLogger.Log(_logFile, $"Inner StackTrace: {ex.InnerException.StackTrace}");
                    }
                }
            }

            FileLogger.Log(_logFile, "---- Ciclo de transferencia completado ----");
        }

        protected override void OnStop()
        {
            FileLogger.Log(_logFile, "---- Deteniendo el servicio...");
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

