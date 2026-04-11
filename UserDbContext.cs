using Microsoft.EntityFrameworkCore;
using FinLedger.Models;

namespace FinLedger;

/// <summary>
/// Separate database for user authentication — stored in users.db
/// Completely separate from finledger.db
/// </summary>
public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).IsRequired().HasMaxLength(80);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Role).HasDefaultValue("viewer");
        });
    }

    public static async Task SeedAsync(UserDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        if (!await db.Users.AnyAsync())
        {
            db.Users.Add(new User
            {
                Username     = "admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
                Role         = "admin",
                CreatedAt    = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }
}
