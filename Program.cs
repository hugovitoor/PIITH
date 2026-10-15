using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using PIITH;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var connectionString = GetConnectionString(builder.Configuration);
builder.Services.AddDbContext<PiithDb>(options => options.UseNpgsql(connectionString));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "PIITH.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Proteção dos endpoints da API por perfil. As contas abaixo são exclusivamente demonstrativas.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/health") ||
        path.StartsWithSegments("/api/auth/login") || path.StartsWithSegments("/api/auth/me") ||
        path.StartsWithSegments("/api/auth/logout"))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "Faça login para continuar." });
        return;
    }

    var role = context.User.FindFirst(ClaimTypes.Role)?.Value ?? "";
    var method = context.Request.Method;
    var isAssistantOnly = (method == "PATCH" && path.StartsWithSegments("/api/prefichas")) ||
                          (method == "PATCH" && path.StartsWithSegments("/api/doctors"));
    var isPointPost = method == "POST" && path.StartsWithSegments("/api/doctors") && path.Value?.EndsWith("/point", StringComparison.OrdinalIgnoreCase) == true;
    var isCreateFicha = method == "POST" && path.Equals("/api/prefichas");
    var isDashboard = method == "GET" && path.Equals("/api/dashboard");

    var allowed = role switch
    {
        "assistente" => true,
        "medico" => isDashboard || isPointPost,
        "totem" => isCreateFicha,
        _ => false
    };
    if (isAssistantOnly && role != "assistente") allowed = false;
    if (!allowed)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = "Seu perfil não tem permissão para esta ação." });
        return;
    }
    await next();
});

// Protótipo acadêmico: as contas/senhas são de demonstração, não use dados reais de pacientes.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PiithDb>();
    await db.Database.EnsureCreatedAsync();
    // Atualiza bancos já existentes sem apagar dados da demonstração.
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"PreFichas\" ADD COLUMN IF NOT EXISTS \"IntakeAnswers\" text NOT NULL DEFAULT '{}';");
    if (!await db.Doctors.AnyAsync())
    {
        db.Doctors.AddRange(
            new Doctor { Name = "Dra. Marina Costa", Specialty = "Clínica geral", Status = "Disponível", LastReleasedAt = DateTime.UtcNow.AddMinutes(-18) },
            new Doctor { Name = "Dr. Rafael Santos", Specialty = "Clínica geral", Status = "Ocupado", LastReleasedAt = DateTime.UtcNow.AddMinutes(-4) },
            new Doctor { Name = "Dra. Camila Alves", Specialty = "Clínica geral", Status = "Indeterminado", LastReleasedAt = DateTime.UtcNow.AddMinutes(-32) }
        );
        await db.SaveChangesAsync();
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", app = "PIITH" }));

app.MapPost("/api/auth/login", async (LoginRequest input, HttpContext context, PiithDb db) =>
{
    var username = input.Username?.Trim().ToLowerInvariant() ?? "";
    var password = input.Password ?? "";
    (string Name, string Role, int? DoctorId)? account = username switch
    {
        "assistente" when password == "1234" => (Name: "Assistente PIITH", Role: "assistente", DoctorId: (int?)null),
        "medico" when password == "1234" => (Name: "Profissional de saúde", Role: "medico", DoctorId: (int?)null),
        "totem" when password == "1234" => (Name: "Totem do paciente", Role: "totem", DoctorId: (int?)null),
        _ => ((string Name, string Role, int? DoctorId)?)null
    };
    if (account is null) return Results.Unauthorized();
    if (account.Value.Role == "medico")
    {
        var firstDoctor = await db.Doctors.OrderBy(d => d.Id).FirstOrDefaultAsync();
        if (firstDoctor is null) return Results.Problem("Nenhum profissional foi cadastrado.", statusCode: 500);
        account = (firstDoctor.Name, "medico", (int?)firstDoctor.Id);
    }

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, account.Value.Name),
        new(ClaimTypes.NameIdentifier, username),
        new(ClaimTypes.Role, account.Value.Role)
    };
    if (account.Value.DoctorId is int doctorId) claims.Add(new Claim("doctorId", doctorId.ToString(CultureInfo.InvariantCulture)));
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { username, name = account.Value.Name, role = account.Value.Role, doctorId = account.Value.DoctorId });
});

app.MapGet("/api/auth/me", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var doctorId = int.TryParse(context.User.FindFirst("doctorId")?.Value, out var id) ? id : (int?)null;
    return Results.Ok(new { username = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, name = context.User.Identity.Name, role = context.User.FindFirst(ClaimTypes.Role)?.Value, doctorId });
});
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Sessão encerrada." });
});

app.MapGet("/api/dashboard", async (PiithDb db, HttpContext context) =>
{
    var role = context.User.FindFirst(ClaimTypes.Role)?.Value;
    var doctorId = int.TryParse(context.User.FindFirst("doctorId")?.Value, out var parsedDoctorId) ? parsedDoctorId : (int?)null;
    var patientsQuery = db.PreFichas.AsQueryable();
    var doctorsQuery = db.Doctors.AsQueryable();
    var recordsQuery = db.PointRecords.AsQueryable();
    if (role == "medico" && doctorId is int ownId)
    {
        patientsQuery = patientsQuery.Where(p => p.DoctorId == ownId);
        doctorsQuery = doctorsQuery.Where(d => d.Id == ownId);
        recordsQuery = recordsQuery.Where(r => r.DoctorId == ownId);
    }
    var patients = await patientsQuery.OrderByDescending(p => p.CreatedAt).ToListAsync();
    var doctors = await doctorsQuery.OrderBy(d => d.Name).ToListAsync();
    var pointRecords = await recordsQuery
        .OrderByDescending(r => r.RecordedAt)
        .Take(2000)
        .Join(db.Doctors, r => r.DoctorId, d => d.Id,
            (r, d) => new { r.Id, r.Action, r.RecordedAt, DoctorName = d.Name })
        .ToListAsync();
    return Results.Ok(new
    {
        patients,
        doctors,
        pointRecords,
        totals = new
        {
            waiting = patients.Count(p => p.Status == "Aguardando avaliação"),
            assigned = patients.Count(p => p.Status == "Encaminhado"),
            doctorsOnDuty = doctors.Count(d => d.Status != "Fora de serviço")
        }
    });
});

app.MapPost("/api/prefichas", async (CreatePreFicha input, PiithDb db) =>
{
    if (string.IsNullOrWhiteSpace(input.PatientName) || input.PatientName.Trim().Length < 2)
        return Results.BadRequest(new { message = "Informe um nome válido para a demonstração." });
    if (string.IsNullOrWhiteSpace(input.Symptoms))
        return Results.BadRequest(new { message = "Informe os sintomas." });

    var ficha = new PreFicha
    {
        Protocol = $"PIITH-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid().ToString("N")[..5].ToUpperInvariant()}",
        PatientName = input.PatientName.Trim(),
        Symptoms = input.Symptoms.Trim(),
        Duration = string.IsNullOrWhiteSpace(input.Duration) ? "Não informado" : input.Duration.Trim(),
        PainLevel = Math.Clamp(input.PainLevel, 0, 10),
        IntakeAnswers = JsonSerializer.Serialize(input.Answers ?? new Dictionary<string, string>()),
        CreatedAt = DateTime.UtcNow,
        Status = "Aguardando avaliação",
        Priority = "A definir pela atendente"
    };
    db.PreFichas.Add(ficha);
    await db.SaveChangesAsync();
    return Results.Created($"/api/prefichas/{ficha.Id}", ficha);
});

app.MapPatch("/api/prefichas/{id:int}", async (int id, UpdatePreFicha input, PiithDb db) =>
{
    var ficha = await db.PreFichas.FindAsync(id);
    if (ficha is null) return Results.NotFound(new { message = "Pré-ficha não encontrada." });
    if (string.IsNullOrWhiteSpace(input.PatientName) || input.PatientName.Trim().Length < 2)
        return Results.BadRequest(new { message = "Informe um nome válido." });
    if (string.IsNullOrWhiteSpace(input.Symptoms))
        return Results.BadRequest(new { message = "Informe os sintomas." });

    ficha.PatientName = input.PatientName.Trim();
    ficha.Symptoms = input.Symptoms.Trim();
    ficha.Duration = string.IsNullOrWhiteSpace(input.Duration) ? "Não informado" : input.Duration.Trim();
    ficha.PainLevel = Math.Clamp(input.PainLevel, 0, 10);
    ficha.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ficha);
});

app.MapPatch("/api/prefichas/{id:int}/status", async (int id, UpdatePreFichaStatus input, PiithDb db) =>
{
    var ficha = await db.PreFichas.FindAsync(id);
    if (ficha is null) return Results.NotFound(new { message = "Pré-ficha não encontrada." });

    var allowed = new[] { "Aguardando avaliação", "Encaminhado", "Em atendimento", "Concluído", "Cancelado" };
    if (!allowed.Contains(input.Status))
        return Results.BadRequest(new { message = "Estado de atendimento inválido." });

    var previousStatus = ficha.Status;
    ficha.Status = input.Status;
    ficha.UpdatedAt = DateTime.UtcNow;

    if (ficha.DoctorId is int doctorId)
    {
        var doctor = await db.Doctors.FindAsync(doctorId);
        if (doctor is not null && input.Status == "Concluído")
        {
            doctor.Status = "Disponível";
            doctor.LastReleasedAt = DateTime.UtcNow;
        }
        else if (doctor is not null && input.Status == "Cancelado" && previousStatus is "Encaminhado" or "Em atendimento")
        {
            doctor.Status = "Disponível";
            doctor.LastReleasedAt = DateTime.UtcNow;
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(ficha);
});

app.MapPatch("/api/prefichas/{id:int}/priority", async (int id, UpdatePriority input, PiithDb db) =>
{
    var ficha = await db.PreFichas.FindAsync(id);
    if (ficha is null) return Results.NotFound(new { message = "Pré-ficha não encontrada." });
    var allowed = new[] { "Urgente", "Moderado", "Simples", "Em análise" };
    if (!allowed.Contains(input.Priority)) return Results.BadRequest(new { message = "Prioridade inválida." });
    ficha.Priority = input.Priority;
    ficha.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(ficha);
});

app.MapPatch("/api/prefichas/{id:int}/assign", async (int id, AssignDoctor input, PiithDb db) =>
{
    var ficha = await db.PreFichas.FindAsync(id);
    if (ficha is null) return Results.NotFound(new { message = "Pré-ficha não encontrada." });
    var doctor = await db.Doctors.FindAsync(input.DoctorId);
    if (doctor is null) return Results.NotFound(new { message = "Profissional não encontrado." });
    if (doctor.Status != "Disponível") return Results.BadRequest(new { message = "Este profissional não está marcado como disponível." });
    ficha.DoctorId = doctor.Id;
    ficha.DoctorName = doctor.Name;
    ficha.Status = "Encaminhado";
    ficha.UpdatedAt = DateTime.UtcNow;
    doctor.Status = "Ocupado";
    await db.SaveChangesAsync();
    return Results.Ok(ficha);
});

app.MapPost("/api/doctors/{id:int}/point", async (int id, PointRequest input, PiithDb db, HttpContext context) =>
{
    if (!int.TryParse(context.User.FindFirst("doctorId")?.Value, out var ownDoctorId) || ownDoctorId != id)
        return Results.Forbid();
    var doctor = await db.Doctors.FindAsync(id);
    if (doctor is null) return Results.NotFound(new { message = "Profissional não encontrado." });
    var action = input.Action?.Trim().ToLowerInvariant();
    if (action is not ("entrada" or "saida")) return Results.BadRequest(new { message = "Ação deve ser entrada ou saida." });
    db.PointRecords.Add(new PointRecord { DoctorId = id, Action = action == "entrada" ? "Entrada" : "Saída", RecordedAt = DateTime.UtcNow });
    doctor.Status = action == "entrada" ? "Indeterminado" : "Fora de serviço";
    await db.SaveChangesAsync();
    return Results.Ok(new { message = $"{doctor.Name}: { (action == "entrada" ? "entrada" : "saída") } registrada." });
});

app.MapPatch("/api/doctors/{id:int}/status", async (int id, UpdateDoctorStatus input, PiithDb db) =>
{
    var doctor = await db.Doctors.FindAsync(id);
    if (doctor is null) return Results.NotFound(new { message = "Profissional não encontrado." });
    var allowed = new[] { "Disponível", "Ocupado", "Indeterminado", "Fora de serviço" };
    if (!allowed.Contains(input.Status)) return Results.BadRequest(new { message = "Estado inválido." });
    if (doctor.Status == "Ocupado" && input.Status == "Disponível") doctor.LastReleasedAt = DateTime.UtcNow;
    doctor.Status = input.Status;
    await db.SaveChangesAsync();
    return Results.Ok(doctor);
});

app.MapFallbackToFile("index.html");
app.Run();

static string GetConnectionString(IConfiguration config)
{
    var configured = config.GetConnectionString("Default");
    if (!string.IsNullOrWhiteSpace(configured)) return configured;
    var url = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
    {
        var userInfo = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.TrimStart('/');
        return $"Host={uri.Host};Port={uri.Port};Database={database};Username={user};Password={password};SSL Mode=Require;Trust Server Certificate=true";
    }
    // Fallback para execução local; configure DATABASE_URL para persistência em produção.
    return "Host=localhost;Port=5432;Database=piith;Username=postgres;Password=postgres";
}

namespace PIITH
{
    public class PiithDb(DbContextOptions<PiithDb> options) : DbContext(options)
    {
        public DbSet<PreFicha> PreFichas => Set<PreFicha>();
        public DbSet<Doctor> Doctors => Set<Doctor>();
        public DbSet<PointRecord> PointRecords => Set<PointRecord>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PreFicha>().HasIndex(p => p.Protocol).IsUnique();
            modelBuilder.Entity<PreFicha>().Property(p => p.PatientName).HasMaxLength(120);
            modelBuilder.Entity<PreFicha>().Property(p => p.Symptoms).HasMaxLength(1200);
            modelBuilder.Entity<PreFicha>().Property(p => p.IntakeAnswers).HasColumnType("text");
            modelBuilder.Entity<Doctor>().Property(d => d.Name).HasMaxLength(120);
        }
    }

    public class PreFicha
    {
        public int Id { get; set; }
        public string Protocol { get; set; } = "";
        public string PatientName { get; set; } = "";
        public string Symptoms { get; set; } = "";
        public string Duration { get; set; } = "";
        public int PainLevel { get; set; }
        public string IntakeAnswers { get; set; } = "{}";
        public string Priority { get; set; } = "A definir pela atendente";
        public string Status { get; set; } = "Aguardando avaliação";
        public int? DoctorId { get; set; }
        public string? DoctorName { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
    public class Doctor
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Specialty { get; set; } = "";
        public string Status { get; set; } = "Indeterminado";
        public DateTime LastReleasedAt { get; set; }
    }
    public class PointRecord
    {
        public int Id { get; set; }
        public int DoctorId { get; set; }
        public string Action { get; set; } = "";
        public DateTime RecordedAt { get; set; }
    }
    public record LoginRequest(string Username, string Password);
    public record CreatePreFicha(string PatientName, string Symptoms, string? Duration, int PainLevel, Dictionary<string, string>? Answers = null);
    public record UpdatePreFicha(string PatientName, string Symptoms, string? Duration, int PainLevel);
    public record UpdatePreFichaStatus(string Status);
    public record UpdatePriority(string Priority);
    public record AssignDoctor(int DoctorId);
    public record PointRequest(string Action);
    public record UpdateDoctorStatus(string Status);
}
