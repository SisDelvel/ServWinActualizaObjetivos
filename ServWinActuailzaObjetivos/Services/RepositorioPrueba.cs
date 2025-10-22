using Dapper;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsServiceTestGeisha.Services
{
    public class RepositorioPrueba
    {
        private readonly string _sourceConnectionString;
        private readonly string _destinationConnectionString;

        // El constructor ahora acepta ambas cadenas de conexión
        public RepositorioPrueba(string sourceConnectionString, string destinationConnectionString)
        {
            _sourceConnectionString = sourceConnectionString;
            _destinationConnectionString = destinationConnectionString;
        }

        public async Task<int> ProcesarTransferenciaObjetivosAsync(CancellationToken cancellationToken = default)
        {
            // 1. LEER DATOS DEL ORIGEN (Donde Enviar = 1)
            List<GrpoObjVtaSucursalArea> objetivosArea;
            List<GrpoObjVtaSucursalAreaAgente> objetivosAgente;

            using (var sourceConnection = new SqlConnection(_sourceConnectionString))
            {
                await sourceConnection.OpenAsync(cancellationToken);

                const string selectAreaSql = "SELECT * FROM GrpoObjVtaSucursalArea WHERE Enviar = 1;";
                objetivosArea = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalArea>(selectAreaSql)).ToList();

                const string selectAgenteSql = "SELECT * FROM GrpoObjVtaSucursalAreaAgente WHERE Enviar = 1;";
                objetivosAgente = (await sourceConnection.QueryAsync<GrpoObjVtaSucursalAreaAgente>(selectAgenteSql)).ToList();
            }

            // Si no hay nada que procesar, termina el ciclo.
            if (!objetivosArea.Any() && !objetivosAgente.Any())
            {
                return 0;
            }

            // 2. INSERTAR O ACTUALIZAR DATOS EN EL DESTINO (Usando MERGE)
            using (var destConnection = new SqlConnection(_destinationConnectionString))
            {
                await destConnection.OpenAsync(cancellationToken);
                using (var transaction = destConnection.BeginTransaction())
                {
                    try
                    {
                        if (objetivosArea.Any())
                        {
                            // Lógica de UPSERT para la tabla de Áreas
                            const string mergeAreaSql = @"
                                MERGE INTO GrpoObjVtaSucursalArea AS T
                                USING (SELECT @IdSucursal AS IdSucursal, @IDArea AS IDArea, @Ejercicio AS Ejercicio, @Mes AS Mes) AS S
                                ON T.IdSucursal = S.IdSucursal AND T.IDArea = S.IDArea AND T.Ejercicio = S.Ejercicio AND T.Mes = S.Mes
                                WHEN MATCHED THEN
                                    UPDATE SET
                                        ObjetivoUnidades = @ObjetivoUnidades,
                                        ObjetivoMonto = @ObjetivoMonto,
                                        ValorRealUnidades = @ValorRealUnidades,
                                        ValorRealMonto = @ValorRealMonto,
                                        FechaUltModif = GETDATE(),
                                        IdUsuarioModif = @IdUsuarioAlta
                                WHEN NOT MATCHED BY TARGET THEN
                                    INSERT (Ejercicio, Mes, IdSucursal, IDArea, ObjetivoUnidades, ObjetivoMonto, ValorRealUnidades, ValorRealMonto, FechaAlta, IdUsuarioAlta)
                                    VALUES (@Ejercicio, @Mes, @IdSucursal, @IDArea, @ObjetivoUnidades, @ObjetivoMonto, @ValorRealUnidades, @ValorRealMonto, @FechaAlta, @IdUsuarioAlta);";
                            await destConnection.ExecuteAsync(mergeAreaSql, objetivosArea, transaction);
                        }

                        if (objetivosAgente.Any())
                        {
                            // Lógica de UPSERT para la tabla de Agentes (ya incluye IdAgenAgente)
                            const string mergeAgenteSql = @"
                                MERGE INTO GrpoObjVtaSucursalAreaAgente AS T
                                USING (SELECT @IdSucursal AS IdSucursal, @IDArea AS IDArea, @IdAgenAgente AS IdAgenAgente, @Ejercicio AS Ejercicio, @Mes AS Mes) AS S
                                ON T.IdSucursal = S.IdSucursal AND T.IDArea = S.IDArea AND T.IdAgenAgente = S.IdAgenAgente AND T.Ejercicio = S.Ejercicio AND T.Mes = S.Mes
                                WHEN MATCHED THEN
                                    UPDATE SET
                                        ObjetivoUnidades = @ObjetivoUnidades,
                                        ObjetivoMonto = @ObjetivoMonto,
                                        ValorRealUnidades = @ValorRealUnidades,
                                        ValorRealMonto = @ValorRealMonto,
                                        FechaUltModif = GETDATE(),
                                        IdUsuarioModif = @IdUsuarioAlta
                                WHEN NOT MATCHED BY TARGET THEN
                                    INSERT (Ejercicio, Mes, IdSucursal, IdArea, IdAgenAgente, ObjetivoUnidades, ObjetivoMonto, ValorRealUnidades, ValorRealMonto, FechaAlta, IdUsuarioAlta)
                                    VALUES (@Ejercicio, @Mes, @IdSucursal, @IdArea, @IdAgenAgente, @ObjetivoUnidades, @ObjetivoMonto, @ValorRealUnidades, @ValorRealMonto, @FechaAlta, @IdUsuarioAlta);";
                            await destConnection.ExecuteAsync(mergeAgenteSql, objetivosAgente, transaction);
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }

            // 3. ACTUALIZAR EL ORIGEN (Poner 'Enviar' en 0)
            using (var sourceConnection = new SqlConnection(_sourceConnectionString))
            {
                await sourceConnection.OpenAsync(cancellationToken);
                using (var transaction = sourceConnection.BeginTransaction())
                {
                    try
                    {
                        if (objetivosArea.Any())
                        {
                            const string updateAreaSql = "UPDATE GrpoObjVtaSucursalArea SET Enviar = 0 WHERE Id = @Id;";
                            await sourceConnection.ExecuteAsync(updateAreaSql, objetivosArea.Select(o => new { o.Id }), transaction);
                        }

                        if (objetivosAgente.Any())
                        {
                            const string updateAgenteSql = "UPDATE GrpoObjVtaSucursalAreaAgente SET Enviar = 0 WHERE Id = @Id;";
                            await sourceConnection.ExecuteAsync(updateAgenteSql, objetivosAgente.Select(o => new { o.Id }), transaction);
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }

            return objetivosArea.Count + objetivosAgente.Count;
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
        public int IdAgenAgente { get; set; }
        public int ObjetivoUnidades { get; set; }
        public float ObjetivoMonto { get; set; }
        public int ValorRealUnidades { get; set; }
        public float ValorRealMonto { get; set; }
        public bool Enviar { get; set; }
        public System.DateTime? FechaAlta { get; set; }
        public int? IdUsuarioAlta { get; set; }
    }
}