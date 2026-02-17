using Dapper;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsServiceTestGeisha;

namespace ServWinActualizaObj.Services
{
    public class RepositorioObjetivos
    {
        private readonly string _sourceConnectionString;
        private readonly string _destinationConnectionString;
        private readonly string _logFile;

        public RepositorioObjetivos(string sourceConnectionString, string destinationConnectionString, string logFile = null)
        {
            _sourceConnectionString = sourceConnectionString;
            _destinationConnectionString = destinationConnectionString;
            _logFile = logFile;
        }

        public async Task<int> ProcesarTransferenciaObjetivosAsync(CancellationToken cancellationToken = default)
        {
            const int idUsuarioPorDefecto = 1;
            var fechaHoy = DateTime.Now;

            // 1. DEFINIR VENTANA DE TIEMPO
            var periodoActual = new { Anio = fechaHoy.Year, Mes = fechaHoy.Month };
            var fechaAnterior = fechaHoy.AddMonths(-1);
            var periodoAnterior = new { Anio = fechaAnterior.Year, Mes = fechaAnterior.Month };

            FileLogger.Log(_logFile, "==================================================");
            FileLogger.Log(_logFile, $"Iniciando Sincronización (Ventana: {periodoAnterior.Mes}/{periodoAnterior.Anio} y {periodoActual.Mes}/{periodoActual.Anio})...");

            // 2. LEER DATOS DEL ORIGEN (SOLO PENDIENTES CON Enviar = 1)
            List<GrpoObjVtaSucursalArea> pendientesArea;
            List<GrpoObjVtaSucursalAreaAgente> pendientesAgente;
            List<GrpoObjVtaSucursalAreaAgeDia> pendientesDia;

            // LEER TODOS (solo para la tabla Dia, para detectar borrados)
            List<GrpoObjVtaSucursalAreaAgeDia> todosDia;

            using (var sourceConnection = new SqlConnection(_sourceConnectionString))
            {
                await sourceConnection.OpenAsync(cancellationToken);

                var parametros = new
                {
                    Anio1 = periodoActual.Anio,
                    Mes1 = periodoActual.Mes,
                    Anio2 = periodoAnterior.Anio,
                    Mes2 = periodoAnterior.Mes
                };

                const string sqlVentana = "(Ejercicio = @Anio1 AND Mes = @Mes1) OR (Ejercicio = @Anio2 AND Mes = @Mes2)";

                // AREA y AGENTE - SIN filtro de ventana (solo Enviar = 1)
                pendientesArea = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalArea>(
                    "SELECT * FROM GrpoObjVtaSucursalArea WHERE Enviar = 1", null)).ToList();

                pendientesAgente = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalAreaAgente>(
                    "SELECT * FROM GrpoObjVtaSucursalAreaAgente WHERE Enviar = 1", null)).ToList();

                // DIA - CON filtro de ventana (para pendientes)
                pendientesDia = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalAreaAgeDia>(
                    $"SELECT * FROM GrpoObjVtaSucursalAreaAgeDia WHERE ({sqlVentana}) AND Enviar = 1", parametros)).ToList();

                // DIA - TODOS (para detectar borrados)
                todosDia = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalAreaAgeDia>(
                    $"SELECT * FROM GrpoObjVtaSucursalAreaAgeDia WHERE {sqlVentana}", parametros)).ToList();
            }

            FileLogger.Log(_logFile, $"Pendientes: Area={pendientesArea.Count}, Agente={pendientesAgente.Count}, Dia={pendientesDia.Count}");
            FileLogger.Log(_logFile, $"Todos (Dia): {todosDia.Count}");

            // Identificar sucursales de este origen
            var sucursalesOrigen = pendientesArea.Select(x => x.IdSucursal)
                .Union(pendientesAgente.Select(x => x.IdSucursal))
                .Union(todosDia.Select(x => x.IdSucursal))
                .Distinct().ToList();

            FileLogger.Log(_logFile, $"Sucursales de este origen: {string.Join(", ", sucursalesOrigen)}");

            // 3. CONSTRUIR MAPEOS DE AGENTES
            Dictionary<string, int> mapaAgentes = new Dictionary<string, int>();
            Dictionary<int, int> mapaSucursalANegocio = new Dictionary<int, int>();

            var todosConAgente = pendientesAgente.Select(o => new { o.IdAgenAgente, o.IdSucursal })
                .Union(todosDia.Select(o => new { o.IdAgenAgente, o.IdSucursal }))
                .Where(x => !string.IsNullOrEmpty(x.IdAgenAgente))
                .Distinct().ToList();

            if (todosConAgente.Any())
            {
                using (var destConnection = new SqlConnection(_destinationConnectionString))
                {
                    await destConnection.OpenAsync(cancellationToken);

                    var sucursalesUnicas = todosConAgente.Select(x => x.IdSucursal).Distinct().ToList();
                    var resSuc = await destConnection.QueryAsync<dynamic>(
                        "SELECT Id, IdNegocio FROM ComuCatSucursales WHERE Id IN @Ids",
                        new { Ids = sucursalesUnicas });
                    mapaSucursalANegocio = resSuc.ToDictionary(r => (int)r.Id, r => (int)r.IdNegocio);

                    var agentesUnicos = todosConAgente.Select(x => NormalizarAgente(x.IdAgenAgente)).Distinct().ToList();
                    var negociosUnicos = mapaSucursalANegocio.Values.Distinct().ToList();

                    const string sqlMap = @"
                SELECT REPLACE(Agente, ' ', '') AS AgenteNorm, IDAgente, IdNegocio 
                FROM AgenAgenteIntelisis 
                WHERE REPLACE(Agente, ' ', '') IN @Agentes AND IdNegocio IN @Negocios";

                    var resMap = await destConnection.QueryAsync<dynamic>(sqlMap, new { Agentes = agentesUnicos, Negocios = negociosUnicos });

                    foreach (var r in resMap)
                    {
                        string clave = $"{r.AgenteNorm}|{r.IdNegocio}";
                        if (!mapaAgentes.ContainsKey(clave))
                            mapaAgentes.Add(clave, (int)r.IDAgente);
                    }
                }
            }

            FileLogger.Log(_logFile, $"Agentes mapeados: {mapaAgentes.Count}");

            // 4. PREPARAR DATOS PARA INSERTAR/ACTUALIZAR
            var idsAreaExitosos = new List<int>();
            var idsAgenteExitosos = new List<int>();
            var idsDiaExitosos = new List<int>();

            // A. AREA - Simple MERGE
            var datosArea = pendientesArea.Select(o => {
                idsAreaExitosos.Add(o.Id); // Todos son exitosos (no hay validación de mapeo)
                return new
                {
                    o.Ejercicio,
                    o.Mes,
                    o.IdSucursal,
                    o.IDArea,
                    o.ValorRealUnidades,
                    o.ValorRealMonto,
                    IdUsuarioAlta = idUsuarioPorDefecto
                };
            }).ToList();

            // B. AGENTE - Solo los que tienen mapeo
            var datosAgente = new List<dynamic>();
            foreach (var item in pendientesAgente)
            {
                if (!mapaSucursalANegocio.TryGetValue(item.IdSucursal, out int idNeg))
                    continue;

                string clave = $"{NormalizarAgente(item.IdAgenAgente)}|{idNeg}";

                if (mapaAgentes.ContainsKey(clave))
                {
                    idsAgenteExitosos.Add(item.Id);
                    datosAgente.Add(new
                    {
                        item.Ejercicio,
                        item.Mes,
                        item.IdSucursal,
                        item.IdArea,
                        IdAgenAgente = mapaAgentes[clave],
                        item.ValorRealUnidades,
                        item.ValorRealMonto,
                        IdUsuarioAlta = idUsuarioPorDefecto
                    });
                }
            }

            // C. DIA - Preparar datos para INSERT/UPDATE
            var datosDiaPendientes = new List<dynamic>();
            foreach (var item in pendientesDia)
            {
                if (!mapaSucursalANegocio.TryGetValue(item.IdSucursal, out int idNeg))
                    continue;

                string clave = $"{NormalizarAgente(item.IdAgenAgente)}|{idNeg}";

                if (mapaAgentes.ContainsKey(clave))
                {
                    idsDiaExitosos.Add(item.Id);
                    datosDiaPendientes.Add(new
                    {
                        item.Ejercicio,
                        item.Mes,
                        item.Dia,
                        item.IdSucursal,
                        item.IdArea,
                        IdAgenAgente = mapaAgentes[clave],
                        item.ValorRealUnidades,
                        item.ValorRealMonto,
                        IdUsuarioAlta = idUsuarioPorDefecto
                    });
                }
            }

            int omitidosAgente = pendientesAgente.Count - idsAgenteExitosos.Count;
            int omitidosDia = pendientesDia.Count - idsDiaExitosos.Count;

            if (omitidosAgente > 0)
                FileLogger.Log(_logFile, $"⚠ {omitidosAgente} registros de Agente omitidos (sin mapeo)");
            if (omitidosDia > 0)
                FileLogger.Log(_logFile, $"⚠ {omitidosDia} registros de Dia omitidos (sin mapeo)");

            // 5. LEER DESTINO (SOLO PARA DIA, SOLO SUCURSALES DE ESTE ORIGEN)
            List<dynamic> destinoDia;
            using (var destConnection = new SqlConnection(_destinationConnectionString))
            {
                await destConnection.OpenAsync(cancellationToken);

                const string sqlDestino = @"
            SELECT Ejercicio, Mes, Dia, IdSucursal, IdAgenAgente, ValorRealUnidades, ValorRealMonto, Estatus, IdArea
            FROM GrpoObjVtaSucursalAreaAgeDia 
            WHERE ((Ejercicio = @Anio1 AND Mes = @Mes1) OR (Ejercicio = @Anio2 AND Mes = @Mes2))
              AND IdSucursal IN @Sucursales";

                destinoDia = (await destConnection.QueryAsync<dynamic>(sqlDestino, new
                {
                    Anio1 = periodoActual.Anio,
                    Mes1 = periodoActual.Mes,
                    Anio2 = periodoAnterior.Anio,
                    Mes2 = periodoAnterior.Mes,
                    Sucursales = sucursalesOrigen
                })).ToList();
            }

            FileLogger.Log(_logFile, $"Registros en Destino (Dia, filtrado por sucursales): {destinoDia.Count}");

            // 6. DETECTAR BORRADOS EN DIA (comparar con TODOS los registros del origen)
            var todasClavesDiaOrigen = new HashSet<string>();
            foreach (var item in todosDia)
            {
                if (!mapaSucursalANegocio.TryGetValue(item.IdSucursal, out int idNeg))
                    continue;

                string clave = $"{NormalizarAgente(item.IdAgenAgente)}|{idNeg}";

                if (mapaAgentes.ContainsKey(clave))
                {
                    int idAgenteNumerico = mapaAgentes[clave];
                    string claveUnica = $"{item.Ejercicio}|{item.Mes}|{item.Dia}|{item.IdSucursal}|{item.IdArea}|{idAgenteNumerico}";
                    todasClavesDiaOrigen.Add(claveUnica);
                }
            }

            var paraDesactivarDia = new List<dynamic>();
            foreach (var destino in destinoDia)
            {
                if (Convert.ToInt32(destino.Estatus) == 0) continue;

                string claveDestino = $"{destino.Ejercicio}|{destino.Mes}|{destino.Dia}|{destino.IdSucursal}|{destino.IdArea}|{destino.IdAgenAgente}";

                if (!todasClavesDiaOrigen.Contains(claveDestino))
                {
                    paraDesactivarDia.Add(new
                    {
                        destino.Ejercicio,
                        destino.Mes,
                        destino.Dia,
                        destino.IdSucursal,
                        destino.IdAgenAgente,
                        destino.IdArea
                    });
                }
            }

            FileLogger.Log(_logFile, $"Acciones -> Area(MERGE):{datosArea.Count}, Agente(MERGE):{datosAgente.Count}, Dia(MERGE):{datosDiaPendientes.Count}, Dia(Desactivar):{paraDesactivarDia.Count}");

            // 7. APLICAR CAMBIOS EN DESTINO
            bool errorEnDestino = false;
            int totalProcesado = 0;

            try
            {
                using (var destConnection = new SqlConnection(_destinationConnectionString))
                {
                    await destConnection.OpenAsync(cancellationToken);
                    using (var transaction = destConnection.BeginTransaction())
                    {
                        try
                        {
                            // AREA - MERGE
                            if (datosArea.Any())
                            {
                                const string sqlArea = @"
                            MERGE INTO GrpoObjVtaSucursalArea AS T 
                            USING (SELECT @IdSucursal AS IdS, @IDArea AS IdA, @Ejercicio AS E, @Mes AS M) AS S 
                            ON T.IdSucursal = S.IdS AND T.IDArea = S.IdA AND T.Ejercicio = S.E AND T.Mes = S.M 
                            WHEN MATCHED THEN 
                                UPDATE SET ValorRealUnidades = @ValorRealUnidades, 
                                          ValorRealMonto = @ValorRealMonto, 
                                          FechaUltModif = GETDATE()
                            WHEN NOT MATCHED THEN 
                                INSERT (Ejercicio, Mes, IdSucursal, IDArea, ObjetivoUnidades, ObjetivoMonto, ValorRealUnidades, ValorRealMonto, Estatus, FechaAlta, IdUsuarioAlta) 
                                VALUES (@Ejercicio, @Mes, @IdSucursal, @IDArea, 0, 0, @ValorRealUnidades, @ValorRealMonto, 1, GETDATE(), @IdUsuarioAlta);";

                                await destConnection.ExecuteAsync(sqlArea, (IEnumerable<dynamic>)datosArea, transaction);
                                FileLogger.Log(_logFile, $"  ✓ Area procesados: {datosArea.Count}");
                            }

                            // AGENTE - MERGE
                            if (datosAgente.Any())
                            {
                                const string sqlAgente = @"
                            MERGE INTO GrpoObjVtaSucursalAreaAgente AS T 
                            USING (SELECT @IdSucursal AS IdS, @IdArea AS IdA, @IdAgenAgente AS IdAg, @Ejercicio AS E, @Mes AS M) AS S 
                            ON T.IdSucursal = S.IdS AND T.IDArea = S.IdA AND T.IdAgenAgente = S.IdAg AND T.Ejercicio = S.E AND T.Mes = S.M 
                            WHEN MATCHED THEN 
                                UPDATE SET ValorRealUnidades = @ValorRealUnidades, 
                                          ValorRealMonto = @ValorRealMonto, 
                                          FechaUltModif = GETDATE()
                            WHEN NOT MATCHED THEN 
                                INSERT (Ejercicio, Mes, IdSucursal, IdArea, IdAgenAgente, ObjetivoUnidades, ObjetivoMonto, ValorRealUnidades, ValorRealMonto, Estatus, FechaAlta, IdUsuarioAlta) 
                                VALUES (@Ejercicio, @Mes, @IdSucursal, @IdArea, @IdAgenAgente, 0, 0, @ValorRealUnidades, @ValorRealMonto, 1, GETDATE(), @IdUsuarioAlta);";

                                await destConnection.ExecuteAsync(sqlAgente, (IEnumerable<dynamic>)datosAgente, transaction);
                                FileLogger.Log(_logFile, $"  ✓ Agente procesados: {datosAgente.Count}");
                            }

                            // DIA - MERGE
                            if (datosDiaPendientes.Any())
                            {
                                const string sqlDia = @"
                            MERGE INTO GrpoObjVtaSucursalAreaAgeDia AS T 
                            USING (SELECT @IdSucursal AS IdS, @IdArea AS IdA, @IdAgenAgente AS IdAg, @Ejercicio AS E, @Mes AS M, @Dia AS D) AS S 
                            ON T.IdSucursal = S.IdS AND T.IDArea = S.IdA AND T.IdAgenAgente = S.IdAg AND T.Ejercicio = S.E AND T.Mes = S.M AND T.Dia = S.D 
                            WHEN MATCHED THEN 
                                UPDATE SET ValorRealUnidades = @ValorRealUnidades, 
                                          ValorRealMonto = @ValorRealMonto, 
                                          Estatus = 1,
                                          FechaUltModif = GETDATE()
                            WHEN NOT MATCHED THEN 
                                INSERT (Ejercicio, Mes, Dia, IdSucursal, IdArea, IdAgenAgente, ObjetivoUnidades, ObjetivoMonto, ValorRealUnidades, ValorRealMonto, Estatus, FechaAlta, IdUsuarioAlta) 
                                VALUES (@Ejercicio, @Mes, @Dia, @IdSucursal, @IdArea, @IdAgenAgente, 0, 0, @ValorRealUnidades, @ValorRealMonto, 1, GETDATE(), @IdUsuarioAlta);";

                                await destConnection.ExecuteAsync(sqlDia, (IEnumerable<dynamic>)datosDiaPendientes, transaction);
                                FileLogger.Log(_logFile, $"  ✓ Dia procesados: {datosDiaPendientes.Count}");
                            }

                            // DIA - DESACTIVAR (solo Dia tiene esta lógica)
                            if (paraDesactivarDia.Any())
                            {
                                const string sqlDesactivar = @"
                            UPDATE GrpoObjVtaSucursalAreaAgeDia 
                            SET Estatus = 0, FechaUltModif = GETDATE() 
                            WHERE Ejercicio = @Ejercicio 
                              AND Mes = @Mes 
                              AND Dia = @Dia 
                              AND IdSucursal = @IdSucursal 
                              AND IdArea = @IdArea 
                              AND IdAgenAgente = @IdAgenAgente;";

                                await destConnection.ExecuteAsync(sqlDesactivar, (IEnumerable<dynamic>)paraDesactivarDia, transaction);
                                FileLogger.Log(_logFile, $"  ✓ Dia desactivados: {paraDesactivarDia.Count}");
                            }

                            transaction.Commit();
                            totalProcesado = datosArea.Count + datosAgente.Count + datosDiaPendientes.Count + paraDesactivarDia.Count;
                            FileLogger.Log(_logFile, $"✓ Transacción completada. Total: {totalProcesado}");
                        }
                        catch (Exception ex)
                        {
                            transaction.Rollback();
                            errorEnDestino = true;
                            FileLogger.Log(_logFile, $"✗ Error en destino: {ex.Message}");
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errorEnDestino = true;
                FileLogger.Log(_logFile, $"✗ Error de conexión: {ex.Message}");
            }

            // 8. MARCAR COMO PROCESADOS EN ORIGEN (solo los exitosos)
            if (!errorEnDestino && (idsAreaExitosos.Any() || idsAgenteExitosos.Any() || idsDiaExitosos.Any()))
            {
                await MarcarComoProcesadosEnOrigen(idsAreaExitosos, idsAgenteExitosos, idsDiaExitosos);
                FileLogger.Log(_logFile, $"✓ Marcados en origen (Enviar=0): Area={idsAreaExitosos.Count}, Agente={idsAgenteExitosos.Count}, Dia={idsDiaExitosos.Count}");
            }

            return totalProcesado;
        }

        private async Task MarcarComoProcesadosEnOrigen(List<int> idsArea, List<int> idsAgente, List<int> idsDia)
        {
            try
            {
                using (var sourceConnection = new SqlConnection(_sourceConnectionString))
                {
                    await sourceConnection.OpenAsync();
                    using (var transaction = sourceConnection.BeginTransaction())
                    {
                        if (idsArea.Any())
                            await sourceConnection.ExecuteAsync(
                                "UPDATE GrpoObjVtaSucursalArea SET Enviar = 0 WHERE Id IN @Ids",
                                new { Ids = idsArea }, transaction);

                        if (idsAgente.Any())
                            await sourceConnection.ExecuteAsync(
                                "UPDATE GrpoObjVtaSucursalAreaAgente SET Enviar = 0 WHERE Id IN @Ids",
                                new { Ids = idsAgente }, transaction);

                        if (idsDia.Any())
                            await sourceConnection.ExecuteAsync(
                                "UPDATE GrpoObjVtaSucursalAreaAgeDia SET Enviar = 0 WHERE Id IN @Ids",
                                new { Ids = idsDia }, transaction);

                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(_logFile, $"✗ Error marcando origen: {ex.Message}");
            }
        }

        private string NormalizarAgente(string agente)
        {
            return string.IsNullOrEmpty(agente) ? "" : agente.Replace(" ", "").Trim();
        }
    }



    public class GrpoObjVtaSucursalArea

    {

        public int Id { get; set; }

        public int Ejercicio { get; set; }

        public int Mes { get; set; }

        public int IdSucursal { get; set; }

        public int IDArea { get; set; }

        public int ObjetivoUnidades { get; set; }

        public int ObjetivoMonto { get; set; }

        public int ValorRealUnidades { get; set; }

        public int ValorRealMonto { get; set; }

        public bool Enviar { get; set; }

        public System.DateTime? FechaAlta { get; set; }

        public int? IdUsuarioAlta { get; set; }

    }

    public class GrpoObjVtaSucursalAreaAgente

    {

        public int Id { get; set; }

        public int Ejercicio { get; set; }

        public int Mes { get; set; }

        public int IdSucursal { get; set; }

        public int IdArea { get; set; }

        public string IdAgenAgente { get; set; }

        public int ObjetivoUnidades { get; set; }

        public float ObjetivoMonto { get; set; }

        public int ValorRealUnidades { get; set; }

        public float ValorRealMonto { get; set; }

        public bool Enviar { get; set; }

        public System.DateTime? FechaAlta { get; set; }

        public int? IdUsuarioAlta { get; set; }

    }

    public class GrpoObjVtaSucursalAreaAgeDia

    {

        public int Id { get; set; }

        public int Ejercicio { get; set; }

        public int Mes { get; set; }

        public int Dia { get; set; }

        public int IdSucursal { get; set; }

        public int IdArea { get; set; }

        public string IdAgenAgente { get; set; }

        public int ObjetivoUnidades { get; set; }

        public float ObjetivoMonto { get; set; }

        public int ValorRealUnidades { get; set; }

        public float ValorRealMonto { get; set; }

        public bool Enviar { get; set; }

        public System.DateTime? FechaAlta { get; set; }

        public int? IdUsuarioAlta { get; set; }

    }

}


