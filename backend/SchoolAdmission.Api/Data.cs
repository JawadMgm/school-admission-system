using Microsoft.EntityFrameworkCore;

namespace SchoolAdmission.Api;

public class SchoolClass
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int TotalSeats { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Stored status is HELD, CONFIRMED or CANCELLED. EXPIRED is derived from ExpiresAt.</summary>
public class Hold
{
    public string Id { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Status { get; set; } = "HELD";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<SchoolClass> Classes => Set<SchoolClass>();
    public DbSet<Hold> Holds => Set<Hold>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SchoolClass>().ToTable("Classes").HasKey(c => c.Id);

        var h = b.Entity<Hold>();
        h.ToTable("Holds").HasKey(x => x.Id);
        h.HasOne<SchoolClass>().WithMany().HasForeignKey(x => x.ClassId);
        h.HasIndex(x => new { x.ClassId, x.Status });
        h.HasIndex(x => x.StudentId);
    }
}
