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

        private const int TAMANO_CHUNK_INTELISIS = 20;

        private const string CXP_ESTADO_PROCESO = "P";
        private const string CXP_ESTADO_CONCLUIDO = "C";
        private const string CXP_ESTADO_CANCELADO = "X";
        private const string CXP_ESTADO_SINAFECTAR = "S";

        private const string INTELISIS_SITUACION_PAGADO = "Pagado";

        public RepositorioCxP(string sourceConnectionString, string destinationConnectionString)
        {
            _sourceConnectionString = sourceConnectionString;
            _destinationConnectionString = destinationConnectionString;
        }

        private class ExistenteCxpInfo
        {
            public int IdProveedor { get; set; }
            public int IdCXPIntelisis { get; set; }
        }

        private class SucursalCatalogoInfo
        {
            public int Id { get; set; }
            public int NoSucursal { get; set; }
        }

        public async Task<int> SincronizarCxPAsync(string nombreConexion, string logFile, CancellationToken token = default)
        {
            FileLogger.Log(logFile, "  Leyendo proveedores desde GrupoGeisha...");

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

            // ComuCatNegocios.NombreConexion trae el prefijo "Conexion"
            int? idNegocio;
            Dictionary<int, int> idSucursalPorNoSucursal = new();

            using (var destConn = new SqlConnection(_destinationConnectionString))
            {
                await destConn.OpenAsync(token);

                idNegocio = await destConn.QueryFirstOrDefaultAsync<int?>(@"
                    SELECT Id FROM ComuCatNegocios
                    WHERE NombreConexion = @NombreConexionCompleto AND Estatus = 1",
                    new { NombreConexionCompleto = "Conexion" + nombreConexion });

                if (idNegocio == null)
                {
                    FileLogger.Log(logFile,
                        $"  ⚠ No se encontró IdNegocio en ComuCatNegocios para la conexión '{nombreConexion}' " +
                        $"(se buscó NombreConexion='Conexion{nombreConexion}'). IdSucursal quedará NULL en todos los movimientos de esta corrida.");
                }
                else
                {
                    var sucursales = (await destConn.QueryAsync<SucursalCatalogoInfo>(@"
                        SELECT Id, NoSucursal FROM ComuCatSucursales
                        WHERE IdNegocio = @IdNegocio AND Estatus = 1",
                        new { IdNegocio = idNegocio.Value })).ToList();

                    idSucursalPorNoSucursal = sucursales.ToDictionary(s => s.NoSucursal, s => s.Id);

                    FileLogger.Log(logFile,
                        $"  IdNegocio={idNegocio} → {idSucursalPorNoSucursal.Count} sucursal(es) en catálogo.");
                }
            }

            Dictionary<int, HashSet<int>> existentesPorProveedor;

            using (var destConn = new SqlConnection(_destinationConnectionString))
            {
                await destConn.OpenAsync(token);

                var idsProveedores = proveedores.Select(p => p.IdProveedor).ToList();

                var existentesRaw = await destConn.QueryAsync<ExistenteCxpInfo>(@"
                    SELECT IdProveedor, IdCXPIntelisis FROM ProvCXP
                    WHERE IdProveedor IN @IdsProveedores 
                      AND Estatus = 1
                      AND CxPEstado = @CxPEstadoProceso",
                    new { IdsProveedores = idsProveedores, CxPEstadoProceso = CXP_ESTADO_PROCESO });

                existentesPorProveedor = existentesRaw
                    .GroupBy(x => x.IdProveedor)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.IdCXPIntelisis).ToHashSet());
            }

            var todosIdsConocidos = existentesPorProveedor.Values
                .SelectMany(s => s)
                .Distinct()
                .ToList();

            var movimientosPorRfc = new Dictionary<string, List<IntelisisCxpRaw>>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunk in proveedores.Chunk(TAMANO_CHUNK_INTELISIS))
            {
                if (token.IsCancellationRequested) break;

                var rfcsChunk = chunk.Select(p => p.Rfc).ToList();

                try
                {
                    using var srcConn = new SqlConnection(_sourceConnectionString);
                    await srcConn.OpenAsync(token);

                    var movimientosChunk = (await srcConn.QueryAsync<IntelisisCxpRaw>(@"
                    SELECT CxP.ID            AS IdCXPIntelisis,
                           CxP.mov, CxP.movid, CxP.concepto, CxP.observaciones,
                           CxP.proyecto, CxP.usuario, CxP.referencia, CxP.condicion,
                           CxP.empresa, CxP.sucursal,
                           CxP.retencion, CxP.retencion2, CxP.retencion3,
                           CxP.beneficiario, CxP.movaplica, CxP.movAplicaId,
                           CxP.origen, CxP.origenId,
                           CxP.importe, CxP.Impuestos, CxP.fechaEmision,
                           CxP.estatus, CxP.Situacion,
                           P.RFC             AS Rfc
                    FROM cxp CxP
                    INNER JOIN Prov P ON CxP.Proveedor = P.Proveedor
                    WHERE P.RFC IN @Rfcs
                      AND (UPPER(LTRIM(RTRIM(CxP.estatus))) = 'PENDIENTE'
                           OR CxP.ID IN @IdsConocidos)",
                        new
                        {
                            Rfcs = rfcsChunk,
                            IdsConocidos = todosIdsConocidos.Any() ? todosIdsConocidos : new List<int> { -1 }
                        })).ToList();

                    foreach (var grupo in movimientosChunk.GroupBy(m => m.Rfc, StringComparer.OrdinalIgnoreCase))
                    {
                        movimientosPorRfc[grupo.Key] = grupo.ToList();
                    }

                    FileLogger.Log(logFile,
                        $"  Chunk de {rfcsChunk.Count} proveedor(es) → {movimientosChunk.Count} movimiento(s) en Intelisis.");
                }
                catch (Exception ex)
                {
                    FileLogger.Log(logFile,
                        $"  ✗ Error leyendo Intelisis para chunk ({string.Join(",", rfcsChunk)}): {ex.Message}");
                    continue;
                }
            }

            int totalProcesado = 0;

            foreach (var proveedor in proveedores)
            {
                if (token.IsCancellationRequested) break;

                if (!movimientosPorRfc.TryGetValue(proveedor.Rfc, out var movimientosIntelisis) || !movimientosIntelisis.Any())
                    continue;

                FileLogger.Log(logFile, $"  RFC={proveedor.Rfc} → {movimientosIntelisis.Count} movimiento(s) en Intelisis.");

                var existentes = existentesPorProveedor.TryGetValue(proveedor.IdProveedor, out var set)
                    ? set
                    : new HashSet<int>();

                try
                {
                    using var destConn = new SqlConnection(_destinationConnectionString);
                    await destConn.OpenAsync(token);

                    using var transaction = destConn.BeginTransaction();

                    try
                    {
                        var omitidos = 0;

                        foreach (var mov in movimientosIntelisis)
                        {
                            token.ThrowIfCancellationRequested();

                            var cxpEstado = MapearCxPEstado(mov.Estatus, mov.IdCXPIntelisis, logFile);

                            if (cxpEstado != CXP_ESTADO_PROCESO && !existentes.Contains(mov.IdCXPIntelisis))
                            {
                                omitidos++;
                                continue;
                            }

                            var importeTotal = mov.Importe + mov.Impuestos;

                            // IdSucursal desde el campo Sucursal de intelisis
                            int? idSucursal = null;

                            if (idNegocio != null)
                            {
                                if (int.TryParse(mov.Sucursal, out var noSucursal))
                                {
                                    if (idSucursalPorNoSucursal.TryGetValue(noSucursal, out var idSucResuelto))
                                    {
                                        idSucursal = idSucResuelto;
                                    }
                                    else
                                    {
                                        FileLogger.Log(logFile,
                                            $"  ⚠ NoSucursal={noSucursal} no encontrado en catálogo para IdNegocio={idNegocio} " +
                                            $"(IdCXPIntelisis={mov.IdCXPIntelisis}). IdSucursal quedará NULL.");
                                    }
                                }
                                else
                                {
                                    FileLogger.Log(logFile,
                                        $"  ⚠ Campo Sucursal inválido o vacío ('{mov.Sucursal}') en IdCXPIntelisis={mov.IdCXPIntelisis}. IdSucursal quedará NULL.");
                                }
                            }

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
                                           IdSucursal     = @IdSucursal,
                                           IdTipoOper     = @IdTipoOper,
                                           IdUsuarioModif = @IdUsuario,
                                           FechaUltModif  = GETDATE()
                            WHEN NOT MATCHED THEN
                                INSERT (IdCXPIntelisis, IdProveedor, CxPEstado, Saldo,
                                        TotalPagos, NoPagosPendientes, FechaEmision,
                                        ImporteTotal, IdSucursal, IdTipoOper, Estatus,
                                        IdUsuarioAlta, FechaAlta)
                                VALUES (@IdCXPIntelisis, @IdProveedor, @CxPEstado, 0,
                                        0, 0, @FechaEmision,
                                        @ImporteTotal, @IdSucursal, @IdTipoOper, 1,
                                        @IdUsuario, GETDATE());",
                                new
                                {
                                    mov.IdCXPIntelisis,
                                    proveedor.IdProveedor,
                                    CxPEstado = cxpEstado,
                                    ImporteTotal = importeTotal,
                                    mov.FechaEmision,
                                    IdSucursal = idSucursal,
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
                        FileLogger.Log(logFile,
                            $"  ✓ Transacción completada para RFC={proveedor.Rfc}. Omitidos (no pendientes y desconocidos): {omitidos}.");
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

        private static string MapearCxPEstado(string? estatusIntelisis, int idCXPIntelisis, string logFile)
        {
            var normalizado = estatusIntelisis?.Trim().ToUpper();

            switch (normalizado)
            {
                case "PENDIENTE": return CXP_ESTADO_PROCESO;
                case "CONCLUIDO": return CXP_ESTADO_CONCLUIDO;
                case "CANCELADO": return CXP_ESTADO_CANCELADO;
                case "SINAFECTAR": return CXP_ESTADO_SINAFECTAR;
                default:
                    FileLogger.Log(logFile,
                        $"  ⚠ Estatus desconocido '{estatusIntelisis}' en IdCXPIntelisis={idCXPIntelisis}. Se trató como CONCLUIDO.");
                    return CXP_ESTADO_CONCLUIDO;
            }
        }
    }
}