using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class ResultadoVerificacion
    {
        public EstatusSolicitudSat Estatus { get; set; }
        public List<string> IdsPaquetes { get; set; } = new();
        public string MensajeSat { get; set; } = string.Empty;
    }
}
