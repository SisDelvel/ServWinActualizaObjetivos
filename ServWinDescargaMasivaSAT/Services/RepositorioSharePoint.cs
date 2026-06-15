using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.Extensions.Options;
using ServWinDescargaMasivaSAT.Models;
using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ServWinDescargaMasivaSAT.Services
{
    public interface IRepositorioSharePoint
    {
        Task<(int IdDocumento, string UniqueId, string Url)> SubirXmlAsync(byte[] contenido, string nombreArchivo, DateTime fecha, CancellationToken ct = default);
    }
    internal class RepositorioSharePoint:IRepositorioSharePoint
    {
        private const int TipoDocumentoFacturaXml = 1;
        private const int TipoOperCxp = 1;

        private readonly HttpClient _http;
        private readonly SharePointConfig _config;
        private readonly ILogger<RepositorioSharePoint> _logger;
        private readonly string _connectionString;

        private string? _token;
        private DateTime _expiracionToken = DateTime.MinValue;

        public RepositorioSharePoint(
            HttpClient http,
            IOptions<SharePointConfig> config,
            ILogger<RepositorioSharePoint> logger,
            IConfiguration configuration)
        {
            _http = http;
            _config = config.Value;
            _logger = logger;
            _connectionString = configuration["ConnectionStrings:Geisha"]!;
        }

        public async Task<(int IdDocumento, string UniqueId, string Url)> SubirXmlAsync(
            byte[] contenido, string nombreArchivo, DateTime fecha, CancellationToken ct = default)
        {
            var token = await ObtenerTokenAsync(ct);

            var ruta = $"FacturasSat/{fecha:yyyy}/{fecha:MM}/{fecha:dd}/{nombreArchivo}";
            var url = $"https://graph.microsoft.com/v1.0/drives/{_config.DriveId}/root:/{ruta}:/content";

            _logger.LogInformation("Subiendo {Archivo} a SharePoint...", nombreArchivo);

            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(contenido);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

            var respuesta = await _http.SendAsync(request, ct);
            var body = await respuesta.Content.ReadAsStringAsync(ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("Error subiendo a SharePoint: {Body}", body);
                throw new Exception($"Error SharePoint ({(int)respuesta.StatusCode}): {body}");
            }

            var json = JsonDocument.Parse(body);

            var downloadUrl = json.RootElement
                .GetProperty("@microsoft.graph.downloadUrl")
                .GetString() ?? string.Empty;

            var uniqueId = System.Web.HttpUtility.ParseQueryString(
                new Uri(downloadUrl).Query)["UniqueId"] ?? string.Empty;

            _logger.LogInformation("Archivo subido. UniqueId={UniqueId}", uniqueId);

            var idDocumento = await InsertarDocumentoAsync(
                nombreArchivo, downloadUrl, uniqueId, "application/xml", contenido.Length);

            return (idDocumento, uniqueId, downloadUrl);
        }

        //BD

        private async Task<int> InsertarDocumentoAsync(
            string archivo, string url, string idSharePoint, string mimeType, long tamano)
        {
            using var conn = new SqlConnection(_connectionString);
            var id = await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO OperDocumentosXTipo
                (IdTiposDocumentoOper, IdTiposOper, Archivo, Url,
                 IdSharePoint, MimeType, Tmano, IdEstadoXDocumento, Estatus, FechaAlta)
              VALUES
                (@IdTipoDoc, @IdTipoOper, @Archivo, @Url,
                 @IdSharePoint, @MimeType, @Tamano, 1, 1, GETDATE());
              SELECT SCOPE_IDENTITY();",
            new
            {
                IdTipoDoc = TipoDocumentoFacturaXml,
                IdTipoOper = TipoOperCxp,
                Archivo = archivo,
                Url = url,
                IdSharePoint = idSharePoint,
                MimeType = mimeType,
                Tamano = tamano
            });


            _logger.LogInformation("Documento insertado en BD. Id={Id}", id);
            return id;
        }

        //Token

        private async Task<string> ObtenerTokenAsync(CancellationToken ct)
        {
            if (_token is not null && DateTime.UtcNow < _expiracionToken.AddMinutes(-1))
                return _token;

            _logger.LogInformation("Obteniendo token de SharePoint...");

            var valores = new Dictionary<string, string>
            {
                { "grant_type",    "client_credentials" },
                { "client_id",     _config.ClientId },
                { "client_secret", _config.ClientSecret },
                { "scope",         "https://graph.microsoft.com/.default" }
            };

            var respuesta = await _http.PostAsync(
                $"https://login.microsoftonline.com/{_config.TenantId}/oauth2/v2.0/token",
                new FormUrlEncodedContent(valores), ct);

            var body = await respuesta.Content.ReadAsStringAsync(ct);
            if (!respuesta.IsSuccessStatusCode)
                throw new Exception($"Error token SharePoint: {body}");

            var json = JsonDocument.Parse(body);
            _token = json.RootElement.GetProperty("access_token").GetString()!;
            var expiraEn = json.RootElement.GetProperty("expires_in").GetInt32();
            _expiracionToken = DateTime.UtcNow.AddSeconds(expiraEn);

            _logger.LogInformation("Token SharePoint obtenido. Válido por {Min} minutos.", expiraEn / 60);
            return _token;
        }
    }
}
