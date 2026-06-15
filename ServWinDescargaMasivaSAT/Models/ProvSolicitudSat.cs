using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class ProvSolicitudSat
    {
        public int Id { get; set; }
        public string NumPeticionSat { get; set; } = string.Empty;
        public DateTime FechaSolicitud { get; set; }
        public int IdEstatusSolicitudSat { get; set; }
        public DateTime? FechaAceptacion { get; set; }
        public int Estatus { get; set; }
    }
}
