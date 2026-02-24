using Onlyspans.TargetsController;

var builder = WebApplication.CreateBuilder(args);

builder.Host.AddSerilog();

builder.Services
    .AddGrpcServices(builder.Environment)
    .AddConnectionRegistry()
    .AddHealthz();

var app = builder.Build();

app.UseGrpcServices();
app.UseHealthz();

app.MapGet("/", () => "Onlyspans.TargetsController");

app.Run();
