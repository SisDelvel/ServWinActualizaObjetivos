using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace ServWinDescargaMasivaSAT.Services
{
    public interface IRepositorioSatAuth
    {
        Task<string> ObtenerTokenAsync(CancellationToken cancellationToken = default);
    }
    internal class RepositorioSatAuth: IRepositorioSatAuth
    {
        private const string UrlAutenticacion = "https://cfdidescargamasivasolicitud.clouda.sat.gob.mx/Autenticacion/Autenticacion.svc";
        private const string SoapAction = "http://DescargaMasivaTerceros.gob.mx/IAutenticacion/Autentica";

        private string? _token;
        private DateTime _expiracion = DateTime.MinValue;

        private readonly HttpClient _httpClient;
        private readonly ILogger<RepositorioSatAuth> _logger;
        private readonly string _rutaCer;
        private readonly string _rutaKey;
        private readonly string _contrasena;

        public RepositorioSatAuth(HttpClient httpClient,ILogger<RepositorioSatAuth> logger,IConfiguration config)
        {
            _httpClient = httpClient;
            _logger = logger;
            _rutaCer = config["SatFiel:RutaCertificado"]!;
            _rutaKey = config["SatFiel:RutaLlavePrivada"]!;
            _contrasena = config["SatFiel:Contrasena"]!;
        }

        public async Task<string> ObtenerTokenAsync(CancellationToken cancellationToken = default)
        {
            if (_token is not null && DateTime.UtcNow < _expiracion.AddSeconds(-30))
            {
                _logger.LogInformation("Token SAT en caché, reutilizando.");
                return _token;
            }

            _logger.LogInformation("Solicitando nuevo token al SAT...");

            var cert = new X509Certificate2(File.ReadAllBytes(_rutaCer));
            var rsa = RSA.Create();
            rsa.ImportEncryptedPkcs8PrivateKey(
                Encoding.UTF8.GetBytes(_contrasena),
                File.ReadAllBytes(_rutaKey),
                out _);

            var xml = ConstruirSoap(cert, rsa);

            var contenido = new StringContent(xml, Encoding.UTF8, "text/xml");
            contenido.Headers.Add("SOAPAction", $"\"{SoapAction}\"");

            var respuesta = await _httpClient.PostAsync(UrlAutenticacion, contenido, cancellationToken);
            var body = await respuesta.Content.ReadAsStringAsync(cancellationToken);

            if (!respuesta.IsSuccessStatusCode)
            {
                _logger.LogError("SAT rechazó autenticación: {Body}", body);
                throw new Exception($"Error autenticación SAT: {body}");
            }

            _token = ExtraerToken(body);
            _expiracion = DateTime.UtcNow.AddMinutes(5);

            _logger.LogInformation("Token SAT obtenido correctamente.");
            return _token;
        }


        private static string ConstruirSoap(X509Certificate2 cert, RSA rsa)
        {
            var timestampId = "_0";
            var tokenId = $"uuid-{Guid.NewGuid():N}-1";
            var creado = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var expira = DateTime.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var certBase64 = Convert.ToBase64String(cert.RawData);

            var timestampXml =
                $"<u:Timestamp xmlns:u=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\" u:Id=\"{timestampId}\">" +
                $"<u:Created>{creado}</u:Created>" +
                $"<u:Expires>{expira}</u:Expires>" +
                $"</u:Timestamp>";

            var digestTimestamp = DigestC14N(timestampXml);

            var signedInfoXml =
                "<SignedInfo xmlns=\"http://www.w3.org/2000/09/xmldsig#\">" +
                "<CanonicalizationMethod Algorithm=\"http://www.w3.org/2001/10/xml-exc-c14n#\"/>" +
                "<SignatureMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#rsa-sha1\"/>" +
                $"<Reference URI=\"#{timestampId}\">" +
                "<Transforms><Transform Algorithm=\"http://www.w3.org/2001/10/xml-exc-c14n#\"/></Transforms>" +
                "<DigestMethod Algorithm=\"http://www.w3.org/2000/09/xmldsig#sha1\"/>" +
                $"<DigestValue>{digestTimestamp}</DigestValue>" +
                "</Reference>" +
                "</SignedInfo>";

            var signedInfoCanonical = CanonicalizarXml(signedInfoXml);
            var firma = Convert.ToBase64String(
                rsa.SignData(signedInfoCanonical, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));

            return
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "xmlns:u=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
                "<s:Header>" +
                "<o:Security s:mustUnderstand=\"1\" " +
                "xmlns:o=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
                timestampXml +
                $"<o:BinarySecurityToken u:Id=\"{tokenId}\" " +
                "ValueType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3\" " +
                "EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" +
                certBase64 +
                "</o:BinarySecurityToken>" +
                "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\">" +
                signedInfoXml +
                $"<SignatureValue>{firma}</SignatureValue>" +
                "<KeyInfo><o:SecurityTokenReference>" +
                $"<o:Reference URI=\"#{tokenId}\" " +
                "ValueType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3\"/>" +
                "</o:SecurityTokenReference></KeyInfo>" +
                "</Signature>" +
                "</o:Security>" +
                "</s:Header>" +
                "<s:Body><Autentica xmlns=\"http://DescargaMasivaTerceros.gob.mx\"/></s:Body>" +
                "</s:Envelope>";
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

            var transform = new XmlDsigExcC14NTransform();
            transform.LoadInput(doc);

            using var stream = (MemoryStream)transform.GetOutput(typeof(Stream));
            return stream.ToArray();
        }

        private static string ExtraerToken(string xmlRespuesta)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xmlRespuesta);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("h", "http://DescargaMasivaTerceros.gob.mx");

            var nodo = doc.SelectSingleNode("//h:AutenticaResponse/h:AutenticaResult", ns);
            if (nodo is null || string.IsNullOrWhiteSpace(nodo.InnerText))
                throw new Exception("El SAT no devolvió token. Verifica que la FIEL esté vigente.");

            return nodo.InnerText.Trim();
        }
    }
}

