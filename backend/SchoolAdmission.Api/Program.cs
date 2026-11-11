using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from env var ConnectionStrings__Default (or appsettings).
builder.Services.AddDbContext<AppDb>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));
builder.Services.AddScoped<AdmissionService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ApiException e)
    {
        ctx.Response.StatusCode = e.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = new { code = e.Code, message = e.Message } });
    }
});

// Create tables on startup; retry because PostgreSQL may still be starting.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); break; }
        catch when (attempt < 30) { await Task.Delay(1000); }
    }
}

// X-Student-Id is the student's 17-digit birth registration number (spec §1.4).
static string Student(string? header)
{
    var id = header?.Trim();
    return id is { Length: 17 } && id.All(char.IsAsciiDigit)
        ? id
        : throw new ApiException(400, "INVALID_STUDENT_ID", "X-Student-Id must be a 17-digit birth registration number.");
}

app.MapPost("/classes", async (CreateClassRequest r, AdmissionService s) =>
{
    var c = await s.CreateClass(r.Name, r.TotalSeats);
    return Results.Created($"/classes/{c.Id}", c);
});
app.MapGet("/classes", async (AdmissionService s) => await s.GetClasses());
app.MapGet("/classes/{classId}", async (string classId, AdmissionService s) => await s.GetClass(classId));

app.MapPost("/classes/{classId}/holds", async (string classId, [FromHeader(Name = "X-Student-Id")] string? studentId, AdmissionService s) =>
{
    var h = await s.CreateHold(classId, Student(studentId));
    return Results.Created($"/holds/{h.HoldId}", h);
});
app.MapGet("/holds/{holdId}", async (string holdId, AdmissionService s) => await s.GetHold(holdId));
app.MapPost("/holds/{holdId}/confirm", async (string holdId, [FromHeader(Name = "X-Student-Id")] string? studentId, AdmissionService s) =>
{
    var h = await s.Confirm(holdId, Student(studentId));
    return new { holdId = h.HoldId, status = h.Status };
});
app.MapDelete("/holds/{holdId}", async (string holdId, [FromHeader(Name = "X-Student-Id")] string? studentId, AdmissionService s) =>
{
    await s.Cancel(holdId, Student(studentId));
    return Results.NoContent();
});
app.MapGet("/me/holds", async ([FromHeader(Name = "X-Student-Id")] string? studentId, AdmissionService s) =>
    await s.MyHolds(Student(studentId)));

app.Run();

record CreateClassRequest(string? Name, int? TotalSeats);

public partial class Program { } // lets the test project use WebApplicationFactory<Program>
