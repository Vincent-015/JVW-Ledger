using Microsoft.EntityFrameworkCore;
using FinLedger.Models;

namespace FinLedger;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).IsRequired().HasMaxLength(120);
            e.Property(a => a.Type).HasDefaultValue("savings");
            e.Property(a => a.Balance).HasColumnType("REAL");
            e.HasMany(a => a.Transactions)
             .WithOne(t => t.Account)
             .HasForeignKey(t => t.AccountId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Type).HasDefaultValue("deposit");
            e.Property(t => t.Amount).HasColumnType("REAL");
            e.Property(t => t.Status).HasDefaultValue("completed");
            e.HasIndex(t => t.AccountId);
            e.HasIndex(t => t.Ref);
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(200);
            e.Property(p => p.Price).HasColumnType("REAL");
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.FirstName).IsRequired().HasMaxLength(80);
            e.Property(c => c.LastName).IsRequired().HasMaxLength(80);
            e.HasIndex(c => c.Email);
        });
    }

    public static async Task SeedAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (await db.Accounts.AnyAsync()) return;

        var now = DateTime.UtcNow;
        var a1 = new Account { Name = "Main Savings",      Type = "savings",    Balance = 125000, Description = "Primary savings account", CreatedAt = now, UpdatedAt = now };
        var a2 = new Account { Name = "Business Checking", Type = "business",   Balance = 48500,  Description = "Business operations",      CreatedAt = now, UpdatedAt = now };
        var a3 = new Account { Name = "Investment Fund",   Type = "investment", Balance = 320000, Description = "Long-term investments",     CreatedAt = now, UpdatedAt = now };
        db.Accounts.AddRange(a1, a2, a3);
        await db.SaveChangesAsync();

        db.Transactions.AddRange(
            new Transaction { AccountId = a1.Id, Type = "deposit", Amount =  125000, Description = "Initial deposit",       Status = "completed", Ref = "TXN-INIT-001", CreatedAt = now },
            new Transaction { AccountId = a2.Id, Type = "deposit", Amount =   50000, Description = "Business startup fund", Status = "completed", Ref = "TXN-INIT-002", CreatedAt = now },
            new Transaction { AccountId = a2.Id, Type = "payment", Amount =   -1500, Description = "Office supplies",       Status = "completed", Ref = "TXN-INIT-003", CreatedAt = now }
        );
        db.Products.AddRange(
            new Product { Name = "Wireless Earbuds Pro", Category = "electronics", Price = 2499, Stock = 50,  Status = "active", Description = "High-quality wireless earbuds", CreatedAt = now, UpdatedAt = now },
            new Product { Name = "Cotton T-Shirt",       Category = "clothing",    Price =  499, Stock = 200, Status = "active", Description = "Premium cotton shirt",           CreatedAt = now, UpdatedAt = now },
            new Product { Name = "Consultation Service", Category = "services",    Price = 5000, Stock = 999, Status = "active", Description = "Business consulting per hour",   CreatedAt = now, UpdatedAt = now }
        );
        db.Customers.AddRange(
            new Customer { FirstName = "Maria", LastName = "Santos", Email = "maria.santos@gmail.com", Phone = "+63 912 345 6789", City = "Cebu City",  CreatedAt = now, UpdatedAt = now },
            new Customer { FirstName = "Jose",  LastName = "Reyes",  Email = "jose.reyes@yahoo.com",   Phone = "+63 917 654 3210", City = "Davao City", CreatedAt = now, UpdatedAt = now }
        );
        await db.SaveChangesAsync();
    }
}
