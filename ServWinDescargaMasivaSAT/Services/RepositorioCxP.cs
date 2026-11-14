using Microsoft.Data.SqlClient;
using ServWinDescargaMasivaSAT.Models;
using Dapper;
using System;
using System.Collections.Generic;
using System.Text;

namespace ServWinDescargaMasivaSAT.Services
{
    public interface IRepositorioCxP
    {
        Task<int?> BuscarProveedorPorRfcAsync(string rfc);
        Task<List<ProvCxp>> BuscarCxpPorProveedorYMontoAsync(int idProveedor, decimal total);
        Task InsertarDocumentoProvCxpAsync(int idDocumento, int idCalendarioPagos);
        Task<int?> ObtenerOCrearCalendarioPagoAsync(int idProvCxp, decimal importe, DateTime fechaFactura);

        // registra la cxp como candidata; la confirmación es manual
        Task InsertarCandidatoConciliacionAsync(int idFacturaSat, int idCxp, decimal valorCxp, decimal valorFacturaSat);
    }
    public class RepositorioCxP : IRepositorioCxP
    {
        private readonly string _connectionString;
        private readonly ILogger<RepositorioCxP> _logger;

        private const int ID_USUARIO_SISTEMA = 1;

        public RepositorioCxP(IConfiguration config, ILogger<RepositorioCxP> logger)
        {
            _connectionString = config["ConnectionStrings:Geisha"]!;
            _logger = logger;
        }

        public async Task<int?> BuscarProveedorPorRfcAsync(string rfc)
        {
            using var conn = new SqlConnection(_connectionString);
            var id = await conn.QueryFirstOrDefaultAsync<int?>(
                @"SELECT pv.Id
                  FROM PersPersonas pp
                  INNER JOIN PersProveedores pv ON pv.IdPersPersona = pp.Id
                  WHERE pp.Rfc = @Rfc AND pp.Estatus = 1 AND pv.Estatus = 1",
                new { Rfc = rfc });

            if (id is null)
                _logger.LogInformation("Proveedor no encontrado para RFC={Rfc}", rfc);

            return id;
        }

        public async Task<List<ProvCxp>> BuscarCxpPorProveedorYMontoAsync(
            int idProveedor, decimal total)
        {
            using var conn = new SqlConnection(_connectionString);
            var resultados = await conn.QueryAsync<ProvCxp>(
                @"SELECT c.Id, c.IdProveedor, c.ImporteTotal, c.IdSucursal, c.CxPEstado
                  FROM ProvCXP c
                  WHERE c.IdProveedor  = @IdProveedor
                    AND c.ImporteTotal = @Total
                    AND c.CxPEstado    = 'P'
                    AND c.Estatus      = 1
                    -- No proponer una CxP que ya está confirmada como conciliada
                    -- con otra factura. Una candidatura sin confirmar sí puede
                    -- seguir compitiendo (caso 'concilia muchas'), pero una vez
                    -- confirmada queda cerrada para cualquier otra factura.
                    AND NOT EXISTS (
                        SELECT 1 FROM ProvFacturasConciliacionSAT pfc
                        INNER JOIN ProvFacturasSAT fs ON fs.Id = pfc.IdFacturaSAT
                        WHERE pfc.IdCxP = c.Id
                          AND fs.EsConfirmada = 1
                          AND fs.Estatus = 1
                    )",
                new { IdProveedor = idProveedor, Total = total });

            return resultados.ToList();
        }

        public async Task InsertarDocumentoProvCxpAsync(int idDocumento, int idCalendarioPagos)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                @"INSERT INTO OperDocumentosXTipoProvCXP
                  (IdProvCalendarioPagos, IdOperDocumentosXTipo, Fecha,
                   Observaciones, Estatus, FechaAlta)
                VALUES
                  (@IdCalendarioPagos, @IdDocumento, GETDATE(),
                   NULL, 1, GETDATE())",
                new
                {
                    IdCalendarioPagos = idCalendarioPagos,
                    IdDocumento = idDocumento
                });

            _logger.LogInformation("Documento {IdDoc} vinculado a CalendarioPago {IdCal}",
                idDocumento, idCalendarioPagos);
        }

        public async Task<int?> ObtenerOCrearCalendarioPagoAsync(int idProvCxp, decimal importe, DateTime fechaFactura)
        {
            using var conn = new SqlConnection(_connectionString);

            var idCalendario = await conn.QueryFirstOrDefaultAsync<int?>(
                @"SELECT TOP 1 Id FROM ProvCalendarioPagos
                WHERE IdProvCxP = @IdProvCxp AND Estatus = 1",
                new { IdProvCxp = idProvCxp });

            if (idCalendario.HasValue)
                return idCalendario;

            var plazoDias = await conn.QueryFirstOrDefaultAsync<int?>(
                @"SELECT cc.PlazoDias
                FROM PersProveedores pv
                LEFT JOIN ComuCatCondicionesPago cc ON cc.Id = pv.IdCondicionPago
                WHERE pv.Id = (SELECT IdProveedor FROM ProvCXP WHERE Id = @IdProvCxp AND Estatus = 1)",
                new { IdProvCxp = idProvCxp });

            var fechaPago = fechaFactura.AddDays(plazoDias ?? 0);

            idCalendario = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO ProvCalendarioPagos
                  (IdProvCxP, NoPago, FechaPromesaDePago, Importe, Impuesto,
                   IdFormaDePago, IdOperEstado, IdTipoOper, Estatus, FechaAlta)
                VALUES
                  (@IdProvCxp, 1, @FechaPago, @Importe, 0,
                   5, 1, 1, 1, GETDATE());
                SELECT SCOPE_IDENTITY();",
                new
                {
                    IdProvCxp = idProvCxp,
                    FechaPago = fechaPago,
                    Importe = importe
                });

            _logger.LogInformation("Calendario creado para CxP {IdCxp}. FechaPago={Fecha}",
                idProvCxp, fechaPago);

            return idCalendario;
        }

        public async Task InsertarCandidatoConciliacionAsync(
            int idFacturaSat, int idCxp, decimal valorCxp, decimal valorFacturaSat)
        {
            using var conn = new SqlConnection(_connectionString);

            // NOT EXISTS para no duplicar el candidato en reintentos
            var filasAfectadas = await conn.ExecuteAsync(
                @"IF NOT EXISTS (
                      SELECT 1 FROM ProvFacturasConciliacionSAT
                      WHERE IdFacturaSAT = @IdFacturaSat
                        AND IdCxP        = @IdCxp
                        AND Estatus      = 1
                  )
                  INSERT INTO ProvFacturasConciliacionSAT
                      (FechaConciliacion, IdFacturaSAT, IdCxP, CampoNoValido,
                       ValorCxP, ValorFacturaSAT, Estatus, IdUsuarioAlta, FechaAlta)
                  VALUES
                      (GETDATE(), @IdFacturaSat, @IdCxp, @CampoNoValido,
                       @ValorCxp, @ValorFacturaSat, 1, @IdUsuarioSistema, GETDATE())",
                new
                {
                    IdFacturaSat = idFacturaSat,
                    IdCxp = idCxp,
                    ValorCxp = valorCxp,
                    ValorFacturaSat = valorFacturaSat,
                    // CampoNoValido no admite NULL
                    CampoNoValido = string.Empty,
                    IdUsuarioSistema = ID_USUARIO_SISTEMA
                });

            if (filasAfectadas > 0)
                _logger.LogInformation(
                    "Candidato de conciliación registrado: Factura={IdFactura} CxP={IdCxp}",
                    idFacturaSat, idCxp);
        }
    }
}