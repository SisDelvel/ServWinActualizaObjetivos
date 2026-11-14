using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class FacturaPendiente
    {
        public int Id { get; set; }
        public string RfcEmisor { get; set; } = string.Empty;
        public decimal Monto { get; set; }

        public DateTime FechaFactura { get; set; }

    }
}
