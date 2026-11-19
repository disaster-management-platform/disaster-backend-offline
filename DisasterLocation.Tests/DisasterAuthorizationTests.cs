using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.DAL.Contexts;
using Xunit;

namespace DisasterLocation.Tests;

public sealed partial class DisasterApiTests
{
    [Fact]
    public async Task CreatorScopeAllowsNewResourcesWithoutJwtEditsAndRejectsForeignOrganizations()
    {
        await using var app = App(); using var creator = app.CreateClient();
        var body = new { nombre="Creación autorizada",ubicacion="Neiva, Huila",severidad=3,descripcion="Prueba de ámbito",
            latitud=2.9,longitud=-75.2,createdBy="spoofed",registeringOrganizationId="foreign" };
        Assert.Equal(HttpStatusCode.Unauthorized,(await creator.PostAsJsonAsync("/api/desastres",body)).StatusCode);
        Authenticate(creator,0,"reconstruction.write",actor:"creator");
        Assert.Equal(HttpStatusCode.Forbidden,(await creator.PostAsJsonAsync("/api/desastres",body)).StatusCode);
        Authenticate(creator,0,"disasters.write",actor:"creator");
        var key=Guid.NewGuid().ToString();creator.DefaultRequestHeaders.Add("Idempotency-Key",key);
        var made=await creator.PostAsJsonAsync("/api/desastres",body);
        Assert.Equal(HttpStatusCode.Created,made.StatusCode);
        var value=await made.Content.ReadFromJsonAsync<JsonElement>();var id=value.GetProperty("id").GetInt32();
        var path=$"/api/desastres/{id}";
        Assert.Equal(path,made.Headers.Location!.ToString());
        Assert.Equal("organization-a",value.GetProperty("registeringOrganizationId").GetString());
        Assert.Equal(HttpStatusCode.OK,(await creator.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await creator.GetAsync(path+"/historial")).StatusCode);
        Assert.Single((await creator.GetFromJsonAsync<JsonElement>("/api/desastres")).EnumerateArray());
        var page=await creator.GetFromJsonAsync<JsonElement>("/api/desastres/sync");
        Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK,(await creator.PostAsJsonAsync("/api/desastres",body)).StatusCode);
        var update=new { nombre="Actualizado",ubicacion="Neiva, Huila",severidad=3,descripcion="Cambio autorizado",
            latitud=2.9,longitud=-75.2,radio=(double?)null,disasterTypeId=(int?)null,startedAt=(DateTimeOffset?)null,attentionLevel=(int?)null,expectedVersion=1 };
        Assert.Equal(HttpStatusCode.OK,(await creator.PutAsJsonAsync(path,update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await creator.PutAsJsonAsync(path,update)).StatusCode);
        var history=await creator.GetFromJsonAsync<JsonElement>(path+"/historial");
        Assert.Equal("creator",history[0].GetProperty("actor").GetString());
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<DisasterDbContext>();
        var stored=await db.Disasters.AsNoTracking().SingleAsync();
        using var original=JsonDocument.Parse(stored.CreationSnapshot!);
        Assert.Equal("creator",original.RootElement.GetProperty("CreatedBy").GetString());
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(Core.Disaster))!.GetProperties(),p=>p.Name=="CreatedBy");
        using var colleague=app.CreateClient();Authenticate(colleague,0,"disasters.write",actor:"colleague");
        Assert.Equal(HttpStatusCode.Forbidden,(await colleague.GetAsync(path)).StatusCode);
        Authenticate(colleague,id,"disasters.read",actor:"colleague");
        Assert.Equal(HttpStatusCode.OK,(await colleague.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await colleague.PutAsJsonAsync(path,update)).StatusCode);
        using var reader=app.CreateClient();Authenticate(reader,0,"disasters.organization.read",actor:"reader");
        Assert.Equal(HttpStatusCode.OK,(await reader.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await reader.PutAsJsonAsync(path,update)).StatusCode);
        using var invalid=app.CreateClient();Authenticate(invalid,id,"disasters.admin",organization:"");
        Assert.Equal(HttpStatusCode.Forbidden,(await invalid.GetAsync(path)).StatusCode);
        using var foreign=app.CreateClient();Authenticate(foreign,id,"disasters.admin",actor:"foreign",organization:"organization-b");
        foreach(var endpoint in new[]{path,path+"/historial"})Assert.Equal(HttpStatusCode.Forbidden,(await foreign.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PutAsJsonAsync(path,update)).StatusCode);
        Assert.Empty((await foreign.GetFromJsonAsync<JsonElement>("/api/desastres")).EnumerateArray());
        Assert.Empty((await foreign.GetFromJsonAsync<JsonElement>("/api/desastres/sync")).GetProperty("items").EnumerateArray());
        foreign.DefaultRequestHeaders.Add("Idempotency-Key",key);
        Assert.Equal(HttpStatusCode.Forbidden,(await foreign.PostAsJsonAsync("/api/desastres",body)).StatusCode);
        Assert.Equal(2,(await db.Disasters.AsNoTracking().SingleAsync()).Version);
    }
}
