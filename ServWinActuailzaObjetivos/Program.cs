using ServWinActuailzaObjetivos.Services;
using System;
using System.ServiceProcess;
using System.Threading;

namespace WindowsServiceTestGeisha
{
    internal static class Program
    {
        static void Main()
        {
#if DEBUG
            var servicio = new ServicioCxP();

            servicio.GetType()
                .GetMethod("OnStart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(servicio, new object[] { new string[0] });

            Console.WriteLine("Servicio corriendo en modo debug. Esperando que procese...");
            Thread.Sleep(TimeSpan.FromSeconds(30));

            Console.WriteLine("Procesamiento completado. Presiona ENTER para detener.");
            Console.ReadLine();

            servicio.GetType()
                .GetMethod("OnStop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(servicio, null);
#else
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service1(),
                new ServicioCxP()
            };
            ServiceBase.Run(ServicesToRun);
#endif
        }
    }
}