using ServWinActualizaCxP;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ServWinActualizaCxP.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService();

builder.Services.AddSingleton<RepositorioCxP>(sp =>
    new RepositorioCxP(
        sourceConnectionString: "Data Source=192.168.89.3;Initial Catalog=Toque;Persist Security Info=False;Pwd=9GPgaPD#sce^HhP;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True;Connection Timeout=90",
        destinationConnectionString: "Data Source=192.168.89.3;Initial Catalog=GrupoGeisha;Persist Security Info=False;Pwd=9GPgaPD#sce^HhP;MultipleActiveResultSets=True;Connect Timeout=1000;User ID=sa;TrustServerCertificate=True"
    )
);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();