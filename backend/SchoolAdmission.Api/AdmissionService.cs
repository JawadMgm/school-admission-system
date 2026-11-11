using Microsoft.EntityFrameworkCore;

namespace SchoolAdmission.Api;

public class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public record ClassDto(string Id, string Name, int TotalSeats, int HeldSeats, int HoldSeats, int ConfirmedSeats, int AvailableSeats);
public record HoldDto(string HoldId, string ClassId, string StudentId, string Status, DateTime ExpiresAt);
public record MyHoldDto(string HoldId, string ClassId, string ClassName, string Status, DateTime ExpiresAt);

public class AdmissionService(AppDb db, IConfiguration config)
{
    private const string Held = "HELD", Confirmed = "CONFIRMED", Cancelled = "CANCELLED", Expired = "EXPIRED";

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];

    private static string Effective(Hold h, DateTime now) =>
        h.Status == Held && h.ExpiresAt <= now ? Expired : h.Status;

    private static HoldDto ToDto(Hold h, DateTime now) => new(h.Id, h.ClassId, h.StudentId, Effective(h, now), h.ExpiresAt);

    // --- Classes -----------------------------------------------------------

    public async Task<ClassDto> CreateClass(string? name, int? totalSeats)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ApiException(400, "INVALID_NAME", "Class name is required (max 200 characters).");
        if (totalSeats is null or < 1 or > 100_000)
            throw new ApiException(400, "INVALID_SEATS", "totalSeats must be a whole number between 1 and 100000.");

        var c = new SchoolClass { Id = NewId("c_"), Name = name.Trim(), TotalSeats = totalSeats.Value, CreatedAt = DateTime.UtcNow };
        db.Classes.Add(c);
        await db.SaveChangesAsync();
        return new ClassDto(c.Id, c.Name, c.TotalSeats, 0, 0, 0, c.TotalSeats);
    }

    public async Task<List<ClassDto>> GetClasses() => await QueryClasses(db.Classes);

    public async Task<ClassDto> GetClass(string classId) =>
        (await QueryClasses(db.Classes.Where(c => c.Id == classId))).FirstOrDefault()
        ?? throw new ApiException(404, "CLASS_NOT_FOUND", "Class not found.");

    private async Task<List<ClassDto>> QueryClasses(IQueryable<SchoolClass> source)
    {
        var now = DateTime.UtcNow;
        var rows = await source.OrderBy(c => c.CreatedAt).Select(c => new
        {
            c.Id, c.Name, c.TotalSeats,
            Held = db.Holds.Count(h => h.ClassId == c.Id && h.Status == Held && h.ExpiresAt > now),
            Confirmed = db.Holds.Count(h => h.ClassId == c.Id && h.Status == Confirmed),
        }).ToListAsync();
        return rows.Select(r => new ClassDto(r.Id, r.Name, r.TotalSeats, r.Held, r.Held, r.Confirmed,
            Math.Max(0, r.TotalSeats - r.Held - r.Confirmed))).ToList();
    }

    // --- Holds -------------------------------------------------------------

    public async Task<HoldDto> CreateHold(string classId, string studentId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        // Row lock on the class: concurrent requests for the same class queue up here,
        // across threads AND across processes, because the lock lives in PostgreSQL.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Classes\" WHERE \"Id\" = {classId} FOR UPDATE");
        var cls = await db.Classes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == classId)
            ?? throw new ApiException(404, "CLASS_NOT_FOUND", "Class not found.");

        var now = DateTime.UtcNow;
        var active = db.Holds.Where(h => h.ClassId == classId &&
            (h.Status == Confirmed || (h.Status == Held && h.ExpiresAt > now)));

        if (await active.AnyAsync(h => h.StudentId == studentId))
            throw new ApiException(409, "ALREADY_APPLIED", "You already have an active application for this class.");
        if (await active.CountAsync() >= cls.TotalSeats)
            throw new ApiException(409, "CLASS_FULL", "No seats available in this class.");

        var seconds = config.GetValue("HOLD_DURATION_SECONDS", 600);
        var expires = now.AddSeconds(seconds);
        var hold = new Hold
        {
            Id = NewId("h_"), ClassId = classId, StudentId = studentId, Status = Held, CreatedAt = now,
            ExpiresAt = new DateTime(expires.Ticks - expires.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ToDto(hold, now);
    }

    public async Task<HoldDto> GetHold(string holdId)
    {
        var h = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId)
            ?? throw new ApiException(404, "HOLD_NOT_FOUND", "Hold not found.");
        return ToDto(h, DateTime.UtcNow);
    }

    public async Task<HoldDto> Confirm(string holdId, string studentId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var h = await LockHold(holdId, studentId);
        var now = DateTime.UtcNow;

        switch (Effective(h, now))
        {
            case Confirmed: break; // idempotent: confirming twice is a no-op, never double counts
            case Held:
                h.Status = Confirmed;
                await db.SaveChangesAsync();
                break;
            case Expired:
                throw new ApiException(409, "HOLD_EXPIRED", "This hold has expired. Please apply again.");
            default:
                throw new ApiException(409, "HOLD_CANCELLED", "This application was cancelled.");
        }
        await tx.CommitAsync();
        return ToDto(h, now);
    }

    public async Task Cancel(string holdId, string studentId)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var h = await LockHold(holdId, studentId);

        if (h.Status == Confirmed)
            throw new ApiException(409, "ALREADY_CONFIRMED", "A confirmed admission cannot be cancelled here.");
        if (Effective(h, DateTime.UtcNow) == Held)
        {
            h.Status = Cancelled;
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync(); // CANCELLED / EXPIRED: nothing to release, so it is a no-op
    }

    public async Task<List<MyHoldDto>> MyHolds(string studentId)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Holds.AsNoTracking().Where(h => h.StudentId == studentId)
            .OrderByDescending(h => h.CreatedAt)
            .Join(db.Classes, h => h.ClassId, c => c.Id, (h, c) => new { Hold = h, ClassName = c.Name })
            .ToListAsync();
        return rows.Select(r => new MyHoldDto(r.Hold.Id, r.Hold.ClassId, r.ClassName, Effective(r.Hold, now), r.Hold.ExpiresAt)).ToList();
    }

    private async Task<Hold> LockHold(string holdId, string studentId)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Holds\" WHERE \"Id\" = {holdId} FOR UPDATE");
        var h = await db.Holds.FirstOrDefaultAsync(x => x.Id == holdId)
            ?? throw new ApiException(404, "HOLD_NOT_FOUND", "Hold not found.");
        if (h.StudentId != studentId)
            throw new ApiException(403, "FORBIDDEN", "This application belongs to another student.");
        return h;
    }
}
