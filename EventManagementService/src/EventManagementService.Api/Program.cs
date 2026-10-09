using Npgsql;
using EventManagementService.Api;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["CONNECTION_STRING"]
    ?? throw new InvalidOperationException("Set CONNECTION_STRING before starting the API.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddProblemDetails();
var app = builder.Build();
app.UseExceptionHandler();

var source = app.Services.GetRequiredService<NpgsqlDataSource>();
// Идемпотентная инициализация: существующие данные никогда не удаляются.
using var initializationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using (var command = source.CreateCommand("""
    CREATE TABLE IF NOT EXISTS events (
        id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        title varchar(200) NOT NULL CHECK (length(trim(title)) > 0),
        date date NOT NULL,
        location varchar(300) NOT NULL CHECK (length(trim(location)) > 0),
        description varchar(2000) NOT NULL
    );
    """))
{
    await command.ExecuteNonQueryAsync(initializationTimeout.Token);
}
app.Logger.LogInformation("PostgreSQL connected; events table ready.");

app.MapGet("/", () => Results.Ok(new
{
    service = "Event Management Service",
    endpoints = new[] { "GET /health", "GET /events", "GET /events/{id}",
        "POST /events", "PUT /events/{id}", "DELETE /events/{id}" }
}));

app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var command = db.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok", service = "Event Management Service",
            database = "PostgreSQL", timestamp = DateTimeOffset.UtcNow });
    }
    catch (NpgsqlException)
    {
        return Results.Problem("PostgreSQL is unavailable.", statusCode: 503);
    }
});

app.MapGet("/events", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    var events = new List<EventItem>();
    await using var command = db.CreateCommand("SELECT id, title, date, location, description FROM events ORDER BY id");
    await using var reader = await command.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct)) events.Add(ReadEvent(reader));
    return Results.Ok(events);
});

app.MapGet("/events/{id:int}", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var command = db.CreateCommand("SELECT id, title, date, location, description FROM events WHERE id = $1");
    command.Parameters.AddWithValue(id);
    await using var reader = await command.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct) ? Results.Ok(ReadEvent(reader)) : Results.NotFound();
});

app.MapPost("/events", async (EventRequest request, NpgsqlDataSource db, CancellationToken ct) =>
{
    var errors = EventValidation.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    await using var command = db.CreateCommand("""
        INSERT INTO events (title, date, location, description) VALUES ($1, $2, $3, $4)
        RETURNING id, title, date, location, description
        """);
    AddParameters(command, request);
    await using var reader = await command.ExecuteReaderAsync(ct);
    await reader.ReadAsync(ct);
    var item = ReadEvent(reader);
    return Results.Created($"/events/{item.Id}", item);
});

app.MapPut("/events/{id:int}", async (int id, EventRequest request, NpgsqlDataSource db, CancellationToken ct) =>
{
    var errors = EventValidation.Validate(request);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    await using var command = db.CreateCommand("""
        UPDATE events SET title = $1, date = $2, location = $3, description = $4 WHERE id = $5
        RETURNING id, title, date, location, description
        """);
    AddParameters(command, request);
    command.Parameters.AddWithValue(id);
    await using var reader = await command.ExecuteReaderAsync(ct);
    return await reader.ReadAsync(ct) ? Results.Ok(ReadEvent(reader)) : Results.NotFound();
});

app.MapDelete("/events/{id:int}", async (int id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var command = db.CreateCommand("DELETE FROM events WHERE id = $1");
    command.Parameters.AddWithValue(id);
    return await command.ExecuteNonQueryAsync(ct) == 1 ? Results.NoContent() : Results.NotFound();
});
app.Run();

static EventItem ReadEvent(NpgsqlDataReader reader) => new(reader.GetInt32(0), reader.GetString(1),
    reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4));

static void AddParameters(NpgsqlCommand command, EventRequest request)
{
    command.Parameters.AddWithValue(request.Title!.Trim());
    command.Parameters.AddWithValue(request.Date!.Value);
    command.Parameters.AddWithValue(request.Location!.Trim());
    command.Parameters.AddWithValue(request.Description?.Trim() ?? "");
}

