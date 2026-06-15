using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class EstadoDescarga
    {
        public string IdSolicitud { get; set; } = string.Empty;
        public DateTime FechaInicio { get; set; }
        public DateTime FechaFin { get; set; }
        public string Estatus { get; set; } = "Pendiente";
        public DateTime FechaCreacion { get; set; } = DateTime.Now;
    }
}
