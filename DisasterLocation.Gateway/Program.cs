var builder = WebApplication.CreateBuilder(args);

// Reenvía las solicitudes sin conocer el modelo, la BLL o el DAL.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.MapReverseProxy();
app.Run();
