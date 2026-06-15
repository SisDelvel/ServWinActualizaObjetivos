using Dapper;
using ServWinActualizaCxP.Models;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text;

namespace ServWinActualizaCxP.Services
{
    public class RepositorioCxP
    {
        private readonly string _sourceConnectionString;
        private readonly string _destinationConnectionString;

        private const int ID_USUARIO_SISTEMA = 1;
        private const int ID_TIPO_OPER_CXP = 1;
        private const int ID_OPERESTADO_PAGADA = 5;
        private const string CXP_ESTADO_PROCESO = "P";
        private const string CXP_ESTADO_CONCLUIDO = "C";
        private const string INTELISIS_SITUACION_PAGADO = "Pagado";

        public RepositorioCxP(string sourceConnectionString, string destinationConnectionString)
        {
            _sourceConnectionString = sourceConnectionString;
            _destinationConnectionString = destinationConnectionString;
        }

        public async Task<int> SincronizarCxPAsync(string nombreConexion, string logFile, CancellationToken token = default)
        {
            FileLogger.Log(logFile, "  Leyendo proveedores desde GrupoGeisha...");

            // ── 1. Leer proveedores activos ───────────────────────────────────────
            List<ProveedorInfo> proveedores;

            using (var destConn = new SqlConnection(_destinationConnectionString))
            {
                await destConn.OpenAsync(token);

                proveedores = (await destConn.QueryAsync<ProveedorInfo>(@"
                SELECT pv.Id AS IdProveedor, p.Rfc
                FROM PersProveedores pv
                INNER JOIN PersPersonas p          ON p.Id = pv.IdPersPersona AND p.Estatus = 1
                INNER JOIN ProvTiemposRegistro ptr ON ptr.IdProveedor = pv.Id
                                                   AND ptr.IdProvCatEstados = 5
                                                   AND ptr.Estatus = 1
                WHERE pv.Estatus = 1
                  AND p.Rfc IS NOT NULL
                  AND p.Rfc <> ''")).ToList();
            }

            FileLogger.Log(logFile, $"  Proveedores activos: {proveedores.Count}");

            if (!proveedores.Any()) return 0;

            int totalProcesado = 0;

            // ── 2. Por cada proveedor leer sus CxP de Intelisis ──────────────────
            foreach (var proveedor in proveedores)
            {
                if (token.IsCancellationRequested) break;

                List<IntelisisCxpRaw> movimientosIntelisis;

                try
                {
                    using var srcConn = new SqlConnection(_sourceConnectionString);
                    await srcConn.OpenAsync(token);

                    movimientosIntelisis = (await srcConn.QueryAsync<IntelisisCxpRaw>(@"
                    SELECT CxP.ID            AS IdCXPIntelisis,
                           CxP.mov, CxP.movid, CxP.concepto, CxP.observaciones,
                           CxP.proyecto, CxP.usuario, CxP.referencia, CxP.condicion,
                           CxP.empresa, CxP.sucursal,
                           CxP.retencion, CxP.retencion2, CxP.retencion3,
                           CxP.beneficiario, CxP.movaplica, CxP.movAplicaId,
                           CxP.origen, CxP.origenId,
                           CxP.importe, CxP.fechaEmision,
                           CxP.estatus, CxP.Situacion
                    FROM cxp CxP
                    INNER JOIN Prov P ON CxP.Proveedor = P.Proveedor
                    WHERE P.RFC = @Rfc",
                        new { Rfc = proveedor.Rfc })).ToList();
                }
                catch (Exception ex)
                {
                    FileLogger.Log(logFile, $"  ✗ Error leyendo Intelisis para RFC={proveedor.Rfc}: {ex.Message}");
                    continue;
                }

                if (!movimientosIntelisis.Any()) continue;

                FileLogger.Log(logFile, $"  RFC={proveedor.Rfc} → {movimientosIntelisis.Count} movimiento(s) en Intelisis.");

                // ── 3. Upsert en GrupoGeisha ──────────────────────────────────────
                try
                {
                    using var destConn = new SqlConnection(_destinationConnectionString);
                    await destConn.OpenAsync(token);
                    using var transaction = destConn.BeginTransaction();

                    try
                    {
                        foreach (var mov in movimientosIntelisis)
                        {
                            token.ThrowIfCancellationRequested();

                            var cxpEstado = mov.Estatus?.ToUpper() == "PENDIENTE"
                                ? CXP_ESTADO_PROCESO
                                : CXP_ESTADO_CONCLUIDO;

                            // ── Upsert ProvCXP ────────────────────────────────────
                            await destConn.ExecuteAsync(@"
                            MERGE ProvCXP AS target
                            USING (SELECT @IdCXPIntelisis AS IdCXPIntelisis,
                                          @IdProveedor    AS IdProveedor) AS source
                               ON target.IdCXPIntelisis = source.IdCXPIntelisis
                              AND target.IdProveedor    = source.IdProveedor
                            WHEN MATCHED THEN
                                UPDATE SET CxPEstado      = @CxPEstado,
                                           ImporteTotal   = @ImporteTotal,
                                           FechaEmision   = @FechaEmision,
                                           IdTipoOper     = @IdTipoOper,
                                           IdUsuarioModif = @IdUsuario,
                                           FechaUltModif  = GETDATE()
                            WHEN NOT MATCHED THEN
                                INSERT (IdCXPIntelisis, IdProveedor, CxPEstado, Saldo,
                                        TotalPagos, NoPagosPendientes, FechaEmision,
                                        ImporteTotal, IdTipoOper, Estatus,
                                        IdUsuarioAlta, FechaAlta)
                                VALUES (@IdCXPIntelisis, @IdProveedor, @CxPEstado, 0,
                                        0, 0, @FechaEmision,
                                        @ImporteTotal, @IdTipoOper, 1,
                                        @IdUsuario, GETDATE());",
                                new
                                {
                                    mov.IdCXPIntelisis,
                                    proveedor.IdProveedor,
                                    CxPEstado = cxpEstado,
                                    ImporteTotal = mov.Importe,
                                    mov.FechaEmision,
                                    IdTipoOper = ID_TIPO_OPER_CXP,
                                    IdUsuario = ID_USUARIO_SISTEMA
                                }, transaction);

                            int idProvCxp = await destConn.QueryFirstOrDefaultAsync<int>(@"
                            SELECT Id FROM ProvCXP
                            WHERE IdCXPIntelisis = @IdCXPIntelisis
                              AND IdProveedor    = @IdProveedor",
                                new { mov.IdCXPIntelisis, proveedor.IdProveedor },
                                transaction);

                            if (idProvCxp == 0)
                            {
                                FileLogger.Log(logFile, $"  ✗ No se pudo obtener IdProvCXP para IdCXPIntelisis={mov.IdCXPIntelisis}. Saltando.");
                                continue;
                            }

                            // ── Upsert ProvCXPIntelisisDatos ─────────────────────
                            await destConn.ExecuteAsync(@"
                            MERGE ProvCXPIntelisisDatos AS target
                            USING (SELECT @IdProvCxP AS IdProvCxP) AS source
                               ON target.IdProvCxP = source.IdProvCxP
                            WHEN MATCHED THEN
                                UPDATE SET Mov           = @Mov,
                                           MovId         = @MovId,
                                           Concepto      = @Concepto,
                                           Proyecto      = @Proyecto,
                                           Usuario       = @Usuario,
                                           Referencia    = @Referencia,
                                           Observaciones = @Observaciones,
                                           Condicion     = @Condicion,
                                           Empresa       = @Empresa,
                                           Sucursal      = @Sucursal,
                                           Retencion     = @Retencion,
                                           Retencion2    = @Retencion2,
                                           Retencion3    = @Retencion3,
                                           Beneficiario  = @Beneficiario,
                                           MovAplica     = @MovAplica,
                                           MovAplicaId   = @MovAplicaId,
                                           Origen        = @Origen,
                                           OrigenId      = @OrigenId,
                                           FechaUltModif = GETDATE()
                            WHEN NOT MATCHED THEN
                                INSERT (IdProvCxP, Mov, MovId, Concepto, Proyecto,
                                        Usuario, Referencia, Observaciones, Condicion,
                                        Empresa, Sucursal,
                                        Retencion, Retencion2, Retencion3,
                                        Beneficiario, MovAplica, MovAplicaId,
                                        Origen, OrigenId,
                                        IdUsuarioAlta, FechaAlta)
                                VALUES (@IdProvCxP, @Mov, @MovId, @Concepto, @Proyecto,
                                        @Usuario, @Referencia, @Observaciones, @Condicion,
                                        @Empresa, @Sucursal,
                                        @Retencion, @Retencion2, @Retencion3,
                                        @Beneficiario, @MovAplica, @MovAplicaId,
                                        @Origen, @OrigenId,
                                        @IdUsuario, GETDATE());",
                                new
                                {
                                    IdProvCxP = idProvCxp,
                                    mov.Mov,
                                    mov.MovId,
                                    mov.Concepto,
                                    mov.Proyecto,
                                    mov.Usuario,
                                    mov.Referencia,
                                    mov.Observaciones,
                                    mov.Condicion,
                                    mov.Empresa,
                                    mov.Sucursal,
                                    mov.Retencion,
                                    mov.Retencion2,
                                    mov.Retencion3,
                                    mov.Beneficiario,
                                    mov.MovAplica,
                                    mov.MovAplicaId,
                                    mov.Origen,
                                    mov.OrigenId,
                                    IdUsuario = ID_USUARIO_SISTEMA
                                }, transaction);

                            // ── Marcar como Pagada si Intelisis reporta Pagado ────
                            if (string.Equals(mov.Situacion, INTELISIS_SITUACION_PAGADO, StringComparison.OrdinalIgnoreCase))
                            {
                                await destConn.ExecuteAsync(@"
                                UPDATE ProvCalendarioPagos
                                SET    IdOperEstado      = @IdOperEstado,
                                       IdUsuarioUltModif = @IdUsuario,
                                       FechaUltModif     = GETDATE()
                                WHERE  IdProvCxP = @IdProvCxP
                                  AND  Estatus   = 1
                                  AND  IdOperEstado != @IdOperEstado",
                                    new
                                    {
                                        IdOperEstado = ID_OPERESTADO_PAGADA,
                                        IdProvCxP = idProvCxp,
                                        IdUsuario = ID_USUARIO_SISTEMA
                                    }, transaction);

                                FileLogger.Log(logFile, $"  ✓ Pagos marcados como Pagada para IdProvCxP={idProvCxp}.");
                            }

                            totalProcesado++;
                        }

                        transaction.Commit();
                        FileLogger.Log(logFile, $"  ✓ Transacción completada para RFC={proveedor.Rfc}.");
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        FileLogger.Log(logFile, $"  ✗ Error en transacción para RFC={proveedor.Rfc}: {ex.Message}");
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log(logFile, $"  ✗ Error de conexión destino para RFC={proveedor.Rfc}: {ex.Message}");
                }
            }

            return totalProcesado;
        }
    }
}
