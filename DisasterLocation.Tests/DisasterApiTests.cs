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
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var clear = new NpgsqlConnection(_connection); NpgsqlConnection.ClearPool(clear);
        using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection); drop.ExecuteNonQuery();
    }
}
