using System;
using System.Collections.Generic;

namespace ServWinDescargaMasivaSAT.Models
{
    // nodo pago20:Pago; un xml puede traer varios
    public class PagoCfdi
    {
        // posición del nodo en el xml; separa dos pagos a la misma factura
        public int NumPago { get; set; } = 1;

        public DateTime FechaPago { get; set; }
        public string? FormaDePagoP { get; set; }
        public string? NumOperacion { get; set; }
        public string? MonedaP { get; set; }
        public decimal? TipoCambioP { get; set; }

        // total de este pago; debe cuadrar con la suma de ImpPagado
        public decimal? MontoPago { get; set; }

        public List<DoctoRelacionadoCfdi> Doctos { get; set; } = new();
    }

    // nodo pago20:DoctoRelacionado: factura que liquida el pago
    public class DoctoRelacionadoCfdi
    {
        // uuid de la factura pagada
        public string IdDocumento { get; set; } = string.Empty;

        public int? NumParcialidad { get; set; }
        public decimal? ImpSaldoAnt { get; set; }
        public decimal ImpPagado { get; set; }
        public decimal? ImpSaldoInsoluto { get; set; }
        public string? MonedaDR { get; set; }
        public decimal? EquivalenciaDR { get; set; }
    }
}
