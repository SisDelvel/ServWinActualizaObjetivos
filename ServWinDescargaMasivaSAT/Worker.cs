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

        public Worker(ILogger<Worker> logger,IRepositorioSatAuth auth,IRepositorioSatDescarga descarga,IRepositorioSharePoint sharePoint,IRepositorioCxP cxp,IConfiguration config)
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
                    var completado = await ProcesarAsync(stoppingToken);

                    if (completado)
                    {
                        var ahora = DateTime.Now;
                        var manana2am = ahora.Date.AddDays(1).AddHours(2);
                        var espera = manana2am - ahora;
                        _logger.LogInformation("Proceso completado. Próxima ejecución: {Fecha}", manana2am);
                        try { await Task.Delay(espera, stoppingToken); }
                        catch (OperationCanceledException) { break; }
                    }
                    else
                    {
                        _logger.LogInformation("SAT aún procesando. Reintentando en 30 minutos...");
                        try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); }
                        catch (OperationCanceledException) { break; }
                    }
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

                    var idDocumento = await _descarga.ObtenerIdDocumentoPorFacturaAsync(factura.Id);

                    var coincidencias = await _cxp.BuscarCxpPorProveedorYMontoAsync(idProveedor.Value, factura.Monto);

                    if (coincidencias.Count == 0)
                    {
                        await _descarga.ActualizarEstadoFacturaAsync(
                            factura.Id, RepositorioSatDescarga.FacturaNoConcilia);
                        _logger.LogInformation("Factura {Id} — No Concilia", factura.Id);
                    }
                    else if (coincidencias.Count == 1)
                    {
                        await _descarga.ActualizarEstadoFacturaAsync(
                            factura.Id, RepositorioSatDescarga.FacturaConcilia);
                        if (idDocumento.HasValue)
                            await _cxp.InsertarDocumentoProvCxpAsync(idDocumento.Value, coincidencias[0].Id);
                        _logger.LogInformation("Factura {Id} — Concilia con CxP {IdCxp}",
                            factura.Id, coincidencias[0].Id);
                        conciliadas++;
                    }
                    else
                    {
                        await _descarga.ActualizarEstadoFacturaAsync(
                            factura.Id, RepositorioSatDescarga.FacturaConciliaMuchas);
                        if (idDocumento.HasValue)
                            foreach (var cxp in coincidencias)
                                await _cxp.InsertarDocumentoProvCxpAsync(idDocumento.Value, cxp.Id);
                        _logger.LogInformation("Factura {Id} — Concilia Muchas ({Total} CxP)",
                            factura.Id, coincidencias.Count);
                        conciliadas++;
                    }
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


        private async Task<bool> ProcesarAsync(CancellationToken ct)
        {
            var rfc = _config["SatFiel:Rfc"]!;

            var solicitudActiva = await _descarga.ObtenerSolicitudActivaAsync();

            if (solicitudActiva is null)
            {
                var fechaInicio = DateTime.Today.AddDays(-1).Date;
                var fechaFin = DateTime.Today.AddDays(-1).Date.AddHours(23).AddMinutes(59).AddSeconds(59);

                _logger.LogInformation("Solicitando facturas del {Fecha}...", fechaInicio.ToString("d"));

                var idSolicitudSat = await _descarga.SolicitarDescargaRecibidosAsync(rfc, fechaInicio, fechaFin, ct);
                var idBd = await _descarga.InsertarSolicitudAsync(idSolicitudSat, fechaInicio);

                _logger.LogInformation("Solicitud creada. IdSAT={IdSat} IdBD={IdBd}", idSolicitudSat, idBd);
                return false; 
            }

            _logger.LogInformation("Retomando solicitud Id={Id} NumPeticion={NumPeticion}",
                solicitudActiva.Id, solicitudActiva.NumPeticionSat);

            var verificacion = await _descarga.VerificarSolicitudAsync(rfc, solicitudActiva.NumPeticionSat, ct);

            _logger.LogInformation("Estatus SAT: {Estatus}", verificacion.Estatus);

            if (verificacion.Estatus == EstatusSolicitudSat.Aceptada ||
                verificacion.Estatus == EstatusSolicitudSat.EnProceso)
            {
                _logger.LogInformation("SAT aún procesando.");
                return false; 
            }

            if (verificacion.Estatus != EstatusSolicitudSat.Terminada)
            {
                _logger.LogWarning("Solicitud terminó con estatus inesperado: {Estatus} — {Mensaje}",
                    verificacion.Estatus, verificacion.MensajeSat);
                await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, (int)verificacion.Estatus);
                return true; 
            }

            await _descarga.ActualizarEstatusSolicitudAsync(solicitudActiva.Id, RepositorioSatDescarga.EstatusTerminada);

            _logger.LogInformation("Solicitud lista. Procesando {Total} paquete(s)...", verificacion.IdsPaquetes.Count);

            var totalXmls = 0;
            foreach (var idPaquete in verificacion.IdsPaquetes)
            {
                try
                {
                    var zipBytes = await _descarga.DescargarPaqueteAsync(rfc, idPaquete, ct);
                    _logger.LogInformation("Paquete {Id} descargado ({Kb} KB)", idPaquete, zipBytes.Length / 1024);

                    var xmlsSubidos = await ProcesarZipAsync(zipBytes, solicitudActiva.Id, solicitudActiva.FechaSolicitud, ct);
                    totalXmls += xmlsSubidos;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando paquete {Id}: {Mensaje}", idPaquete, ex.Message);
                }
            }

            _logger.LogInformation("---- Proceso completado. {Total} XML(s) procesados ----", totalXmls);
            return true;
        }

        private async Task<int> ProcesarZipAsync(byte[] zipBytes, int idSolicitud, DateTime fechaFactura, CancellationToken ct)
        {
            var contador = 0;

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

                    var (uuid, rfcEmisor, rfcReceptor, total) = ExtraerDatosCfdi(contenido);
                    var nombreArchivo = $"{uuid}.xml";

                    _logger.LogInformation("Procesando UUID={Uuid} RFC={Rfc} Total={Total}",
                        uuid, rfcReceptor, total);

                    var idFactura = await _descarga.InsertarFacturaSatAsync(
                        idSolicitud, uuid, rfcEmisor, rfcReceptor,
                        RepositorioSatDescarga.FacturaEstadoPendiente
                        , total, fechaFactura);

                    var (idDocumento, uniqueId, url) = await _sharePoint.SubirXmlAsync(
                        contenido, nombreArchivo, fechaFactura, ct);

                    await ConciliarAsync(idFactura, idDocumento, rfcEmisor, total, fechaFactura, ct);

                    contador++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando XML {Archivo}: {Mensaje}",
                        entrada.Name, ex.Message);
                }
            }

            return contador;
        }


        private async Task ConciliarAsync(int idFactura, int idDocumento, string rfcEmisor, decimal total, DateTime fechaFactura, CancellationToken ct)
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
            }
            else if (coincidencias.Count == 1)
            {
                await _descarga.ActualizarEstadoFacturaAsync(idFactura, RepositorioSatDescarga.FacturaConcilia);

                var idCalendario = await _cxp.ObtenerOCrearCalendarioPagoAsync(
                    coincidencias[0].Id, total, fechaFactura);

                if (idCalendario.HasValue)
                    await _cxp.InsertarDocumentoProvCxpAsync(idDocumento, idCalendario.Value);

                _logger.LogInformation("Factura {Id} — Concilia con CxP {IdCxp}",
                    idFactura, coincidencias[0].Id);
            }
            else
            {
                await _descarga.ActualizarEstadoFacturaAsync(idFactura, RepositorioSatDescarga.FacturaConciliaMuchas);

                foreach (var cxp in coincidencias)
                {
                    var idCalendario = await _cxp.ObtenerOCrearCalendarioPagoAsync(
                        cxp.Id, total, fechaFactura);

                    if (idCalendario.HasValue)
                        await _cxp.InsertarDocumentoProvCxpAsync(idDocumento, idCalendario.Value);
                }

                _logger.LogInformation("Factura {Id} — Concilia Muchas ({Total} CxP)",
                    idFactura, coincidencias.Count);
            }
        }


        private (string Uuid, string RfcEmisor, string RfcReceptor, decimal Total)ExtraerDatosCfdi(byte[] contenido)
        {
            var doc = new XmlDocument();
            doc.Load(new MemoryStream(contenido));

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("cfdi", "http://www.sat.gob.mx/cfd/4");
            ns.AddNamespace("tfd", "http://www.sat.gob.mx/TimbreFiscalDigital");

            var comprobante = doc.SelectSingleNode("//cfdi:Comprobante", ns);
            var emisor = doc.SelectSingleNode("//cfdi:Emisor", ns);
            var receptor = doc.SelectSingleNode("//cfdi:Receptor", ns);
            var timbre = doc.SelectSingleNode("//tfd:TimbreFiscalDigital", ns);

            var uuid = timbre?.Attributes?["UUID"]?.Value ?? string.Empty;
            var rfcEmisor = emisor?.Attributes?["Rfc"]?.Value ?? string.Empty;
            var rfcReceptor = receptor?.Attributes?["Rfc"]?.Value ?? string.Empty;
            var totalStr = comprobante?.Attributes?["Total"]?.Value ?? "0";

            decimal.TryParse(totalStr,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var total);

            return (uuid, rfcEmisor, rfcReceptor, total);
        }
    }
}
