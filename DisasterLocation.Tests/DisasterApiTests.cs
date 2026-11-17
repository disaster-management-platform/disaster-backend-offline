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
        var path = $"/api/desastres/{id}/fase"; var body = new { phase = "Response", expectedVersion = 1 };
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
        var first = await client.PutAsJsonAsync(path, new { phase = "Response", expectedVersion = 1 });
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
            new { phase = "Response", expectedVersion = 1 })).StatusCode);
        var saved = await db.Disasters.SingleAsync(); Assert.Equal(1, saved.Version);
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

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(_admin); connection.Open();
        using var clear = new NpgsqlConnection(_connection); NpgsqlConnection.ClearPool(clear);
        using var drop = new NpgsqlCommand($"DROP DATABASE \"{_name}\" WITH (FORCE)", connection); drop.ExecuteNonQuery();
    }
}
