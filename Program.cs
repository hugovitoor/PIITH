using Microsoft.EntityFrameworkCore;
using PIITH;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
var connectionString = GetConnectionString(builder.Configuration);
builder.Services.AddDbContext<PiithDb>(options => options.UseNpgsql(connectionString));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Protótipo de demonstração: use somente dados fictícios até adicionar autenticação e controles LGPD.
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

app.MapGet("/api/dashboard", async (PiithDb db) =>
{
    var patients = await db.PreFichas.OrderByDescending(p => p.CreatedAt).ToListAsync();
    var doctors = await db.Doctors.OrderBy(d => d.Name).ToListAsync();
    var pointRecords = await db.PointRecords
        .OrderByDescending(r => r.RecordedAt)
        .Take(30)
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

app.MapPost("/api/doctors/{id:int}/point", async (int id, PointRequest input, PiithDb db) =>
{
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
    public record CreatePreFicha(string PatientName, string Symptoms, string? Duration, int PainLevel, Dictionary<string, string>? Answers = null);
    public record UpdatePreFicha(string PatientName, string Symptoms, string? Duration, int PainLevel);
    public record UpdatePreFichaStatus(string Status);
    public record UpdatePriority(string Priority);
    public record AssignDoctor(int DoctorId);
    public record PointRequest(string Action);
    public record UpdateDoctorStatus(string Status);
}
