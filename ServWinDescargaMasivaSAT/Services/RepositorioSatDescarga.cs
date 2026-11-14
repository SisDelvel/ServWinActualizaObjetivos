using ServWinDescargaMasivaSAT.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using Dapper;
using Microsoft.Data.SqlClient;

namespace ServWinDescargaMasivaSAT.Services
{
    public interface IRepositorioSatDescarga
    {
        Task<string> SolicitarDescargaRecibidosAsync(string rfc, DateTime fechaInicio, DateTime fechaFin, CancellationToken ct = default);

        Task<ResultadoVerificacion> VerificarSolicitudAsync(string rfc, string idSolicitud, CancellationToken ct = default);

        Task<byte[]> DescargarPaqueteAsync(string rfc, string idPaquete, CancellationToken ct = default);

        Task<ProvSolicitudSat?> ObtenerSolicitudActivaAsync();

        Task<int> InsertarSolicitudAsync(string idSolicitudSat, DateTime fechaSolicitud, int idEstatus = RepositorioSatDescarga.EstatusEnProceso);

        // solicitudes activas desde la fecha indicada
        Task<List<ProvSolicitudSat>> ObtenerSolicitudesDesdeAsync(DateTime desde);

        Task ActualizarEstatusSolicitudAsync(int id, int idEstatus);

        // inserta la cabecera del xml; si el uuid ya existe regresa su id
        Task<int> InsertarFacturaSatAsync(int idSolicitud, string uuid, string rfcEmisor, string rfcReceptor, int idEstado, decimal monto, DateTime fechaFactura, string tipoDeComprobante, string? metodoPago);

        // marca la cabecera como factura (ProvFacturasSATPUE)
        Task<int> RegistrarFacturaPueAsync(int idFacturaSat);

        // marca la cabecera como complemento (ProvFacturasSATPPD)
        Task<int> RegistrarComplementoPpdAsync(int idFacturaSat, decimal? montoTotalPagos);

        // inserta un DoctoRelacionado en ProvFacsSATPPDConciliado
        Task InsertarDoctoRelacionadoAsync(int idProvFacturasPpd, PagoCfdi pago, DoctoRelacionadoCfdi docto);

        // liga los complementos que llegaron antes que su factura
        Task<int> ResolverFacturasDeComplementosAsync();

        Task ActualizarEstadoFacturaAsync(int id, int idEstado);

        Task<List<FacturaPendiente>> ObtenerFacturasPendientesAsync();

        Task<int?> ObtenerIdDocumentoPorFacturaAsync(int idFactura);

    }
    internal class RepositorioSatDescarga:IRepositorioSatDescarga
    {
        private const string UrlSolicitud = "https://cfdidescargamasivasolicitud.clouda.sat.gob.mx/SolicitaDescargaService.svc";
        private const string UrlVerifica = "https://cfdidescargamasivasolicitud.clouda.sat.gob.mx/VerificaSolicitudDescargaService.svc";
        private const string UrlDescarga = "https://cfdidescargamasiva.clouda.sat.gob.mx/DescargaMasivaService.svc";

        private const string ActionSolicitud = "http://DescargaMasivaTerceros.sat.gob.mx/ISolicitaDescargaService/SolicitaDescargaRecibidos";
        private const string ActionVerifica = "http://DescargaMasivaTerceros.sat.gob.mx/IVerificaSolicitudDescargaService/VerificaSolicitudDescarga";
        private const string ActionDescarga = "http://DescargaMasivaTerceros.sat.gob.mx/IDescargaMasivaTercerosService/Descargar";

        private const string NsDescarga = "http://DescargaMasivaTerceros.sat.gob.mx";
        private const string NsFirma = "http://www.w3.org/2000/09/xmldsig#";

        // estatus de ProvSolicitudesSAT (no son los del sat)
        public const int EstatusEnProceso = 4;
        public const int EstatusTerminada = 5;
        public const int EstatusFallida = 6;

        // solicitud aceptada
        public const string CodSolicitudAceptada = "5000";

        // sin comprobantes en el rango; el día cuenta como completo
        public const string CodigoSinInformacion = "5004";

        public const int FacturaEstadoPendiente = 1;
        public const int FacturaNoConcilia = 2;
        public const int FacturaConcilia = 3;
        public const int FacturaConciliaMuchas = 4;

        // TipoDeComprobante: I factura, P complemento, E nota de crédito
        public const string TipoComprobanteIngreso = "I";
        public const string TipoComprobantePago = "P";
        public const string TipoComprobanteEgreso = "E";

        private readonly HttpClient _http;
        private readonly IRepositorioSatAuth _auth;
        private readonly ILogger<RepositorioSatDescarga> _logger;
        private readonly string _rutaCer;
        private readonly string _rutaKey;
        private readonly string _contrasena;
        private readonly string _connectionString;

        public RepositorioSatDescarga(HttpClient http,IRepositorioSatAuth auth,ILogger<RepositorioSatDescarga> logger,IConfiguration config)
        {
            _http = http;
            _auth = auth;
            _logger = logger;
            _rutaCer = config["SatFiel:RutaCertificado"]!;
            _rutaKey = config["SatFiel:RutaLlavePrivada"]!;
            _contrasena = config["SatFiel:Contrasena"]!;
            _connectionString = config["ConnectionStrings:Geisha"]!;
        }

        //SAT: Paso 1

        public async Task<string> SolicitarDescargaRecibidosAsync(
            string rfc, DateTime fechaInicio, DateTime fechaFin, CancellationToken ct = default)
        {
            _logger.LogInformation("Solicitando descarga emitidos RFC={Rfc} del {Inicio} al {Fin}",
                rfc, fechaInicio.ToString("d"), fechaFin.ToString("d"));

            var token = await _auth.ObtenerTokenAsync(ct);
            var (cert, rsa) = CargarFiel();
            var xml = ConstruirSoapSolicitud(cert, rsa, rfc, fechaInicio, fechaFin);
            var respuesta = await EnviarAsync(UrlSolicitud, ActionSolicitud, token, xml, ct);

            var codEstatus = ExtraerAtributo(respuesta,
    "http://DescargaMasivaTerceros.sat.gob.mx",
    "SolicitaDescargaRecibidosResult", "CodEstatus");

            if (codEstatus != CodSolicitudAceptada)
            {
                var mensaje = ExtraerAtributo(respuesta,
                    "http://DescargaMasivaTerceros.sat.gob.mx",
                    "SolicitaDescargaRecibidosResult", "Mensaje");
                throw new SatRechazoException(codEstatus, mensaje);
            }


            var idSolicitud = ExtraerAtributo(respuesta,
                "http://DescargaMasivaTerceros.sat.gob.mx",
                "SolicitaDescargaRecibidosResult", "IdSolicitud");

            _logger.LogInformation("SAT asignó IdSolicitud={Id}", idSolicitud);
            return idSolicitud;
        }

        //SAT: Paso 2

        public async Task<ResultadoVerificacion> VerificarSolicitudAsync(
            string rfc, string idSolicitud, CancellationToken ct = default)
        {
            _logger.LogInformation("Verificando solicitud {Id}", idSolicitud);

            var token = await _auth.ObtenerTokenAsync(ct);
            var (cert, rsa) = CargarFiel();
            var xml = ConstruirSoapVerifica(cert, rsa, rfc, idSolicitud);
            var respuesta = await EnviarAsync(UrlVerifica, ActionVerifica, token, xml, ct);

            return ExtraerResultadoVerificacion(respuesta);
        }

        //SAT: Paso 3 

        public async Task<byte[]> DescargarPaqueteAsync(
            string rfc, string idPaquete, CancellationToken ct = default)
        {
            _logger.LogInformation("Descargando paquete {Id}", idPaquete);

            var token = await _auth.ObtenerTokenAsync(ct);
            var (cert, rsa) = CargarFiel();
            var xml = ConstruirSoapDescarga(cert, rsa, rfc, idPaquete);
            var respuesta = await EnviarAsync(UrlDescarga, ActionDescarga, token, xml, ct);

            var paqueteBase64 = ExtraerNodo(respuesta,
                "http://DescargaMasivaTerceros.sat.gob.mx",
                "RespuestaDescargaMasivaTercerosSalida/Paquete");

            _logger.LogInformation("Paquete {Id} descargado correctamente.", idPaquete);
            return Convert.FromBase64String(paqueteBase64);
        }

        //BD: Solicitudes

        public async Task<ProvSolicitudSat?> ObtenerSolicitudActivaAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryFirstOrDefaultAsync<ProvSolicitudSat>(
                @"SELECT TOP 1 * FROM ProvSolicitudesSAT
                  WHERE IdEstatusSolicitudSAT = @Estatus AND Estatus = 1
                  ORDER BY FechaSolicitud DESC",
                new { Estatus = EstatusEnProceso });
        }

        public async Task<List<ProvSolicitudSat>> ObtenerSolicitudesDesdeAsync(DateTime desde)
        {
            using var conn = new SqlConnection(_connectionString);
            var result = await conn.QueryAsync<ProvSolicitudSat>(
                @"SELECT Id, NumPeticionSat, FechaSolicitud, IdEstatusSolicitudSAT, FechaAceptacion, Estatus
                  FROM ProvSolicitudesSAT
                  WHERE FechaSolicitud >= @Desde AND Estatus = 1",
                new { Desde = desde.Date });
            return result.ToList();
        }

        public async Task<int> InsertarSolicitudAsync(string idSolicitudSat, DateTime fechaSolicitud, int idEstatus = EstatusEnProceso)
        {
            using var conn = new SqlConnection(_connectionString);
            var id = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO ProvSolicitudesSAT
                  (NumPeticionSat, FechaSolicitud, IdEstatusSolicitudSAT, FechaAceptacion, Estatus)
                VALUES
                  (@IdSolicitud, @FechaSolicitud, @IdEstatus, @FechaAceptacion, 1);
                SELECT SCOPE_IDENTITY();",
                new
                {
                    IdSolicitud = idSolicitudSat,
                    FechaSolicitud = fechaSolicitud,
                    IdEstatus = idEstatus,
                    FechaAceptacion = fechaSolicitud
                });

            _logger.LogInformation("Solicitud insertada en BD. Id={Id}", id);
            return id;
        }

        public async Task ActualizarEstatusSolicitudAsync(int id, int idEstatus)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                @"UPDATE ProvSolicitudesSAT
          SET IdEstatusSolicitudSAT = @IdEstatus,
              FechaAceptacion = CASE WHEN @IdEstatus = @EstatusTerminada THEN @Fecha ELSE FechaAceptacion END,
              FechaRechazo = CASE WHEN @IdEstatus = @EstatusFallida THEN @Fecha ELSE FechaRechazo END,
              FechaAlta = CASE WHEN FechaAlta IS NULL THEN @Fecha ELSE FechaAlta END,
              FechaUltModif = @Fecha
          WHERE Id = @Id",
                new { Id = id, IdEstatus = idEstatus, Fecha = DateTime.Now, EstatusTerminada, EstatusFallida });
        }

        //BD: Facturas

        // si el uuid ya existe regresa su id
        public async Task<int> InsertarFacturaSatAsync(
    int idSolicitud, string uuid, string rfcEmisor,
    string rfcReceptor, int idEstado, decimal monto, DateTime fechaFactura,
    string tipoDeComprobante, string? metodoPago)
        {
            using var conn = new SqlConnection(_connectionString);

            var idExistente = await conn.QueryFirstOrDefaultAsync<int?>(
                "SELECT TOP 1 Id FROM ProvFacturasSAT WHERE UUID = @Uuid ORDER BY Id",
                new { Uuid = uuid });

            if (idExistente.HasValue)
            {
                _logger.LogInformation(
                    "UUID={Uuid} ya estaba registrado (Id={Id}). No se reinserta.", uuid, idExistente.Value);
                return idExistente.Value;
            }

            var id = await conn.ExecuteScalarAsync<int>(
                @"INSERT INTO ProvFacturasSAT
                (IdSolicitudSAT, UUID, RFCEmisor, RFCReceptor,
                 IdEstadoFacturaSAT, Monto, FechaFactura, IdSucursal,
                 EsConfirmada, FechaRecepcion, FechaUltValidacion, FechaAlta, Estatus,
                 TipoDeComprobante, MetodoPago)
              VALUES
                (@IdSolicitud, @Uuid, @RfcEmisor, @RfcReceptor,
                 @IdEstado, @Monto, @FechaFactura, 1,
                 0, @FechaRecepcion, @FechaRecepcion, @FechaRecepcion, 1,
                 @TipoDeComprobante, @MetodoPago);
              SELECT SCOPE_IDENTITY();",
            new
            {
                IdSolicitud = idSolicitud,
                Uuid = uuid,
                RfcEmisor = rfcEmisor,
                RfcReceptor = rfcReceptor,
                IdEstado = idEstado,
                Monto = monto,
                FechaFactura = fechaFactura,
                FechaRecepcion = DateTime.Now,
                TipoDeComprobante = tipoDeComprobante,
                MetodoPago = metodoPago
            });

            return id;
        }

        //BD: Clasificación factura / complemento

        public async Task<int> RegistrarFacturaPueAsync(int idFacturaSat) =>
            await ClasificarAsync("ProvFacturasSATPUE", idFacturaSat);

        public async Task<int> RegistrarComplementoPpdAsync(int idFacturaSat, decimal? montoTotalPagos)
        {
            var id = await ClasificarAsync("ProvFacturasSATPPD", idFacturaSat);

            // sólo la tabla de complementos tiene este campo
            if (montoTotalPagos.HasValue)
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.ExecuteAsync(
                    @"UPDATE ProvFacturasSATPPD
                      SET    MontoTotalPagos = @Monto,
                             FechaUltModif   = GETDATE()
                      WHERE  Id = @Id
                        AND  ISNULL(MontoTotalPagos, -1) <> @Monto",
                    new { Id = id, Monto = montoTotalPagos.Value });
            }

            return id;
        }

        // si ya estaba clasificada regresa el renglón existente
        private async Task<int> ClasificarAsync(string tabla, int idFacturaSat)
        {
            using var conn = new SqlConnection(_connectionString);

            var idExistente = await conn.QueryFirstOrDefaultAsync<int?>(
                $"SELECT TOP 1 Id FROM {tabla} WHERE IdProvFacturaSAT = @IdFactura",
                new { IdFactura = idFacturaSat });

            if (idExistente.HasValue) return idExistente.Value;

            return await conn.ExecuteScalarAsync<int>(
                $@"INSERT INTO {tabla} (IdProvFacturaSAT, Estatus, FechaAlta)
                   VALUES (@IdFactura, 1, GETDATE());
                   SELECT SCOPE_IDENTITY();",
                new { IdFactura = idFacturaSat });
        }

        //BD: Complementos de pago

        // NOT EXISTS para no duplicar al reprocesar; sin factura, IdFacturaSAT queda NULL
        public async Task InsertarDoctoRelacionadoAsync(
            int idProvFacturasPpd, PagoCfdi pago, DoctoRelacionadoCfdi docto)
        {
            using var conn = new SqlConnection(_connectionString);

            var filas = await conn.ExecuteAsync(
                @"IF NOT EXISTS (
                      SELECT 1 FROM ProvFacsSATPPDConciliado
                      WHERE IdProvFacturasPPD = @IdPpd
                        AND NumPago           = @NumPago
                        AND UUIDRelacionada   = @Uuid
                  )
                  INSERT INTO ProvFacsSATPPDConciliado
                      (IdProvFacturasPPD, NumPago, FechaPago, FormaDePagoP, NumOperacion,
                       MonedaP, TipoCambioP, MontoPago, UUIDRelacionada, IdFacturaSAT,
                       NoParcialidad, MontoSaldoAnterior, MontoPagado, MontoSaldoInsoluto,
                       MonedaDR, EquivalenciaDR,
                       EsConciliado, Estatus, FechaAlta)
                  VALUES
                      (@IdPpd, @NumPago, @FechaPago, @FormaDePagoP, @NumOperacion,
                       @MonedaP, @TipoCambioP, @MontoPago, @Uuid,
                       -- Mientras queden UUID duplicados en ProvFacturasSAT (ver
                       -- script 02, sección 6) puede haber dos copias de la misma
                       -- factura. Se prefiere la confirmada, que es la que tiene
                       -- la CxP ligada; si ninguna lo está, la más antigua.
                       (SELECT TOP 1 Id FROM ProvFacturasSAT
                         WHERE UUID = @Uuid AND ISNULL(TipoDeComprobante, 'I') = 'I'
                         ORDER BY EsConfirmada DESC, Id),
                       @NoParcialidad, @MontoSaldoAnterior, @MontoPagado, @MontoSaldoInsoluto,
                       @MonedaDR, @EquivalenciaDR,
                       0, 1, GETDATE())",
                new
                {
                    IdPpd = idProvFacturasPpd,
                    pago.NumPago,
                    pago.FechaPago,
                    pago.FormaDePagoP,
                    pago.NumOperacion,
                    pago.MonedaP,
                    pago.TipoCambioP,
                    pago.MontoPago,
                    Uuid = docto.IdDocumento,
                    NoParcialidad = docto.NumParcialidad,
                    MontoSaldoAnterior = docto.ImpSaldoAnt,
                    MontoPagado = docto.ImpPagado,
                    MontoSaldoInsoluto = docto.ImpSaldoInsoluto,
                    docto.MonedaDR,
                    docto.EquivalenciaDR
                });

            if (filas > 0)
                _logger.LogInformation(
                    "Docto relacionado registrado: PPD={IdPpd} Pago={NumPago} UUID={Uuid} Pagado={Monto}",
                    idProvFacturasPpd, pago.NumPago, docto.IdDocumento, docto.ImpPagado);
        }

        public async Task<int> ResolverFacturasDeComplementosAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.ExecuteAsync(
                @"UPDATE c
                  SET    c.IdFacturaSAT  = f.Id,
                         c.FechaUltModif = GETDATE()
                  FROM   ProvFacsSATPPDConciliado c
                  CROSS APPLY (
                      SELECT TOP 1 Id FROM ProvFacturasSAT
                      WHERE UUID = c.UUIDRelacionada
                        AND ISNULL(TipoDeComprobante, 'I') = 'I'
                      ORDER BY EsConfirmada DESC, Id
                  ) f
                  WHERE  c.IdFacturaSAT IS NULL
                    AND  c.Estatus = 1");
        }

        public async Task ActualizarEstadoFacturaAsync(int id, int idEstado)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.ExecuteAsync(
                @"UPDATE ProvFacturasSAT
                  SET IdEstadoFacturaSAT = @IdEstado,
                      FechaUltValidacion = @Fecha
                  WHERE Id = @Id",
                new { Id = id, IdEstado = idEstado, Fecha = DateTime.Now });
        }

        //Construcción de XMLs SOAP

        private static string ConstruirSoapSolicitud(
    X509Certificate2 cert, RSA rsa, string rfc, DateTime inicio, DateTime fin)
        {
            var certB64 = Convert.ToBase64String(cert.RawData);
            var issuer = cert.Issuer;
            var serial = NumeroSerieLegible(cert);
            var inicioStr = inicio.ToString("yyyy-MM-ddTHH:mm:ss");
            var finStr = fin.ToString("yyyy-MM-ddTHH:mm:ss");

            // el nodo firmado debe declarar los mismos namespaces que el envelope (des y xd)
            var nodoXml =
                $"<des:SolicitaDescargaRecibidos xmlns:des=\"{NsDescarga}\" xmlns:xd=\"{NsFirma}\">" +
                $"<des:solicitud RfcReceptor=\"{rfc}\" FechaInicial=\"{inicioStr}\" " +
                $"FechaFinal=\"{finStr}\" TipoSolicitud=\"CFDI\" EstadoComprobante=\"Vigente\" RfcSolicitante=\"{rfc}\"/>" +
                $"</des:SolicitaDescargaRecibidos>";

            var (digestVal, sigVal) = FirmarNodo(nodoXml, rsa);

            // 2. Corregimos EstadoComprobante="Vigente" en el Envelope final
            return
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "xmlns:des=\"http://DescargaMasivaTerceros.sat.gob.mx\" " +
                "xmlns:xd=\"http://www.w3.org/2000/09/xmldsig#\">" +
                "<s:Header/><s:Body>" +
                "<des:SolicitaDescargaRecibidos>" +
                $"<des:solicitud RfcReceptor=\"{rfc}\" FechaInicial=\"{inicioStr}\" " +
                $"FechaFinal=\"{finStr}\" TipoSolicitud=\"CFDI\" EstadoComprobante=\"Vigente\" RfcSolicitante=\"{rfc}\">" +
                FirmaXml(certB64, issuer, serial, digestVal, sigVal) +
                "</des:solicitud>" +
                "</des:SolicitaDescargaRecibidos>" +
                "</s:Body></s:Envelope>";
        }

        private static string ConstruirSoapVerifica(
            X509Certificate2 cert, RSA rsa, string rfc, string idSolicitud)
        {
            var certB64 = Convert.ToBase64String(cert.RawData);
            var issuer = cert.Issuer;
            var serial = NumeroSerieLegible(cert);

            var nodoXml =
                $"<des:VerificaSolicitudDescarga xmlns:des=\"{NsDescarga}\" xmlns:xd=\"{NsFirma}\">" +
                $"<des:solicitud IdSolicitud=\"{idSolicitud}\" RfcSolicitante=\"{rfc}\"/>" +
                $"</des:VerificaSolicitudDescarga>";

            var (digestVal, sigVal) = FirmarNodo(nodoXml, rsa);

            return
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "xmlns:des=\"http://DescargaMasivaTerceros.sat.gob.mx\" " +
                "xmlns:xd=\"http://www.w3.org/2000/09/xmldsig#\">" +
                "<s:Header/><s:Body>" +
                "<des:VerificaSolicitudDescarga>" +
                $"<des:solicitud IdSolicitud=\"{idSolicitud}\" RfcSolicitante=\"{rfc}\">" +
                FirmaXml(certB64, issuer, serial, digestVal, sigVal) +
                "</des:solicitud></des:VerificaSolicitudDescarga>" +
                "</s:Body></s:Envelope>";
        }

        private static string ConstruirSoapDescarga(
            X509Certificate2 cert, RSA rsa, string rfc, string idPaquete)
        {
            var certB64 = Convert.ToBase64String(cert.RawData);
            var issuer = cert.Issuer;
            var serial = NumeroSerieLegible(cert);

            var nodoXml =
                $"<des:PeticionDescargaMasivaTercerosEntrada xmlns:des=\"{NsDescarga}\" xmlns:xd=\"{NsFirma}\">" +
                $"<des:peticionDescarga IdPaquete=\"{idPaquete}\" RfcSolicitante=\"{rfc}\"/>" +
                $"</des:PeticionDescargaMasivaTercerosEntrada>";

            var (digestVal, sigVal) = FirmarNodo(nodoXml, rsa);

            return
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "xmlns:des=\"http://DescargaMasivaTerceros.sat.gob.mx\" " +
                "xmlns:xd=\"http://www.w3.org/2000/09/xmldsig#\">" +
                "<s:Header/><s:Body>" +
                "<des:PeticionDescargaMasivaTercerosEntrada>" +
                $"<des:peticionDescarga IdPaquete=\"{idPaquete}\" RfcSolicitante=\"{rfc}\">" +
                FirmaXml(certB64, issuer, serial, digestVal, sigVal) +
                "</des:peticionDescarga>" +
                "</des:PeticionDescargaMasivaTercerosEntrada>" +
                "</s:Body></s:Envelope>";
        }

        //Firma XML

        // el sat espera el número de serie como texto, no en hexadecimal
        private static string NumeroSerieLegible(X509Certificate2 cert)
        {
            var hex = cert.SerialNumber;
            if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0) return hex ?? string.Empty;

            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out bytes[i]))
                    return hex;
            }

            var texto = Encoding.ASCII.GetString(bytes);
            return texto.All(char.IsDigit) ? texto : hex;
        }

        private static (string DigestValue, string SignatureValue) FirmarNodo(string nodoXml, RSA rsa)
        {
            var digestVal = DigestC14N(nodoXml);

            var signedInfo =
                "<SignedInfo xmlns=\"http://www.w3.org/2000/09/xmldsig#\">" +
                "<CanonicalizationMethod Algorithm=\"http://www.w3.org/TR/2001/REC-xml-c14n-20010315\"/>" +
                "<SignatureMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#rsa-sha1\"/>" +
                "<Reference URI=\"\">" +
                "<Transforms><Transform Algorithm=\"http://www.w3.org/2000/09/xmldsig#enveloped-signature\"/></Transforms>" +
                "<DigestMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#sha1\"/>" +
                $"<DigestValue>{digestVal}</DigestValue>" +
                "</Reference></SignedInfo>";

            var sigBytes = rsa.SignData(
                CanonicalizarXml(signedInfo), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

            return (digestVal, Convert.ToBase64String(sigBytes));
        }

        private static string FirmaXml(
            string certB64, string issuer, string serial, string digestVal, string sigVal) =>
            "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\">" +
            "<SignedInfo>" +
            "<CanonicalizationMethod Algorithm=\"http://www.w3.org/TR/2001/REC-xml-c14n-20010315\"/>" +
            "<SignatureMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#rsa-sha1\"/>" +
            "<Reference URI=\"\">" +
            "<Transforms><Transform Algorithm=\"http://www.w3.org/2000/09/xmldsig#enveloped-signature\"/></Transforms>" +
            "<DigestMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#sha1\"/>" +
            $"<DigestValue>{digestVal}</DigestValue>" +
            "</Reference></SignedInfo>" +
            $"<SignatureValue>{sigVal}</SignatureValue>" +
            "<KeyInfo><X509Data>" +
            $"<X509IssuerSerial><X509IssuerName>{issuer}</X509IssuerName><X509SerialNumber>{serial}</X509SerialNumber></X509IssuerSerial>" +
            $"<X509Certificate>{certB64}</X509Certificate>" +
            "</X509Data></KeyInfo>" +
            "</Signature>";

        //Reintento

        // facturas sin conciliar; se excluyen los complementos (tipo P)
        public async Task<List<FacturaPendiente>> ObtenerFacturasPendientesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            var result = await conn.QueryAsync<FacturaPendiente>(
                @"SELECT Id, RFCEmisor, Monto, FechaFactura
          FROM ProvFacturasSAT
          WHERE IdEstadoFacturaSAT IN (@Pendiente, @NoConcilia)
            AND Estatus = 1
            AND ISNULL(TipoDeComprobante, @TipoIngreso) = @TipoIngreso",
                new
                {
                    Pendiente = FacturaEstadoPendiente,
                    NoConcilia = FacturaNoConcilia,
                    TipoIngreso = TipoComprobanteIngreso
                });
            return result.ToList();
        }

        public async Task<int?> ObtenerIdDocumentoPorFacturaAsync(int idFactura)
        {
            using var conn = new SqlConnection(_connectionString);
            return await conn.QueryFirstOrDefaultAsync<int?>(
                @"SELECT Id FROM OperDocumentosXTipo
                WHERE IdTiposOper = 1
                  AND IdTiposDocumentoOper = 1
                  AND Estatus = 1
                  AND Id IN (
                      SELECT r.IdOperDocumentosXTipo
                      FROM OperDocumentosXTipoProvCXP r
                      INNER JOIN ProvCalendarioPagos cal ON cal.Id = r.IdProvCalendarioPagos
                      INNER JOIN ProvCXP cxp ON cxp.Id = cal.IdProvCxP
                      WHERE cxp.IdProveedor IN (
                          SELECT pv.Id FROM PersProveedores pv
                          INNER JOIN PersPersonas pp ON pp.Id = pv.IdPersPersona
                          INNER JOIN ProvFacturasSAT f ON f.RFCReceptor = pp.Rfc
                          WHERE f.Id = @IdFactura
                    )
            )",
                new { IdFactura = idFactura });
        }

        //Helpers

        private (X509Certificate2 Cert, RSA Rsa) CargarFiel()
        {
            var cert = new X509Certificate2(File.ReadAllBytes(_rutaCer));
            var rsa = RSA.Create();
            rsa.ImportEncryptedPkcs8PrivateKey(
                Encoding.UTF8.GetBytes(_contrasena),
                File.ReadAllBytes(_rutaKey), out _);
            return (cert, rsa);
        }

        private async Task<string> EnviarAsync(
            string url, string action, string token, string xml, CancellationToken ct)
        {
            var contenido = new StringContent(xml, Encoding.UTF8, "text/xml");
            contenido.Headers.Add("SOAPAction", $"\"{action}\"");

            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Add("Authorization", $"WRAP access_token=\"{token}\"");

            var resp = await _http.PostAsync(url, contenido, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("SAT respondió error en {Url}: {Body}", url, body);
                throw new Exception($"Error SAT ({(int)resp.StatusCode}): {body}");
            }

            return body;
        }

        private static string DigestC14N(string xml)
        {
            var bytes = CanonicalizarXml(xml);
            return Convert.ToBase64String(SHA1.HashData(bytes));
        }

        private static byte[] CanonicalizarXml(string xml)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
            var t = new XmlDsigC14NTransform();
            t.LoadInput(doc);
            using var stream = (MemoryStream)t.GetOutput(typeof(Stream));
            return stream.ToArray();
        }

        private static string ExtraerNodo(string xmlRespuesta, string ns, string xpath)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlRespuesta);
            var mgr = new XmlNamespaceManager(doc.NameTable);
            mgr.AddNamespace("des", ns);
            var nodo = doc.SelectSingleNode($"//{xpath.Replace("/", "/des:")}", mgr)
                       ?? doc.SelectSingleNode($"//des:{xpath.Split('/').Last()}", mgr);
            if (nodo is null || string.IsNullOrWhiteSpace(nodo.InnerText))
                throw new Exception($"No se encontró '{xpath}' en la respuesta SAT: {xmlRespuesta}");
            return nodo.InnerText.Trim();
        }

        private static string ExtraerAtributo(
            string xmlRespuesta, string ns, string nombreNodo, string nombreAtributo)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlRespuesta);
            var mgr = new XmlNamespaceManager(doc.NameTable);
            mgr.AddNamespace("des", ns);
            var nodo = doc.SelectSingleNode($"//des:{nombreNodo}", mgr);
            if (nodo is null)
                throw new Exception($"No se encontró el nodo '{nombreNodo}' en la respuesta SAT.");
            var valor = nodo.Attributes?[nombreAtributo]?.Value;
            if (string.IsNullOrWhiteSpace(valor))
                throw new Exception($"No se encontró el atributo '{nombreAtributo}' en '{nombreNodo}'.");
            return valor;
        }

        private static ResultadoVerificacion ExtraerResultadoVerificacion(string xmlRespuesta)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlRespuesta);
            var mgr = new XmlNamespaceManager(doc.NameTable);
            mgr.AddNamespace("des", "http://DescargaMasivaTerceros.sat.gob.mx");

            var resultado = new ResultadoVerificacion();
            var nodo = doc.SelectSingleNode("//des:VerificaSolicitudDescargaResult", mgr);
            if (nodo is null) return resultado;

            if (int.TryParse(nodo.Attributes?["EstadoSolicitud"]?.Value, out var estatus))
                resultado.Estatus = (EstatusSolicitudSat)estatus;

            resultado.CodigoEstadoSolicitud = nodo.Attributes?["CodigoEstadoSolicitud"]?.Value ?? string.Empty;
            resultado.MensajeSat = nodo.Attributes?["Mensaje"]?.Value ?? string.Empty;

            var nodosPaquetes = doc.SelectNodes("//des:IdsPaquetes", mgr);
            if (nodosPaquetes != null)
                foreach (XmlNode p in nodosPaquetes)
                    resultado.IdsPaquetes.Add(p.InnerText.Trim());

            return resultado;
        }

    }
}
