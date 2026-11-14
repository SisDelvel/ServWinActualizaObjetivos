using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class ResultadoVerificacion
    {
        public EstatusSolicitudSat Estatus { get; set; }

        // CodigoEstadoSolicitud del sat; 5004 = día sin comprobantes
        public string CodigoEstadoSolicitud { get; set; } = string.Empty;
        public List<string> IdsPaquetes { get; set; } = new();
        public string MensajeSat { get; set; } = string.Empty;
    }
}
