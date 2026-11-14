namespace ServWinDescargaMasivaSAT.Models
{
    // rechazo del sat (CodEstatus distinto de 5000); cuenta como intento fallido del día
    public class SatRechazoException : Exception
    {
        public string CodEstatus { get; }

        public SatRechazoException(string codEstatus, string mensaje)
            : base($"SAT rechazó la solicitud. CodEstatus={codEstatus}: {mensaje}")
        {
            CodEstatus = codEstatus;
        }
    }
}
