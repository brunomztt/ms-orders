using Microsoft.AspNetCore.Diagnostics;
using Dapper;
using MySqlConnector;
using MsOrders.Domain;
using MsOrders.Repositories;
using MsOrders.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<OrderRepository>();
builder.Services.AddHttpClient<CatalogClient>(http => {
    http.BaseAddress = new Uri((builder.Configuration["Catalog:BaseUrl"] ?? "http://localhost:8080").TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHostedService<OrderWorker>();
builder.Services.AddHostedService<OutboxWorker>();
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context => {
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch { OrderConflictException => 409, MySqlException => 503, _ => 500 };
    await Results.Problem(statusCode: context.Response.StatusCode,
        title: error is OrderConflictException ? error.Message : "Service temporarily unavailable").ExecuteAsync(context);
}));
app.Use(async (context, next) => { context.Response.Headers["X-Pod-Name"] = Environment.MachineName; await next(); });
app.MapGet("/health/live", () => Results.Ok(new { status = "UP", pod = Environment.MachineName }));
app.MapGet("/health", async (OrderRepository repository) => {
    try { await using var db = await repository.Open(); await db.ExecuteScalarAsync<int>("SELECT 1");
        return Results.Ok(new { status = "UP", database = "connected", pod = Environment.MachineName }); }
    catch { return Results.Problem(statusCode: 503, title: "Orders database unavailable"); }
});
app.MapGet("/api/orders/instance", () => new { service = "ms-orders", pod = Environment.MachineName });
app.MapControllers();
var repository = app.Services.GetRequiredService<OrderRepository>();
for (var attempt = 1; ; attempt++)
{
    try { await repository.Initialize(CancellationToken.None); break; }
    catch (Exception ex) when (attempt < 30) { app.Logger.LogWarning("Database not ready ({Attempt}): {Type}", attempt, ex.GetType().Name); await Task.Delay(2000); }
}
await app.RunAsync();
