using RummiHub.Core.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "RummiHub API");

app.MapGet("/ping", () => Results.Ok(new { Status = "ok", Timestamp = DateTime.UtcNow }));

app.MapPost("/solve", (SolveRequest request) =>
{
    try
    {
        // TODO: publish request to Redis Stream, await response from solver worker
        return Task.FromResult(Results.Accepted($"/solve/{request.RequestId}", new SolveResponse
        {
            RequestId = request.RequestId,
            Solved = false,
            Error = "Solver not yet implemented — job queued"
        }));
    }
    catch (Exception exception)
    {
        return Task.FromException<IResult>(exception);
    }
});

app.MapGet("/solve/{requestId}", (string requestId) =>
{
    // TODO: check Redis for solve result
    return Results.Ok(new { RequestId = requestId, Status = "pending" });
});

app.MapDefaultEndpoints();

app.Run();
