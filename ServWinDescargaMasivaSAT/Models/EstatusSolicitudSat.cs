using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Models
{
    public enum EstatusSolicitudSat
    {
        Aceptada = 1, 
        EnProceso = 2,
        Terminada = 3,
        Error = 4,
        Rechazada = 5,
        Expirada = 6
    }
}
