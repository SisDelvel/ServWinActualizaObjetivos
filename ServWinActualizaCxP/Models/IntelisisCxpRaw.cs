using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinActualizaCxP.Models
{
    public class IntelisisCxpRaw
    {
        public int IdCXPIntelisis { get; set; }
        public string Rfc { get; set; } = string.Empty;
        public string Mov { get; set; } = string.Empty;
        public string? MovId { get; set; }
        public string? Concepto { get; set; }
        public string? Observaciones { get; set; }
        public string? Proyecto { get; set; }
        public string? Usuario { get; set; }
        public string? Referencia { get; set; }
        public string? Condicion { get; set; }
        public string? Empresa { get; set; }
        public string? Sucursal { get; set; }
        public decimal Retencion { get; set; }
        public decimal Retencion2 { get; set; }
        public decimal Retencion3 { get; set; }
        public string? Beneficiario { get; set; }
        public string? MovAplica { get; set; }
        public string? MovAplicaId { get; set; }
        public string? Origen { get; set; }
        public string? OrigenId { get; set; }
        public decimal Importe { get; set; }
        public decimal Impuestos { get; set; }
        public DateTime? FechaEmision { get; set; }
        public string? Estatus { get; set; }
        public string? Situacion { get; set; }
    }
}
