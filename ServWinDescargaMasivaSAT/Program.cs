using ServWinDescargaMasivaSAT;
using ServWinDescargaMasivaSAT.Models;
using ServWinDescargaMasivaSAT.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<SatFielConfig>(builder.Configuration.GetSection("SatFiel"));

builder.Services.AddHttpClient<IRepositorioSatAuth, RepositorioSatAuth>(client =>client.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddHttpClient<IRepositorioSatDescarga, RepositorioSatDescarga>(client =>client.Timeout = TimeSpan.FromSeconds(120));

builder.Services.AddSingleton<IRepositorioCxP, RepositorioCxP>();

builder.Services.Configure<SharePointConfig>(builder.Configuration.GetSection("SharePoint"));

builder.Services.AddHttpClient<IRepositorioSharePoint, RepositorioSharePoint>(client =>client.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddHostedService<Worker>();

builder.Services.AddWindowsService(options => options.ServiceName = "Geisha - Descarga Masiva SAT");

var host = builder.Build();
host.Run();