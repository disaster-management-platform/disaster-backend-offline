using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Platform.DAL;
using Platform.DAL.Contexts;
using Xunit;

namespace DisasterLocation.Tests;

public sealed class DisasterApiTests : IDisposable
{
    private readonly string _admin;
    private readonly string _name = "disaster_test_" + Guid.NewGuid().ToString("N");
    private readonly string _connection;
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes(
        "test-only-key-never-for-deployment-0123456789-abcdefghijklmnopqrstuv"));

    public DisasterApiTests()
    {
        _admin = PlatformDatabase.ResolveConnectionString(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")
            ?? throw new InvalidOperationException("Configure TEST_POSTGRES_CONNECTION para PostgreSQL local aislado."));
        var options = new NpgsqlConnectionStringBuilder(_admin);
        if (!options.Host!.StartsWith('/')) throw new InvalidOperationException("Las pruebas requieren un socket PostgreSQL local.");
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", connection); create.ExecuteNonQuery();
        options.Database = _name; _connection = options.ToString();
    }

    private WebApplicationFactory<Program> App(bool migrate = true, string environment = "Testing", bool localDemo = false)
    {
        if (migrate) PlatformDatabase.MigrateAsync(_connection).GetAwaiter().GetResult();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ConnectionStrings:Disasters", _connection);
            builder.UseSetting("Security:LocalDemo", localDemo.ToString());
            builder.UseSetting("Security:Authority", "https://identity.test");
            builder.UseSetting("Security:Audience", "platform");
            builder.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = "https://identity.test" };
                configuration.SigningKeys.Add(Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            }));
        });
    }

    private static void Authenticate(HttpClient client, int id, string role = "reconstruction.write", bool expired = false, bool forgedDemo = false)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new("sub", "authenticated-operator"), new("organization_id", "organization-a"),
            new("role", role), new("disaster_id", id.ToString()) };
        if (forgedDemo) claims.Add(new("local_demo", "true"));
        var token = new JwtSecurityToken("https://identity.test", "platform", claims, now.AddHours(-2),
            expired ? now.AddHours(-1) : now.AddHours(1), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private static async Task<int> CreateAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/desastres", new { nombre = "Reporte", ubicacion = "Bogotá",
            severidad = 4, descripcion = "Prueba aislada", latitud = 4.71, longitud = -74.07 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, data.GetProperty("radio").ValueKind);
        return data.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task PhaseChangesRequireIdentityRoleAndDisasterAccess()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client);
        var path = $"/api/desastres/{id}/fase"; var body = new { phase = "Response", expectedVersion = 1, reason = "Inicio de atención justificado" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, body)).StatusCode);
        Authenticate(client, id, expired: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, body)).StatusCode);
        Authenticate(client, id, "reconstruction.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, body)).StatusCode);
        Authenticate(client, id + 1, forgedDemo: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, body)).StatusCode);
        Authenticate(client, id);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, body)).StatusCode);
    }

    [Fact]
    public async Task PhaseTransitionsPreserveVersionAndAuditUnderConcurrentRequests()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client); Authenticate(client, id);
        var path = $"/api/desastres/{id}/fase";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { phase = "Closed", expectedVersion = 1 })).StatusCode);
        var first = await client.PutAsJsonAsync(path, new { phase = "Response", expectedVersion = 1, reason = "Inicio de atención justificado" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(2, (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, new { phase = "Recovery", expectedVersion = 1 })).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PutAsJsonAsync(path, new { phase = "Recovery", expectedVersion = 2 })));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(result => result.StatusCode).OrderBy(status => status));
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        Assert.Equal(3, (await db.Disasters.SingleAsync()).Version); Assert.Equal(2, await db.PhaseAudit.CountAsync());
        Assert.All(await db.PhaseAudit.ToListAsync(), audit => Assert.Equal("authenticated-operator", audit.Actor));
    }

    [Fact]
    public async Task AuditFailureRollsBackPhaseAndVersion()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client); Authenticate(client, id);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE disasters.\"DisasterPhaseAudit\" ADD CONSTRAINT \"TestRejectAudit\" CHECK (false)");
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PutAsJsonAsync($"/api/desastres/{id}/fase",
            new { phase = "Response", expectedVersion = 1, reason = "Inicio de atención justificado" })).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PutAsJsonAsync($"/api/desastres/{id}", new {
            nombre = "No guardar", ubicacion = "Bogotá", severidad = 4, descripcion = "Prueba aislada", latitud = 4.71,
            longitud = -74.07, radio = (double?)null, disasterTypeId = (int?)null, startedAt = (string?)null,
            attentionLevel = (int?)null, expectedVersion = 1 })).StatusCode);
        var saved = await db.Disasters.SingleAsync(); Assert.Equal("Reporte", saved.Nombre); Assert.Equal(1, saved.Version);
        Assert.Equal(DisasterLocation.Core.DisasterPhase.Registered, saved.Phase); Assert.Empty(await db.PhaseAudit.ToListAsync());
    }

    [Fact]
    public async Task MalformedJsonRemainsBadRequestInDevelopment()
    {
        await using var app = App(environment: "Development"); using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/desastres", new { nombre = "Reporte", ubicacion = "Bogotá",
            severidad = 1.5, descripcion = "Prueba aislada", latitud = 4.71, longitud = -74.07 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.Text.Json", body);
    }

    [Fact]
    public void OrdinaryStartupDoesNotApplyMigrations()
    {
        using var app = App(migrate: false); using var client = app.CreateClient();
        using var connection = new NpgsqlConnection(_connection); connection.Open();
        using var query = new NpgsqlCommand("SELECT to_regnamespace('disasters')::text", connection);
        Assert.Equal(DBNull.Value, query.ExecuteScalar());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void LocalDemoIsRejectedOutsideDevelopment(string environment)
    {
        using var app = App(environment: environment, localDemo: true);
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    [Fact]
    public async Task TypesArePublicButAdministrationIsAuthorizedAndKeepsHistoricalReferences()
    {
        await using var app = App(); using var client = app.CreateClient();
        var types = await client.GetFromJsonAsync<JsonElement>("/api/tipos-desastre");
        Assert.Equal(6, types.GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/tipos-desastre", new { name = "Otro" })).StatusCode);
        Authenticate(client, 1, role: "reconstruction.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/tipos-desastre", new { name = "Otro" })).StatusCode);
        Authenticate(client, 1, role: "disasters.admin");
        var added = await client.PostAsJsonAsync("/api/tipos-desastre", new { name = "Evento local" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var typeId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.True(typeId >= 7);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/tipos-desastre", new { name = "Evento local" })).StatusCode);
        var body = new { nombre = "Tipo histórico", ubicacion = "Bogotá", severidad = 5, descripcion = "Conservar",
            latitud = 4.7, longitud = -74, disasterTypeId = typeId, attentionLevel = 1 };
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var created = await client.PostAsJsonAsync("/api/desastres", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/tipos-desastre/{typeId}/desactivar", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/desastres", body)).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/desastres", body)).StatusCode);
        Assert.Equal(typeId, (await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}")).GetProperty("disasterTypeId").GetInt32());
    }

    [Fact]
    public async Task UpdatesAreVersionedAndAuditedWhileCreationReceiptsRemainImmutableAcrossInstances()
    {
        await using var app = App(); await using var peer = App(); using var client = app.CreateClient(); using var retry = peer.CreateClient();
        var operation = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", operation); retry.DefaultRequestHeaders.Add("Idempotency-Key", operation);
        var original = new { nombre = "Original", ubicacion = "Bogotá", severidad = 5, descripcion = "Inicial",
            latitud = 4.7, longitud = -74, radio = 1234.75, disasterTypeId = 1,
            startedAt = "2026-11-17T09:00:00-05:00", attentionLevel = 1 };
        var created = await client.PostAsJsonAsync("/api/desastres", original);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = receipt.GetProperty("id").GetInt32();
        var path = $"/api/desastres/{id}";
        var update = new { nombre = "Actualizado", ubicacion = "Medellín", severidad = 2, descripcion = "Nueva información",
            latitud = 6.2, longitud = -75.5, radio = 900.5, disasterTypeId = 2, startedAt = "2026-11-17T15:00:00Z",
            attentionLevel = 4, expectedVersion = 1, version = 900, phase = "Closed", clientRequestId = Guid.NewGuid(), registeredAt = "1900-01-01T00:00:00Z" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(path, update)).StatusCode);
        Authenticate(client, id + 1); Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, update)).StatusCode);
        Authenticate(client, id);
        var updated = await client.PutAsJsonAsync(path, update); Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var state = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, state.GetProperty("version").GetInt64()); Assert.Equal("Registered", state.GetProperty("phase").GetString());
        Assert.Equal(receipt.GetProperty("registeredAt").GetString(), state.GetProperty("registeredAt").GetString());
        var emitted = await client.GetFromJsonAsync<JsonElement>(path + "/alertas");
        Assert.Equal(new[] { "DisasterAreaChanged", "DisasterAttentionChanged" }, emitted.EnumerateArray().Select(x => x.GetProperty("eventType").GetString()).OrderBy(x => x));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, update)).StatusCode);
        var repeated = await retry.PostAsJsonAsync("/api/desastres", original); Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(receipt.ToString(), (await repeated.Content.ReadFromJsonAsync<JsonElement>()).ToString());
        Assert.Equal(state.ToString(), (await retry.GetFromJsonAsync<JsonElement>(path)).ToString());
        var history = await client.GetFromJsonAsync<JsonElement>(path + "/historial");
        Assert.Equal(1, history.GetArrayLength()); Assert.Equal("authenticated-operator", history[0].GetProperty("actor").GetString());
        Assert.Equal("Success", history[0].GetProperty("result").GetString());
        using var changeDocument = JsonDocument.Parse(history[0].GetProperty("changes").GetString()!);
        var changes = changeDocument.RootElement;
        Assert.Equal("Original", changes.GetProperty("Nombre").GetProperty("Before").GetString());
        Assert.Equal("Actualizado", changes.GetProperty("Nombre").GetProperty("After").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await retry.PostAsJsonAsync("/api/desastres", update)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await retry.GetAsync(path + "/historial")).StatusCode);
    }

    [Fact]
    public async Task MatrixRejectsForbiddenMovesAndRequiresJustificationAndClosureConfirmation()
    {
        Assert.Equal(0, (int)DisasterLocation.Core.DisasterPhase.Registered);
        Assert.Equal(1, (int)DisasterLocation.Core.DisasterPhase.Response);
        Assert.Equal(2, (int)DisasterLocation.Core.DisasterPhase.Recovery);
        Assert.Equal(3, (int)DisasterLocation.Core.DisasterPhase.Closed);
        await using var app = App(); using var client = app.CreateClient();
        var id = await CreateAsync(client); Authenticate(client, id); var path = $"/api/desastres/{id}/fase";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { phase = "Response", expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { phase = "Reconstruction", expectedVersion = 1, reason = "No permitido" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { phase = "Closed", expectedVersion = 1, reason = "Cierre" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, new { phase = "Closed", expectedVersion = 1,
            reason = "Atención local finalizada; sin compromisos pendientes", confirmNoCriticalCommitments = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new { phase = "Recovery", expectedVersion = 2, reason = "Retroceso" })).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/historial");
        Assert.True(history[0].GetProperty("commitmentsConfirmed").GetBoolean());
        Assert.Contains("sin compromisos", history[0].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(4, 0, true)]
    [InlineData(4, 5, false)]
    [InlineData(0, 2, true)]
    [InlineData(0, 6, false)]
    [InlineData(5, 2, true)]
    [InlineData(5, 6, false)]
    [InlineData(1, 6, false)]
    [InlineData(2, 6, true)]
    [InlineData(6, 3, true)]
    public async Task ExplicitPhaseMatrixIsEnforced(int before, int after, bool allowed)
    {
        await using var app = App(); using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var record = new DisasterLocation.Core.Disaster { Nombre = "Matriz", Ubicacion = "Bogotá", Severidad = 1,
            Descripcion = "Escenario controlado", Latitud = 0, Longitud = 0, Phase = (DisasterLocation.Core.DisasterPhase)before };
        db.Disasters.Add(record); await db.SaveChangesAsync();
        using var client = app.CreateClient(); Authenticate(client, record.Id);
        var response = await client.PutAsJsonAsync($"/api/desastres/{record.Id}/fase", new { phase = after,
            expectedVersion = 1, reason = "Decisión autorizada de avance", confirmNoCriticalCommitments = true });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, await db.PhaseAudit.CountAsync());
    }

    [Fact]
    public async Task HistoricalMigrationPreservesUnknownDatesAndDoesNotInventMissingOriginalContent()
    {
        await using var app = App(migrate: false); using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("20261117172742_ProtectDisastersReferences");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO disasters."Desastres" ("Nombre","Ubicacion","Severidad","Descripcion","Latitud","Longitud","ClientRequestId","Phase","Version")
            VALUES ('Histórico','Bogotá',4,'Conservar',4.7,-74,'00000000-0000-0000-0000-000000000001',0,1),
                   ('Sin evidencia','Bogotá',4,'Conservar',4.7,-74,'00000000-0000-0000-0000-000000000002',2,5);
            """);
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        var rows = await db.Disasters.OrderBy(x => x.Id).ToListAsync();
        Assert.NotNull(rows[0].CreationSnapshot); Assert.Null(rows[1].CreationSnapshot);
        Assert.All(rows, row => { Assert.Null(row.RegisteredAt); Assert.Null(row.StartedAt); Assert.Null(row.DisasterTypeId); Assert.Null(row.AttentionLevel); });
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", "00000000-0000-0000-0000-000000000001");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/desastres", new { nombre = "Histórico", ubicacion = "Bogotá",
            severidad = 4, descripcion = "Conservar", latitud = 4.7, longitud = -74 })).StatusCode);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "00000000-0000-0000-0000-000000000002");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/desastres", new { nombre = "Sin evidencia", ubicacion = "Bogotá",
            severidad = 4, descripcion = "Conservar", latitud = 4.7, longitud = -74 })).StatusCode);
        Assert.Equal(2, await db.Disasters.CountAsync());
        var historicalPage = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync");
        Assert.Equal(2, historicalPage.GetProperty("items").GetArrayLength());
        Assert.All(historicalPage.GetProperty("items").EnumerateArray(), row =>
            Assert.Equal(JsonValueKind.Null, row.GetProperty("registeredAt").ValueKind));
    }

    [Fact]
    public void SynchronizationCannotConsumeTheEntireConnectionPool()
    {
        using var app = App(migrate: false).WithWebHostBuilder(builder => builder.UseSetting("Synchronization:ConcurrentRequests", "100"));
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    private static object Operation(string name, string? key = null, object? disaster = null) => new {
        clientRequestId = key ?? Guid.NewGuid().ToString(),
        disaster = disaster ?? new { nombre = name, ubicacion = "Bogotá", severidad = 4, descripcion = "Lote",
            latitud = 4.7, longitud = -74, radio = (double?)null }
    };

    [Fact]
    public async Task BatchUploadAndPagedDeltaPreserveAll130OperationsAndCreationReceipts()
    {
        await using var app = App(); await using var peer = App(); using var client = app.CreateClient(); using var retry = peer.CreateClient();
        var operations = Enumerable.Range(0, 130).Select(i => Operation("Lote " + i)).ToArray();
        foreach (var batch in operations.Chunk(50))
        {
            var response = await client.PostAsJsonAsync("/api/desastres/batch", new { operations = batch });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
            Assert.Equal(batch.Length, results.GetArrayLength());
            Assert.All(results.EnumerateArray(), result => {
                Assert.Equal("Created", result.GetProperty("status").GetString());
                Assert.True(Guid.TryParse(result.GetProperty("clientRequestId").GetString(), out _));
                Assert.True(result.GetProperty("disaster").GetProperty("id").GetInt32() > 0);
            });
        }
        var repeated = await retry.PostAsJsonAsync("/api/desastres/batch", new { operations = operations.Take(50) });
        Assert.All((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray(),
            result => Assert.Equal("AlreadyProcessed", result.GetProperty("status").GetString()));
        var ids = new HashSet<int>(); string? cursor = null; int pages = 0;
        do {
            var page = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?limit=50" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.InRange(page.GetProperty("items").GetArrayLength(), 1, 50);
            foreach (var row in page.GetProperty("items").EnumerateArray()) Assert.True(ids.Add(row.GetProperty("id").GetInt32()));
            cursor = page.GetProperty("nextCursor").GetString(); pages++;
            if (!page.GetProperty("hasMore").GetBoolean()) break;
        } while (true);
        Assert.Equal(3, pages); Assert.Equal(130, ids.Count);
        var empty = await retry.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Empty(empty.GetProperty("items").EnumerateArray()); Assert.Equal(cursor, empty.GetProperty("nextCursor").GetString());
        var id = ids.First(); Authenticate(client, id);
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}");
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(current.ToString())!;
        fields["radio"] = 3456.75; fields["expectedVersion"] = current.GetProperty("version").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}", fields)).StatusCode);
        var changes = await retry.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(1, changes.GetProperty("items").GetArrayLength()); Assert.Equal(3456.75, changes.GetProperty("items")[0].GetProperty("radio").GetDouble());
        Assert.Equal(2, changes.GetProperty("items")[0].GetProperty("version").GetInt64());
        Assert.Equal(130, (await retry.GetFromJsonAsync<JsonElement>("/api/desastres")).GetArrayLength());
    }

    [Fact]
    public async Task BatchFailuresAreIndividualAndDoNotPoisonFollowingScopes()
    {
        await using var app = App(); using var client = app.CreateClient();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE disasters.\"Desastres\" ADD CONSTRAINT \"TestBatchFailure\" CHECK (\"Nombre\" <> 'Persistencia fallida')");
        var key = Guid.NewGuid().ToString();
        await client.PostAsJsonAsync("/api/desastres/batch", new { operations = new[] { Operation("Original", key) } });
        var caseKey = Guid.NewGuid().ToString();
        var caseInsensitive = JsonSerializer.SerializeToElement(new { CLIENTREQUESTID = caseKey, Disaster = new {
            nombre = "Válido", ubicacion = "Local", severidad = 1, descripcion = "Local", latitud = 0, longitud = 0 } });
        var response = await client.PostAsJsonAsync("/api/desastres/batch", new { operations = new[] {
            (object)caseInsensitive, Operation("Validación", disaster: new { nombre = "Inválido", severidad = 8 }),
            Operation("Tipo JSON inválido", disaster: new { severidad = 1.5 }),
            Operation("Diferente", key), Operation("Persistencia fallida"), Operation("Después del fallo") } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.Equal(new[] { "Created", "ValidationError", "ValidationError", "Conflict", "RetryableFailure", "Created" },
            results.EnumerateArray().Select(x => x.GetProperty("status").GetString()).ToArray());
        Assert.Equal(caseKey, results[0].GetProperty("clientRequestId").GetString());
        Assert.Equal(3, await db.Disasters.CountAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/desastres/batch", new { operations = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/desastres/batch",
            new { operations = Enumerable.Range(0, 51).Select(i => Operation(i.ToString())) })).StatusCode);
        using var huge = new StringContent("{\"operations\":[],\"padding\":\"" + new string('x', 1_048_576) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/desastres/batch", huge)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/desastres/batch/", huge)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/desastres/sync?limit=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/desastres/sync?cursor=invalid")).StatusCode);
    }

    [Fact]
    public async Task BatchRetryAcrossInstancesNeverDuplicatesConcurrentCreations()
    {
        await using var app = App(); await using var peer = App(); using var first = app.CreateClient(); using var second = peer.CreateClient();
        var body = new { operations = new[] { Operation("Concurrencia", Guid.NewGuid().ToString()) } };
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/desastres/batch", body), second.PostAsJsonAsync("/api/desastres/batch", body));
        var statuses = new List<string>(); var ids = new List<int>();
        foreach (var response in responses) {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
            statuses.Add(row.GetProperty("status").GetString()!); ids.Add(row.GetProperty("disaster").GetProperty("id").GetInt32());
        }
        Assert.Equal(new[] { "AlreadyProcessed", "Created" }, statuses.OrderBy(x => x)); Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task DeltaDoesNotSkipLateCommitsOrChangesFromTransactionsStartedEarlier()
    {
        await using var app = App(); using var client = app.CreateClient();
        await using var older = new NpgsqlConnection(_connection); await older.OpenAsync();
        await using var newer = new NpgsqlConnection(_connection); await newer.OpenAsync();
        const string insert = "INSERT INTO disasters.\"Desastres\" (\"Nombre\",\"Ubicacion\",\"Severidad\",\"Descripcion\",\"Latitud\",\"Longitud\",\"RegisteredAt\",\"UpdatedAt\") VALUES (@name,'Local',1,'Local',0,0,TIMESTAMPTZ '2026-11-17 12:00:00+00',TIMESTAMPTZ '2026-11-17 12:00:00+00')";
        await using (var first = await older.BeginTransactionAsync())
        {
            await using (var second = await newer.BeginTransactionAsync()) {
                await using var command = new NpgsqlCommand(insert, newer, second); command.Parameters.AddWithValue("name", "Confirma primero");
                await command.ExecuteNonQueryAsync(); await second.CommitAsync();
            }
            var page = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync");
            var cursor = page.GetProperty("nextCursor").GetString();
            await using var late = new NpgsqlCommand(insert, older, first); late.Parameters.AddWithValue("name", "Transacción anterior");
            await late.ExecuteNonQueryAsync(); await first.CommitAsync();
            var changes = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(cursor!));
            Assert.Equal("Transacción anterior", changes.GetProperty("items")[0].GetProperty("nombre").GetString());
            Assert.Equal(page.GetProperty("items")[0].GetProperty("updatedAt").GetString(),
                changes.GetProperty("items")[0].GetProperty("updatedAt").GetString());
        }
        var before = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync");
        var after = before.GetProperty("nextCursor").GetString();
        await using var pending = await older.BeginTransactionAsync();
        await using var write = new NpgsqlCommand(insert, older, pending); write.Parameters.AddWithValue("name", "Sin confirmar");
        await write.ExecuteNonQueryAsync();
        await using var following = await newer.BeginTransactionAsync();
        await using var blocked = new NpgsqlCommand(insert, newer, following); blocked.Parameters.AddWithValue("name", "Espera revisión");
        var waiting = blocked.ExecuteNonQueryAsync();
        await Task.Delay(100); Assert.False(waiting.IsCompleted);
        var interim = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(after!));
        Assert.Empty(interim.GetProperty("items").EnumerateArray()); Assert.Equal(after, interim.GetProperty("nextCursor").GetString());
        await pending.CommitAsync(); await waiting.WaitAsync(TimeSpan.FromSeconds(5)); await following.CommitAsync();
        var final = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(after!));
        Assert.Equal(new[] { "Sin confirmar", "Espera revisión" }, final.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("nombre").GetString()));
    }

    [Fact]
    public async Task IntegrationMigrationPreservesHistoricalSnapshotsSequencesAndUnknownReferences()
    {
        var options = new DbContextOptionsBuilder<DisasterDbContext>().UseNpgsql(_connection,
            p => p.MigrationsHistoryTable(PlatformDatabase.DisasterHistory, PlatformDatabase.DisasterSchema)).Options;
        await using (var prior = new DisasterDbContext(options))
        {
            await prior.GetService<IMigrator>().MigrateAsync("20261118030455_AddDisasterSyncSequence");
            await prior.Database.ExecuteSqlRawAsync("INSERT INTO disasters.\"Desastres\" (\"Nombre\",\"Ubicacion\",\"Descripcion\",\"Severidad\",\"Latitud\",\"Longitud\",\"CreationSnapshot\") VALUES ('Historic','Local','Original',1,0,0,'{{\"Nombre\":\"Historic\",\"Radio\":null}}')");
        }
        long revision;
        await using (var connection = new NpgsqlConnection(_connection)) {
            await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT \"SyncSequence\" FROM disasters.\"Desastres\"", connection);
            revision = (long)(await command.ExecuteScalarAsync())!;
        }
        await PlatformDatabase.MigrateAsync(_connection);
        await using var db = new DisasterDbContext(options);
        var row = await db.Disasters.SingleAsync();
        Assert.Equal("Historic", row.Nombre); Assert.Null(row.GeographyJson); Assert.Null(row.RegisteringOrganizationId);
        Assert.Null(row.EmergencyResponsibleId); Assert.Null(row.OrganizationsJson);
        Assert.Contains("Historic", row.CreationSnapshot); Assert.Equal(1, row.Version);
        Assert.Equal(revision, await db.Disasters.Select(x => EF.Property<long>(x, "SyncSequence")).SingleAsync());
        Assert.Empty(await db.Reports.ToListAsync()); Assert.Empty(await db.Outbox.ToListAsync());
    }

    [Fact]
    public async Task NewIntegrationAndResponsibilityOperationsNeverExpandAnonymousAccess()
    {
        await using var app = App(); using var client = app.CreateClient();
        var created = await client.PostAsJsonAsync("/api/desastres", new { nombre = "Público histórico", ubicacion = "Local", severidad = 2,
            descripcion = "Contrato compatible", latitud = 4.7, longitud = -74.0,
            registeringOrganizationId = "forged", emergencyResponsibleId = "forged", organizations = new[] { new { organizationId = "forged" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = await created.Content.ReadFromJsonAsync<JsonElement>(); var id = row.GetProperty("id").GetInt32();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("registeringOrganizationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("emergencyResponsibleId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("organizations").ValueKind);
        foreach (var path in new[] { $"/api/desastres/{id}/reportes", $"/api/desastres/{id}/alertas", "/api/reportes-desastre" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/desastres/{id}/organismos", new { expectedVersion = 1, organizations = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(1))).StatusCode);
    }

    private static object Incoming(string sourceReportId, bool preliminary = true, object? content = null) => new {
        sourceKind = "External", sourceId = "authorized-provider", sourceReportId,
        content = content ?? new { measurement = 4.5, original = "Contenido sin modificar" },
        createPreliminary = preliminary,
        disaster = preliminary ? new { nombre = "Detección", ubicacion = "Bogotá", severidad = 3,
            descripcion = "Fuente canónica", latitud = 4.7, longitud = -74.0, radio = (double?)null } : null,
        registeringOrganizationId = "eden:organization:25"
    };

    [Fact]
    public async Task ExternalReportsAreAuthorizedIdempotentAndDetectedAtomically()
    {
        await using var app = App(); await using var peer = App(); using var client = app.CreateClient(); using var second = peer.CreateClient();
        var request = Incoming("measurement-1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/reportes-desastre", request)).StatusCode);
        Authenticate(client, 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/reportes-desastre", request)).StatusCode);
        Authenticate(client, 1, "disasters.integration"); Authenticate(second, 1, "disasters.integration");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/reportes-desastre", new { sourceId = "no-kind", sourceReportId = "missing-kind", content = new { value = 1 } })).StatusCode);
        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/reportes-desastre", request), second.PostAsJsonAsync("/api/reportes-desastre", request));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Created }, results.Select(x => x.StatusCode).OrderBy(x => x));
        var first = await results[0].Content.ReadFromJsonAsync<JsonElement>(); var receipt = await results[1].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetString(), receipt.GetProperty("id").GetString());
        var id = first.GetProperty("disasterId").GetInt32();
        var disaster = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}");
        Assert.Equal("Detected", disaster.GetProperty("phase").GetString());
        Assert.Equal("eden:organization:25", disaster.GetProperty("registeringOrganizationId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/reportes-desastre", Incoming("measurement-1", content: new { measurement = 8 }))).StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        Assert.Equal(1, await db.Disasters.CountAsync()); Assert.Equal(1, await db.Reports.CountAsync());
        Assert.Equal(1, await db.Outbox.CountAsync()); Assert.Equal(1, await db.PhaseAudit.CountAsync());
        Assert.Equal("DisasterDetected", (await db.Outbox.SingleAsync()).EventType);
        Assert.Contains("measurement", (await db.Reports.SingleAsync()).OriginalContent);
    }

    [Fact]
    public async Task ConsolidationPreservesOriginalReportsAndAdvancesTheDeltaCursor()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client);
        Authenticate(client, id, "disasters.integration");
        var reportResponse = await client.PostAsJsonAsync("/api/reportes-desastre", Incoming("pending-1", false));
        var report = await reportResponse.Content.ReadFromJsonAsync<JsonElement>(); var reportId = report.GetProperty("id").GetGuid();
        var initial = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync");
        Authenticate(client, id + 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/desastres/{id}/reportes/{reportId}", new { expectedVersion = 1 })).StatusCode);
        Authenticate(client, id);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/reportes/{reportId}", new { expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/desastres/{id}/reportes/{reportId}", new { expectedVersion = 2 })).StatusCode);
        var reports = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/reportes");
        Assert.Equal(report.GetProperty("content").ToString(), reports[0].GetProperty("content").ToString());
        var delta = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(initial.GetProperty("nextCursor").GetString()!));
        Assert.Single(delta.GetProperty("items").EnumerateArray()); Assert.Equal(2, delta.GetProperty("items")[0].GetProperty("version").GetInt64());
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/historial");
        Assert.Contains(reportId.ToString(), history[0].GetProperty("changes").GetString());
        Authenticate(client, id, "disasters.integration");
        var internalResponse = await client.PostAsJsonAsync("/api/reportes-desastre", new {
            sourceKind = "Internal", sourceId = "operator-console", sourceReportId = "internal-25",
            content = new { observation = "Segundo reporte original" } });
        var internalReport = await internalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Authenticate(client, id);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/reportes/{internalReport.GetProperty("id").GetGuid()}", new { expectedVersion = 2 })).StatusCode);
        var all = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/reportes");
        Assert.Equal(2, all.GetArrayLength());
        Assert.Contains(all.EnumerateArray(), x => x.GetProperty("id").GetGuid() == reportId && JsonElement.DeepEquals(x.GetProperty("content"), report.GetProperty("content")));
        Assert.Equal(3, (await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}")).GetProperty("version").GetInt64());
    }

    private static object Geography(long version, object? polygon = null) => new {
        expectedVersion = version, sourceId = "gis-source:authorized",
        geography = new { polygon, municipalities = new[] { "Bogotá" }, neighborhoods = new[] { "Centro" }, riskZones = new[] { "Ribera" } }
    };
    private static object Polygon(params double[][] points) => new { type = "Polygon", coordinates = new[] { points } };

    [Fact]
    public async Task OrganizationsAndGeographyAreVersionedAuditedAndIncludedInDeltaSync()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client); Authenticate(client, id);
        var organizations = new { expectedVersion = 1, registeringOrganizationId = "eden:registrar:1", emergencyResponsibleId = "responsible:4",
            organizations = new[] { new { organizationId = "eden:12", responsibility = "Rescate" }, new { organizationId = "eden:12", responsibility = "Evacuación" } } };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/organismos", organizations)).StatusCode);
        var cursor = (await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync")).GetProperty("nextCursor").GetString();
        var polygon = Polygon([-74.1,4.6],[-74.0,4.6],[-74.0,4.7],[-74.1,4.6]);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(2, polygon))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(2))).StatusCode);
        var changes = await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(cursor!));
        var current = changes.GetProperty("items")[0]; Assert.Equal(3, current.GetProperty("version").GetInt64());
        Assert.Equal("Polygon", current.GetProperty("geography").GetProperty("polygon").GetProperty("type").GetString());
        Assert.Equal(2, current.GetProperty("organizations").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(3))).StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/historial");
        Assert.Equal(3, history.GetArrayLength()); Assert.Contains("Polygon", history[2].GetProperty("changes").GetString());
        Assert.Equal("gis-source:authorized", history[2].GetProperty("reason").GetString());
        Assert.All(history.EnumerateArray(), x => Assert.Equal("authenticated-operator", x.GetProperty("actor").GetString()));
        var alerts = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/alertas"); Assert.Equal(2, alerts.GetArrayLength());
    }

    [Fact]
    public async Task InvalidGeometriesAreRejectedWithoutChangingVersionOrAudit()
    {
        await using var app = App(); using var client = app.CreateClient(); var id = await CreateAsync(client); Authenticate(client, id);
        foreach (var polygon in new object[] {
            Polygon([0,0],[1,1],[0,1],[1,0],[0,0]), Polygon([0,0],[1,1],[2,2],[0,0]),
            Polygon([0,0],[1,0],[1,1]), Polygon([0,0],[181,0],[1,1],[0,0]),
            new { type = "Polygon", coordinates = new double[][][] { [[0,0],[2,0],[2,2],[0,2],[0,0]], [[3,3],[4,3],[4,4],[3,3]] } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(1, polygon))).StatusCode);
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}"); Assert.Equal(1, current.GetProperty("version").GetInt64());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}/historial")).EnumerateArray());
    }

    [Fact]
    public async Task FailedOutboxInsertRollsBackBusinessAuditAndDeltaAndHistoricalCreationStillReplays()
    {
        await using var app = App(); using var client = app.CreateClient();
        var key = Guid.NewGuid().ToString(); client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var body = new { nombre = "Original", ubicacion = "Bogotá", severidad = 3, descripcion = "Histórico", latitud = 4.7, longitud = -74.0 };
        var created = await client.PostAsJsonAsync("/api/desastres", body); var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("id").GetInt32(); Authenticate(client, id);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE disasters.\"DisasterOutboxMessages\" ADD CONSTRAINT \"RejectEvent\" CHECK (\"EventType\" <> 'DisasterAreaChanged')");
        var cursor = (await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync")).GetProperty("nextCursor").GetString();
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(1))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/desastres/sync?cursor=" + Uri.EscapeDataString(cursor!))).GetProperty("items").EnumerateArray());
        Assert.Equal(0, await db.PhaseAudit.CountAsync()); Assert.Equal(0, await db.Outbox.CountAsync());
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE disasters.\"DisasterOutboxMessages\" DROP CONSTRAINT \"RejectEvent\"");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/desastres/{id}/geografia", Geography(1))).StatusCode);
        var replay = await client.PostAsJsonAsync("/api/desastres", body); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(receipt.GetProperty("nombre").GetString(), replayed.GetProperty("nombre").GetString());
        Assert.Equal(JsonValueKind.Null, replayed.GetProperty("geography").ValueKind);
        Assert.NotEqual(1, (await client.GetFromJsonAsync<JsonElement>($"/api/desastres/{id}")).GetProperty("version").GetInt64());
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var clear = new NpgsqlConnection(_connection); NpgsqlConnection.ClearPool(clear);
        using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection); drop.ExecuteNonQuery();
    }
}
