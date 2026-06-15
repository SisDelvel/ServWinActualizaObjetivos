using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public class ProvCxp
    {
        public int Id { get; set; }
        public int IdProveedor { get; set; }
        public decimal ImporteTotal { get; set; }
        public int? IdSucursal { get; set; }
        public string CxPEstado { get; set; } = string.Empty;
    }
}
