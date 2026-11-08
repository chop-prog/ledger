using Microsoft.EntityFrameworkCore;

namespace Ledger;

public class Account
{
    public long Id { get; set; }
    public required string Owner { get; set; }

    /// <summary>Kopecks, never a floating-point number. Changed only by the atomic updates in LedgerService.</summary>
    public long Balance { get; set; }
}

public class Transfer
{
    public long Id { get; set; }
    public required string Key { get; set; }

    /// <summary>Null for a deposit: the money comes from outside the ledger.</summary>
    public long? FromAccountId { get; set; }
    public long ToAccountId { get; set; }
    public long Amount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One line of an account's history. The entries of an account always add up to its balance.</summary>
public class Entry
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long TransferId { get; set; }
    public long Amount { get; set; }
}

public class LedgerDb(DbContextOptions<LedgerDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<Entry> Entries => Set<Entry>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Last line of defence: even a bug in the code can't save a negative balance.
        model.Entity<Account>().ToTable(t => t.HasCheckConstraint("CK_Account_Balance", "Balance >= 0"));
        model.Entity<Transfer>().HasIndex(t => t.Key).IsUnique();
        model.Entity<Entry>().HasIndex(e => e.AccountId);
    }
}
