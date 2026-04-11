namespace FinLedger.Models;

public class Transaction
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Type { get; set; } = "deposit";
    public double Amount { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "completed";
    public string? Ref { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Account? Account { get; set; }
}
