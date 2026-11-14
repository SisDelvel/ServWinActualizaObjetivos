using ServWinDescargaMasivaSAT.Models;
using ServWinDescargaMasivaSAT.Services;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;

namespace ServWinDescargaMasivaSAT
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IRepositorioSatAuth _auth;
        private readonly IRepositorioSatDescarga _descarga;
        private readonly IRepositorioSharePoint _sharePoint;
        private readonly IRepositorioCxP _cxp;
        private readonly IConfiguration _config;

        // qué hace el ciclo después de una vuelta
        private enum ResultadoCiclo
        {
            // sin días pendientes: dormir hasta las 2 am
            AlDia,
            // trabajo en curso: volver pronto
            Continuar,
            // error: reintentar más tarde
            Reintentar
        }

        // días hacia atrás que se revisan
        private const int DiasRecuperacionPorDefecto = 7;

        // solicitudes fallidas permitidas por día
        private const int MaxSolicitudesFallidasPorDia = 3;

        // reintentos de paquetes por solicitud
        private const int MaxIntentosPaquetes = 3;

        // en memoria: al reiniciar empieza de cero
        private readonly Dictionary<int, int> _intentosPorSolicitud = new();

        public Worker(ILogger<Worker> logger, IRepositorioSatAuth auth, IRepositorioSatDescarga descarga, IRepositorioSharePoint sharePoint, IRepositorioCxP cxp, IConfiguration config)
        {
            _logger = logger;
            _auth = auth;
            _descarga = descarga;
            _sharePoint = sharePoint;
            _cxp = cxp;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Inicia Servicio Descarga Masiva SAT");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReintentarPendientesAsync(stoppingToken);
                    var resultado = await ProcesarAsync(stoppingToken);

                    TimeSpan espera;
                    switch (resultado)
                    {
                        case ResultadoCiclo.AlDia:
                            var manana2am = DateTime.Today.AddDays(1).AddHours(2);
                            espera = manana2am - DateTime.Now;
                            _logger.LogInformation("Descarga al día. Próxima ejecución: {Fecha}", manana2am);
                            break;

                        case ResultadoCiclo.Reintentar:
                            espera = TimeSpan.FromMinutes(30);
                            _logger.LogInformation("Reintentando en 30 minutos...");
                            break;

                        default:
                            espera = TimeSpan.FromMinutes(5);
                            _logger.LogInformation("Revisando de nuevo en 5 minutos...");
                            break;
                    }

                    try { await Task.Delay(espera, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en ciclo principal: {Mensaje}", ex.Message);
                    try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("---- Servicio detenido ----");
        }


        private async Task ReintentarPendientesAsync(CancellationToken ct)
        {
            var pendientes = await _descarga.ObtenerFacturasPendientesAsync();

            if (!pendientes.Any())
            {
                _logger.LogInformation("Sin facturas pendientes de días anteriores.");
                return;
            }

            _logger.LogInformation("Reintentando conciliación de {Total} factura(s) pendiente(s)...",
                pendientes.Count);

            var conciliadas = 0;
            foreach (var factura in pendientes)
            {
                try
                {
                    var idProveedor = await _cxp.BuscarProveedorPorRfcAsync(factura.RfcEmisor);

                    if (idProveedor is null)
                    {
                        continue;
                    }

                    var coincidencias = await _cxp.BuscarCxpPorProveedorYMontoAsync(idProveedor.Value, factura.Monto);

                    if (coincidencias.Count == 0)
                    {
                        await _descarga.ActualizarEstadoFacturaAsync(
                            factura.Id, RepositorioSatDescarga.FacturaNoConcilia);
                        _logger.LogInformation("Factura {Id} — No Concilia", factura.Id);
                        continue;
                    }

                    // con una o varias coincidencias sólo se registran como candidatas; la confirmación es manual
                    foreach (var cxp in coincidencias)
                    {
                        await _cxp.InsertarCandidatoConciliacionAsync(
                            factura.Id, cxp.Id, cxp.ImporteTotal, factura.Monto);
                    }

                    var nuevoEstado = coincidencias.Count == 1
                        ? RepositorioSatDescarga.FacturaConcilia
                        : RepositorioSatDescarga.FacturaConciliaMuchas;

                    await _descarga.ActualizarEstadoFacturaAsync(factura.Id, nuevoEstado);

                    _logger.LogInformation(
                        "Factura {Id} — {Estado} ({Total} candidato(s), pendiente de confirmación manual)",
                        factura.Id,
                        nuevoEstado == RepositorioSatDescarga.FacturaConcilia ? "Concilia" : "Concilia Muchas",
                        coincidencias.Count);

                    conciliadas++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error reintentando factura {Id}: {Mensaje}",
                        factura.Id, ex.Message);
                }
            }

            _logger.LogInformation("Reintento completado. {Conciliadas}/{Total} conciliadas.",
                conciliadas, pendientes.Count);
        }


        // sigue la solicitud en curso o pide el día más antiguo pendiente
        private async Task<ResultadoCiclo> ProcesarAsync(CancellationToken ct)
        {
            var rfc = _config["SatFiel:Rfc"]!;

            var solicitudActiva = await _descarga.ObtenerSolicitudActivaAsync();

            if (solicitudActiva is null)
                return await SolicitarSiguienteDiaAsync(rfc, ct);

            _logger.LogInformation("Retomando solicitud Id={Id} del {Dia:d} NumPeticion={NumPeticion}",
                solicitudActiva.Id, solicitudActiva.FechaSolicitud, solicitudActiva.NumPeticionSat);

            var verificacion = await _descarga.VerificarSolicitudAsync(rfc, solicitudActiva.NumPeticionSat, ct);

            _logger.LogInformation("Estatus SAT: {Estatus} (código {Codigo})",
                verificacion.Estatus, verificacion.CodigoEstadoSolicitud);

            if (verificacion.Estatus == EstatusSolicitudSat.Aceptada ||
                verificacion.Estatus == EstatusSolicitudSat.EnProceso)
            {
                _logger.LogInformation("SAT aún procesando.");
                return ResultadoCiclo.Continuar;
            }

            // 5004: día sin comprobantes, no es error
            var sinInformacion =
                verificacion.CodigoEstadoSolicitud == RepositorioSatDescarga.CodigoSinInformacion ||
                (verificacion.Estatus == EstatusSolicitudSat.Terminada && verificacion.IdsPaquetes.Count == 0);

            if (sinInformacion)
            {
                _logger.LogInformation("El {Dia:d} no tiene comprobantes recibidos.", solicitudActiva.FechaSolicitud);
                await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, RepositorioSatDescarga.EstatusTerminada);
                return ResultadoCiclo.Continuar;
            }

            if (verificacion.Estatus != EstatusSolicitudSat.Terminada)
            {
                // se marca fallida y se vuelve a pedir el día
                _logger.LogWarning("Solicitud del {Dia:d} terminó como {Estatus} — {Mensaje}. Se volverá a pedir.",
                    solicitudActiva.FechaSolicitud, verificacion.Estatus, verificacion.MensajeSat);
                await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, RepositorioSatDescarga.EstatusFallida);
                return ResultadoCiclo.Continuar;
            }

            _logger.LogInformation("Solicitud lista. Procesando {Total} paquete(s)...", verificacion.IdsPaquetes.Count);

            var totalXmls = 0;
            var paquetesConError = 0;
            foreach (var idPaquete in verificacion.IdsPaquetes)
            {
                try
                {
                    var zipBytes = await _descarga.DescargarPaqueteAsync(rfc, idPaquete, ct);
                    _logger.LogInformation("Paquete {Id} descargado ({Kb} KB)", idPaquete, zipBytes.Length / 1024);

                    var (procesados, errores) = await ProcesarZipAsync(zipBytes, solicitudActiva.Id, solicitudActiva.FechaSolicitud, ct);
                    totalXmls += procesados;

                    if (errores > 0)
                    {
                        paquetesConError++;
                        _logger.LogWarning("Paquete {Id}: {Errores} XML(s) con error.", idPaquete, errores);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    paquetesConError++;
                    _logger.LogError(ex, "Error procesando paquete {Id}: {Mensaje}", idPaquete, ex.Message);
                }
            }

            if (paquetesConError == 0)
            {
                await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, RepositorioSatDescarga.EstatusTerminada);
                _intentosPorSolicitud.Remove(solicitudActiva.Id);
                _logger.LogInformation("---- Día {Dia:d} completado. {Total} XML(s) procesados ----",
                    solicitudActiva.FechaSolicitud, totalXmls);
                return ResultadoCiclo.Continuar;
            }

            // la solicitud sigue en proceso para reintentar los paquetes
            var intentos = _intentosPorSolicitud.GetValueOrDefault(solicitudActiva.Id) + 1;
            _intentosPorSolicitud[solicitudActiva.Id] = intentos;

            if (intentos >= MaxIntentosPaquetes)
            {
                _logger.LogError(
                    "Solicitud {Id} del {Dia:d}: {Fallidos} paquete(s) siguen fallando tras {Intentos} intentos. Se marca Fallida y se volverá a pedir el día.",
                    solicitudActiva.Id, solicitudActiva.FechaSolicitud, paquetesConError, intentos);
                await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, RepositorioSatDescarga.EstatusFallida);
                _intentosPorSolicitud.Remove(solicitudActiva.Id);
                return ResultadoCiclo.Reintentar;
            }

            _logger.LogWarning(
                "Solicitud {Id} del {Dia:d}: {Fallidos} paquete(s) con error (intento {Intento} de {Max}).",
                solicitudActiva.Id, solicitudActiva.FechaSolicitud, paquetesConError, intentos, MaxIntentosPaquetes);
            return ResultadoCiclo.Reintentar;
        }

        // pide el día más antiguo sin descargar
        private async Task<ResultadoCiclo> SolicitarSiguienteDiaAsync(string rfc, CancellationToken ct)
        {
            var dia = await SiguienteDiaPorDescargarAsync();

            if (dia is null)
                return ResultadoCiclo.AlDia;

            var fechaInicio = dia.Value;
            var fechaFin = dia.Value.AddDays(1).AddSeconds(-1);

            _logger.LogInformation("Solicitando facturas del {Fecha:d}...", fechaInicio);

            try
            {
                var idSolicitudSat = await _descarga.SolicitarDescargaRecibidosAsync(rfc, fechaInicio, fechaFin, ct);
                var idBd = await _descarga.InsertarSolicitudAsync(idSolicitudSat, fechaInicio);

                _logger.LogInformation("Solicitud creada. IdSAT={IdSat} IdBD={IdBd}", idSolicitudSat, idBd);
                return ResultadoCiclo.Continuar;
            }
            catch (SatRechazoException ex)
            {
                // cuenta como intento fallido del día
                _logger.LogWarning("El SAT rechazó la solicitud del {Fecha:d}: {Mensaje}", fechaInicio, ex.Message);
                await _descarga.InsertarSolicitudAsync(
                    $"RECHAZADA-{ex.CodEstatus}", fechaInicio, RepositorioSatDescarga.EstatusFallida);
                return ResultadoCiclo.Reintentar;
            }
        }

        private async Task<DateTime?> SiguienteDiaPorDescargarAsync()
        {
            var diasRecuperacion = Math.Max(1,
                _config.GetValue("DescargaSat:DiasRecuperacion", DiasRecuperacionPorDefecto));

            var ayer = DateTime.Today.AddDays(-1);
            var desde = ayer.AddDays(-(diasRecuperacion - 1));

            var solicitudes = await _descarga.ObtenerSolicitudesDesdeAsync(desde);

            for (var dia = desde; dia <= ayer; dia = dia.AddDays(1))
            {
                var delDia = solicitudes.Where(s => s.FechaSolicitud.Date == dia).ToList();

                if (delDia.Any(s => s.IdEstatusSolicitudSat == RepositorioSatDescarga.EstatusTerminada))
                    continue;

                var fallidas = delDia.Count(s => s.IdEstatusSolicitudSat == RepositorioSatDescarga.EstatusFallida);
                if (fallidas >= MaxSolicitudesFallidasPorDia)
                {
                    _logger.LogWarning(
                        "El {Dia:d} falló {Veces} veces y ya no se vuelve a pedir. Revisar a mano.", dia, fallidas);
                    continue;
                }

                return dia;
            }

            return null;
        }

        private async Task<(int Procesados, int Errores)> ProcesarZipAsync(byte[] zipBytes, int idSolicitud, DateTime fechaSolicitud, CancellationToken ct)
        {
            var contador = 0;
            var omitidos = 0;
            var errores = 0;

            using var zipStream = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entrada in zip.Entries)
            {
                if (!entrada.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var entradaStream = entrada.Open();
                    using var ms = new MemoryStream();
                    await entradaStream.CopyToAsync(ms, ct);
                    var contenido = ms.ToArray();

                    var cfdi = ExtraerDatosCfdi(contenido);

                    if (string.IsNullOrWhiteSpace(cfdi.Uuid))
                    {
                        _logger.LogWarning("XML {Archivo} sin UUID legible. Se omite.", entrada.Name);
                        omitidos++;
                        continue;
                    }

                    var nombreArchivo = $"{cfdi.Uuid}.xml";

                    // fecha del comprobante; si no viene, la de la solicitud
                    var fechaFactura = cfdi.Fecha ?? fechaSolicitud;

                    var esComplemento = cfdi.TipoDeComprobante.Equals(
                        RepositorioSatDescarga.TipoComprobantePago, StringComparison.OrdinalIgnoreCase);
                    var esFactura = cfdi.TipoDeComprobante.Equals(
                        RepositorioSatDescarga.TipoComprobanteIngreso, StringComparison.OrdinalIgnoreCase);

                    // todo xml va a sharepoint con su tipo de documento
                    var (_, uniqueId, url) = await _sharePoint.SubirXmlAsync(
                        contenido, nombreArchivo, fechaFactura,
                        esComplemento ? TiposDocumentoOper.ComplementoXml : TiposDocumentoOper.FacturaXml,
                        ct);

                    // se registran facturas (I) y complementos (P); las notas de crédito (E) sólo van a sharepoint
                    if (!esFactura && !esComplemento)
                    {
                        _logger.LogInformation(
                            "UUID={Uuid} es tipo '{Tipo}'. Guardado en SharePoint y omitido de ProvFacturasSAT.",
                            cfdi.Uuid, cfdi.TipoDeComprobante);
                        omitidos++;
                        continue;
                    }

                    if (esComplemento)
                    {
                        await ProcesarComplementoAsync(cfdi, idSolicitud, fechaFactura);
                        contador++;
                        continue;
                    }

                    _logger.LogInformation("Procesando UUID={Uuid} RFC={Rfc} Total={Total} Fecha={Fecha:d} MetodoPago={Metodo}",
                        cfdi.Uuid, cfdi.RfcEmisor, cfdi.Total, fechaFactura, cfdi.MetodoPago ?? "—");

                    var idFactura = await _descarga.InsertarFacturaSatAsync(
                        idSolicitud, cfdi.Uuid, cfdi.RfcEmisor, cfdi.RfcReceptor,
                        RepositorioSatDescarga.FacturaEstadoPendiente,
                        cfdi.Total, fechaFactura,
                        RepositorioSatDescarga.TipoComprobanteIngreso, cfdi.MetodoPago);

                    // marca la cabecera como factura
                    await _descarga.RegistrarFacturaPueAsync(idFactura);

                    // la liga con la cxp se hace al confirmar desde la vista
                    await ConciliarAsync(idFactura, cfdi.RfcEmisor, cfdi.Total, fechaFactura, ct);

                    contador++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // se cuenta para reintentar el paquete
                    errores++;
                    _logger.LogError(ex, "Error procesando XML {Archivo}: {Mensaje}",
                        entrada.Name, ex.Message);
                }
            }

            if (omitidos > 0)
                _logger.LogInformation("{Omitidos} XML(s) omitidos de ProvFacturasSAT (no son factura ni complemento).", omitidos);

            // los complementos se resuelven al final del zip
            var ligados = await _descarga.ResolverFacturasDeComplementosAsync();
            if (ligados > 0)
                _logger.LogInformation("{Ligados} renglón(es) de complemento ligados a su factura.", ligados);

            return (contador, errores);
        }


        // registra el complemento: cabecera, ProvFacturasSATPPD y un renglón por factura pagada
        private async Task ProcesarComplementoAsync(DatosCfdi cfdi, int idSolicitud, DateTime fechaEmision)
        {
            var totalPagado = cfdi.Pagos.Sum(p => p.Doctos.Sum(d => d.ImpPagado));
            var totalDoctos = cfdi.Pagos.Sum(p => p.Doctos.Count);

            _logger.LogInformation(
                "Complemento UUID={Uuid} RFC={Rfc} Pagos={Pagos} Doctos={Doctos} Pagado={Pagado} Declarado={Declarado}",
                cfdi.Uuid, cfdi.RfcEmisor, cfdi.Pagos.Count, totalDoctos,
                totalPagado, cfdi.MontoTotalPagos);

            // si no cuadra con el xml sólo se avisa
            if (cfdi.MontoTotalPagos.HasValue &&
                Math.Abs(cfdi.MontoTotalPagos.Value - totalPagado) > 0.01m)
            {
                _logger.LogWarning(
                    "Complemento UUID={Uuid} NO cuadra: MontoTotalPagos={Declarado} pero los documentos suman {Pagado}.",
                    cfdi.Uuid, cfdi.MontoTotalPagos, totalPagado);
            }

            foreach (var pago in cfdi.Pagos)
            {
                var sumaPago = pago.Doctos.Sum(d => d.ImpPagado);
                if (pago.MontoPago.HasValue && Math.Abs(pago.MontoPago.Value - sumaPago) > 0.01m)
                {
                    _logger.LogWarning(
                        "Complemento UUID={Uuid} pago {NumPago} NO cuadra: Monto={Monto} pero sus documentos suman {Suma}.",
                        cfdi.Uuid, pago.NumPago, pago.MontoPago, sumaPago);
                }
            }

            var idCabecera = await _descarga.InsertarFacturaSatAsync(
                idSolicitud, cfdi.Uuid, cfdi.RfcEmisor, cfdi.RfcReceptor,
                RepositorioSatDescarga.FacturaEstadoPendiente,
                0m, fechaEmision,
                RepositorioSatDescarga.TipoComprobantePago, null);

            var idPpd = await _descarga.RegistrarComplementoPpdAsync(idCabecera, cfdi.MontoTotalPagos);

            if (totalDoctos == 0)
            {
                _logger.LogWarning(
                    "Complemento UUID={Uuid} sin documentos relacionados legibles. Queda registrado sin renglones.",
                    cfdi.Uuid);
                return;
            }

            foreach (var pago in cfdi.Pagos)
            {
                foreach (var docto in pago.Doctos)
                {
                    if (string.IsNullOrWhiteSpace(docto.IdDocumento))
                    {
                        _logger.LogWarning(
                            "Complemento UUID={Uuid} pago {NumPago}: DoctoRelacionado sin IdDocumento. Se omite el renglón.",
                            cfdi.Uuid, pago.NumPago);
                        continue;
                    }

                    await _descarga.InsertarDoctoRelacionadoAsync(idPpd, pago, docto);
                }
            }
        }


        private async Task ConciliarAsync(int idFactura, string rfcEmisor, decimal total, DateTime fechaFactura, CancellationToken ct)
        {
            var idProveedor = await _cxp.BuscarProveedorPorRfcAsync(rfcEmisor);

            if (idProveedor is null)
            {
                _logger.LogInformation("RFC={Rfc} sin proveedor. Factura queda Pendiente.", rfcEmisor);
                return;
            }

            var coincidencias = await _cxp.BuscarCxpPorProveedorYMontoAsync(idProveedor.Value, total);

            if (coincidencias.Count == 0)
            {
                await _descarga.ActualizarEstadoFacturaAsync(idFactura, RepositorioSatDescarga.FacturaNoConcilia);
                _logger.LogInformation("Factura {Id} — No Concilia", idFactura);
                return;
            }

            // con una o varias coincidencias sólo se registran como candidatas; la confirmación es manual
            foreach (var cxp in coincidencias)
            {
                await _cxp.InsertarCandidatoConciliacionAsync(idFactura, cxp.Id, cxp.ImporteTotal, total);
            }

            var nuevoEstado = coincidencias.Count == 1
                ? RepositorioSatDescarga.FacturaConcilia
                : RepositorioSatDescarga.FacturaConciliaMuchas;

            await _descarga.ActualizarEstadoFacturaAsync(idFactura, nuevoEstado);

            _logger.LogInformation(
                "Factura {Id} — {Estado} ({Total} candidato(s), pendiente de confirmación manual)",
                idFactura,
                nuevoEstado == RepositorioSatDescarga.FacturaConcilia ? "Concilia" : "Concilia Muchas",
                coincidencias.Count);
        }


        private DatosCfdi ExtraerDatosCfdi(byte[] contenido)
        {
            var doc = new XmlDocument();
            doc.Load(new MemoryStream(contenido));

            // por nombre local para servir en cfd/3 y cfd/4
            var comprobante = doc.SelectSingleNode("/*[local-name()='Comprobante']");
            var emisor = doc.SelectSingleNode("//*[local-name()='Emisor']");
            var receptor = doc.SelectSingleNode("//*[local-name()='Receptor']");
            var timbre = doc.SelectSingleNode("//*[local-name()='TimbreFiscalDigital']");

            return new DatosCfdi
            {
                Uuid = timbre?.Attributes?["UUID"]?.Value ?? string.Empty,
                RfcEmisor = emisor?.Attributes?["Rfc"]?.Value ?? string.Empty,
                RfcReceptor = receptor?.Attributes?["Rfc"]?.Value ?? string.Empty,
                TipoDeComprobante = comprobante?.Attributes?["TipoDeComprobante"]?.Value ?? string.Empty,
                // sólo en cfdi de ingreso
                MetodoPago = comprobante?.Attributes?["MetodoPago"]?.Value,
                Total = ADecimal(comprobante?.Attributes?["Total"]?.Value),
                Fecha = AFecha(comprobante?.Attributes?["Fecha"]?.Value),
                Pagos = ExtraerPagos(doc),
                // MontoTotalPagos del complemento
                MontoTotalPagos = ADecimalNulo(
                    doc.SelectSingleNode("//*[local-name()='Totales']")?
                       .Attributes?["MontoTotalPagos"]?.Value)
            };
        }

        // lee los nodos Pago y sus DoctoRelacionado; el índice del nodo es el NumPago
        private static List<PagoCfdi> ExtraerPagos(XmlDocument doc)
        {
            var pagos = new List<PagoCfdi>();
            var nodos = doc.SelectNodes("//*[local-name()='Pagos']/*[local-name()='Pago']");

            if (nodos is null) return pagos;

            var numPago = 0;
            foreach (XmlNode nodo in nodos)
            {
                numPago++;
                var pago = new PagoCfdi
                {
                    NumPago = numPago,
                    FechaPago = AFecha(nodo.Attributes?["FechaPago"]?.Value) ?? DateTime.MinValue,
                    FormaDePagoP = nodo.Attributes?["FormaDePagoP"]?.Value,
                    NumOperacion = nodo.Attributes?["NumOperacion"]?.Value,
                    MonedaP = nodo.Attributes?["MonedaP"]?.Value,
                    TipoCambioP = ADecimalNulo(nodo.Attributes?["TipoCambioP"]?.Value),
                    MontoPago = ADecimalNulo(nodo.Attributes?["Monto"]?.Value)
                };

                foreach (XmlNode dr in nodo.SelectNodes("*[local-name()='DoctoRelacionado']")!)
                {
                    pago.Doctos.Add(new DoctoRelacionadoCfdi
                    {
                        IdDocumento = dr.Attributes?["IdDocumento"]?.Value ?? string.Empty,
                        NumParcialidad = AEntero(dr.Attributes?["NumParcialidad"]?.Value),
                        ImpSaldoAnt = ADecimalNulo(dr.Attributes?["ImpSaldoAnt"]?.Value),
                        ImpPagado = ADecimal(dr.Attributes?["ImpPagado"]?.Value),
                        ImpSaldoInsoluto = ADecimalNulo(dr.Attributes?["ImpSaldoInsoluto"]?.Value),
                        MonedaDR = dr.Attributes?["MonedaDR"]?.Value,
                        EquivalenciaDR = ADecimalNulo(dr.Attributes?["EquivalenciaDR"]?.Value)
                    });
                }

                pagos.Add(pago);
            }

            return pagos;
        }

        private static decimal ADecimal(string? valor)
        {
            decimal.TryParse(valor,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var resultado);
            return resultado;
        }

        private static decimal? ADecimalNulo(string? valor)
        {
            return decimal.TryParse(valor,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var resultado)
                ? resultado
                : null;
        }

        private static int? AEntero(string? valor)
        {
            return int.TryParse(valor,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var resultado)
                ? resultado
                : null;
        }

        private static DateTime? AFecha(string? valor)
        {
            return DateTime.TryParse(valor,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var resultado)
                ? resultado
                : null;
        }

        private sealed class DatosCfdi
        {
            public string Uuid { get; init; } = string.Empty;
            public string RfcEmisor { get; init; } = string.Empty;
            public string RfcReceptor { get; init; } = string.Empty;
            public string TipoDeComprobante { get; init; } = string.Empty;
            public string? MetodoPago { get; init; }
            public decimal Total { get; init; }
            public DateTime? Fecha { get; init; }

            // sólo en complementos (tipo P)
            public List<PagoCfdi> Pagos { get; init; } = new();

            // MontoTotalPagos, sólo en tipo P
            public decimal? MontoTotalPagos { get; init; }
        }
    }
}