using Microsoft.EntityFrameworkCore;

namespace Ledger;

public class LedgerException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public class LedgerService(LedgerDb db, TimeProvider time)
{
    public const long MaxAmount = 1_000_000_000_00; // 1 billion rubles in kopecks

    public async Task<Account> CreateAccountAsync(string owner)
    {
        var name = owner.Trim();
        if (name.Length is 0 or > 200) throw new LedgerException(400, "Owner must be 1 to 200 characters");
        var account = new Account { Owner = name };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    public async Task<Account> GetAccountAsync(long id) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id)
        ?? throw new LedgerException(404, $"No account {id}");

    public async Task<List<Entry>> StatementAsync(long id)
    {
        await GetAccountAsync(id);
        return await db.Entries.AsNoTracking().Where(e => e.AccountId == id).OrderBy(e => e.Id).ToListAsync();
    }

    /// <summary>
    /// Moves money, or adds it when <paramref name="from"/> is null. Repeating a request with the
    /// same key returns the first result and moves nothing, so a client can safely retry after a timeout.
    /// </summary>
    /// <returns>The transfer and whether it is a replay of an earlier request.</returns>
    public async Task<(Transfer Transfer, bool Replayed)> MoveAsync(string key, long? from, long to, long amount)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100) throw new LedgerException(400, "Idempotency-Key is required, up to 100 characters");
        if (amount is <= 0 or > MaxAmount) throw new LedgerException(400, "Amount must be positive and within the limit");
        if (from == to) throw new LedgerException(400, "Cannot transfer to the same account");

        // On SQLite this is BEGIN IMMEDIATE: writers queue up here, so the steps below can't interleave.
        await using var tx = await db.Database.BeginTransactionAsync();

        var earlier = await db.Transfers.AsNoTracking().SingleOrDefaultAsync(t => t.Key == key);
        if (earlier is not null)
        {
            if (earlier.FromAccountId != from || earlier.ToAccountId != to || earlier.Amount != amount)
                throw new LedgerException(422, "This Idempotency-Key was used for a different request");
            return (earlier, true);
        }

        if (!await db.Accounts.AnyAsync(a => a.Id == to)) throw new LedgerException(404, $"No account {to}");
        if (from is long source)
        {
            if (!await db.Accounts.AnyAsync(a => a.Id == source)) throw new LedgerException(404, $"No account {source}");
            // Check and change in one statement: reading the balance first and saving later could overdraw.
            var debited = await db.Accounts
                .Where(a => a.Id == source && a.Balance >= amount)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Balance, a => a.Balance - amount));
            if (debited == 0) throw new LedgerException(409, "Insufficient funds");
        }
        await db.Accounts.Where(a => a.Id == to)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Balance, a => a.Balance + amount));

        var transfer = new Transfer { Key = key, FromAccountId = from, ToAccountId = to, Amount = amount, CreatedAt = time.GetUtcNow() };
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync();

        if (from is long payer) db.Entries.Add(new Entry { AccountId = payer, TransferId = transfer.Id, Amount = -amount });
        db.Entries.Add(new Entry { AccountId = to, TransferId = transfer.Id, Amount = amount });
        await db.SaveChangesAsync();

        await tx.CommitAsync();
        return (transfer, false);
    }
}
