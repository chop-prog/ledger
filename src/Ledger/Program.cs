using Ledger;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<LedgerDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Ledger") ?? "Data Source=ledger.db"));
builder.Services.AddScoped<LedgerService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LedgerDb>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); // readers don't wait for the writer
}

var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    try
    {
        return await next(context);
    }
    catch (LedgerException e)
    {
        return Results.Problem(detail: e.Message, statusCode: e.Status);
    }
});

api.MapPost("/accounts", async (NewAccount body, LedgerService ledger) =>
{
    var account = await ledger.CreateAccountAsync(body.Owner);
    return Results.Created($"/api/accounts/{account.Id}", account);
});

api.MapGet("/accounts/{id:long}", async (long id, LedgerService ledger) => Results.Ok(await ledger.GetAccountAsync(id)));

api.MapGet("/accounts/{id:long}/statement", async (long id, LedgerService ledger) => Results.Ok(await ledger.StatementAsync(id)));

api.MapPost("/accounts/{id:long}/deposits", async (long id, MoneyIn body, [FromHeader(Name = "Idempotency-Key")] string? key, LedgerService ledger, HttpContext http) =>
    Respond(await ledger.MoveAsync(key ?? "", null, id, body.Amount), http));

api.MapPost("/transfers", async (NewTransfer body, [FromHeader(Name = "Idempotency-Key")] string? key, LedgerService ledger, HttpContext http) =>
    Respond(await ledger.MoveAsync(key ?? "", body.From, body.To, body.Amount), http));

app.Run();

static IResult Respond((Transfer Transfer, bool Replayed) result, HttpContext http)
{
    if (!result.Replayed) return Results.Created($"/api/transfers/{result.Transfer.Id}", result.Transfer);
    http.Response.Headers["Idempotent-Replay"] = "true";
    return Results.Ok(result.Transfer);
}

record NewAccount(string Owner);
record MoneyIn(long Amount);
record NewTransfer(long From, long To, long Amount);

public partial class Program;
