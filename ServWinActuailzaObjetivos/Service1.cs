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
using System.Timers;
using WindowsServiceTestGeisha.Services;

namespace WindowsServiceTestGeisha
{
    public partial class Service1 : ServiceBase
    {
        private CancellationTokenSource _cancellationTokenSource;
        private Task _mainTask;
        private readonly string _logFile = @"C:\Log\TransferService_" + DateTime.Now.ToString("yyyy_MM_dd") + ".txt";

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
                    await ProcesarTareaPrincipalAsync(token);
                }
                catch (OperationCanceledException)
                {
                    FileLogger.Log(_logFile, "Ciclo principal cancelado.");
                    break;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(_logFile, $"SE PRODUJO UNA EXCEPCIÓN EN EL CICLO PRINCIPAL: {ex.Message}\nStackTrace: {ex.StackTrace}");
                }

                try
                {
                    // ===== CAMBIO AQUÍ: La espera ahora es de 1 hora =====
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
            // ================== CAMBIOS AQUÍ ==================

            const string conexion = "Toque";
            //const string conexion = "Kique";
            //const string conexion = "Nicui";
            //const string conexion = "Reque";
            //const string conexion = "Nivil";

            token.ThrowIfCancellationRequested();
            FileLogger.Log(_logFile, "---- Iniciando ciclo de transferencia...");

            string sourceConnectionString;

            // Asigna la cadena de conexión de origen según el valor de la constante
            switch (conexion)
            {
                case "Toque":
                    //sourceConnectionString = "Data Source=192.168.89.3; Initial Catalog=Toque;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90";
                    sourceConnectionString = "Data Source=toque.geisha.mx; Initial Catalog=Toque;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True;Connection Timeout=90";
                    FileLogger.Log(_logFile, "Usando Conexión 1: Toque");
                    break;
                case "Kique":
                    //sourceConnectionString = "Data Source=192.168.89.3; Initial Catalog=Kique;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90";
                    sourceConnectionString = "Data Source=192.168.50.242; Initial Catalog=Kique;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True;Connection Timeout=90";
                    FileLogger.Log(_logFile, "Usando Conexión 2: Kique");
                    break;
                case "Nicui":
                    //sourceConnectionString = "Data Source=192.168.89.3; Initial Catalog=Nicui;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90";
                    sourceConnectionString = "Data Source=nicui.geisha.mx; Initial Catalog=Nicui;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb;TrustServerCertificate=True";
                    FileLogger.Log(_logFile, "Usando Conexión 3: Nicui");
                    break;
                case "Reque":
                    //sourceConnectionString = "Data Source=192.168.89.3; Initial Catalog=Reque;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90";
                    sourceConnectionString = "Data Source=Reque.geisha.mx; Initial Catalog=reque;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb; Encrypt=False";
                    FileLogger.Log(_logFile, "Usando Conexión 4: Reque");
                    break;
                case "Nivil":
                    //sourceConnectionString = "Data Source=192.168.89.3; Initial Catalog=Nivil;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa; Encrypt=False";
                    sourceConnectionString = "Data Source=nivil.geisha.mx; Initial Catalog=Nivil;Persist Security Info=False;Pwd=QiZSSnDd4XemHbd;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=ApWeb; Encrypt=False";
                    FileLogger.Log(_logFile, "Usando Conexión 5: Nivil");
                    break;
                default:
                    FileLogger.Log(_logFile, $"ERROR: El valor de 'conexion' ({conexion}) no es válido. Debe ser entre 1 y 5.");
                    return;
            }

            // La conexión de destino permanece igual
            //string destinationConnectionString = "Data Source=192.168.89.3; Initial Catalog=GrupoGeisha;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True";
            string destinationConnectionString = "Data Source=web.geisha.mx; Initial Catalog=GrupoGeisha;Persist Security Info=False;Pwd=Geish@;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True";

            var repositorio = new RepositorioPrueba(sourceConnectionString, destinationConnectionString);

            int registrosProcesados = await repositorio.ProcesarTransferenciaObjetivosAsync(token);

            if (registrosProcesados > 0)
            {
                FileLogger.Log(_logFile, $"Transferencia completada. Se procesaron {registrosProcesados} registros.");
            }
            else
            {
                FileLogger.Log(_logFile, "No se encontraron registros nuevos para transferir.");
            }
        }
        protected override void OnStop()
        {
            FileLogger.Log(_logFile, "---- Deteniendo el servicio...");
            _cancellationTokenSource?.Cancel();

            try
            {
                // Esperamos un tiempo razonable para que la tarea termine limpiamente.
                _mainTask?.Wait(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                FileLogger.Log(_logFile, $"Excepción al esperar la tarea principal: {ex.Message}");
            }

            _cancellationTokenSource?.Dispose();
            FileLogger.Log(_logFile, "---- Servicio Detenido ----------------------------------------------");
        }


    }
}
