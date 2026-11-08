using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Ledger.Tests;

/// <summary>Every test gets its own database file and goes through the real HTTP pipeline.</summary>
public sealed class LedgerApiTests : IDisposable
{
    private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"ledger-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    private record AccountView(long Id, string Owner, long Balance);
    private record TransferView(long Id, long? FromAccountId, long ToAccountId, long Amount);
    private record EntryView(long Id, long AccountId, long TransferId, long Amount);

    public LedgerApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Ledger", $"Data Source={dbPath}"));
        client = factory.CreateClient();
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) File.Delete(f);
    }

    private async Task<long> AccountWith(long balance)
    {
        var created = await client.PostAsJsonAsync("/api/accounts", new { owner = "Анна" });
        var account = (await created.Content.ReadFromJsonAsync<AccountView>())!;
        if (balance > 0) Assert.Equal(HttpStatusCode.Created, (await Deposit(account.Id, balance, Guid.NewGuid().ToString())).StatusCode);
        return account.Id;
    }

    private Task<HttpResponseMessage> Deposit(long id, long amount, string key) =>
        Send($"/api/accounts/{id}/deposits", new { amount }, key);

    private Task<HttpResponseMessage> Transfer(long from, long to, long amount, string key) =>
        Send("/api/transfers", new { from, to, amount }, key);

    private Task<HttpResponseMessage> Send(string url, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private async Task<long> Balance(long id) =>
        (await client.GetFromJsonAsync<AccountView>($"/api/accounts/{id}"))!.Balance;

    private async Task<long> StatementSum(long id) =>
        (await client.GetFromJsonAsync<List<EntryView>>($"/api/accounts/{id}/statement"))!.Sum(e => e.Amount);

    [Fact]
    public async Task Deposit_adds_money()
    {
        var a = await AccountWith(0);
        Assert.Equal(HttpStatusCode.Created, (await Deposit(a, 500, "d1")).StatusCode);
        Assert.Equal(500, await Balance(a));
    }

    [Fact]
    public async Task Transfer_moves_money_and_writes_both_sides()
    {
        var (a, b) = (await AccountWith(1000), await AccountWith(0));
        Assert.Equal(HttpStatusCode.Created, (await Transfer(a, b, 300, "t1")).StatusCode);
        Assert.Equal(700, await Balance(a));
        Assert.Equal(300, await Balance(b));
        Assert.Equal(700, await StatementSum(a));
        Assert.Equal(300, await StatementSum(b));
    }

    [Fact]
    public async Task Insufficient_funds_is_conflict_and_changes_nothing()
    {
        var (a, b) = (await AccountWith(100), await AccountWith(0));
        var response = await Transfer(a, b, 101, "t1");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(100, await Balance(a));
        Assert.Equal(0, await Balance(b));
    }

    [Fact]
    public async Task A_failed_transfer_does_not_use_up_its_key()
    {
        var (a, b) = (await AccountWith(100), await AccountWith(0));
        await Transfer(a, b, 200, "retry");
        await Deposit(a, 100, "top-up");
        Assert.Equal(HttpStatusCode.Created, (await Transfer(a, b, 200, "retry")).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_000_000_001)]
    public async Task Bad_amount_is_rejected(long amount)
    {
        var (a, b) = (await AccountWith(100), await AccountWith(0));
        Assert.Equal(HttpStatusCode.BadRequest, (await Transfer(a, b, amount, "t1")).StatusCode);
    }

    [Fact]
    public async Task Same_account_is_rejected()
    {
        var a = await AccountWith(100);
        Assert.Equal(HttpStatusCode.BadRequest, (await Transfer(a, a, 10, "t1")).StatusCode);
    }

    [Fact]
    public async Task Missing_key_is_rejected()
    {
        var (a, b) = (await AccountWith(100), await AccountWith(0));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send("/api/transfers", new { from = a, to = b, amount = 1 }, null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_accounts_are_not_found()
    {
        var a = await AccountWith(100);
        Assert.Equal(HttpStatusCode.NotFound, (await Transfer(a, 999_999, 1, "t1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Transfer(999_999, a, 1, "t2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/accounts/999999")).StatusCode);
        Assert.Equal(100, await Balance(a));
    }

    [Fact]
    public async Task Retry_with_the_same_key_moves_money_once()
    {
        var (a, b) = (await AccountWith(1000), await AccountWith(0));
        var first = await Transfer(a, b, 300, "t1");
        var second = await Transfer(a, b, 300, "t1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        var (t1, t2) = (await first.Content.ReadFromJsonAsync<TransferView>(), await second.Content.ReadFromJsonAsync<TransferView>());
        Assert.Equal(t1!.Id, t2!.Id);
        Assert.Equal(700, await Balance(a));
    }

    [Fact]
    public async Task Same_key_for_a_different_request_is_refused()
    {
        var (a, b) = (await AccountWith(1000), await AccountWith(0));
        await Transfer(a, b, 300, "t1");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Transfer(a, b, 400, "t1")).StatusCode);
        Assert.Equal(700, await Balance(a));
    }

    [Fact]
    public async Task Parallel_transfers_cannot_overdraw()
    {
        var (a, b) = (await AccountWith(5000), await AccountWith(0));
        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Transfer(a, b, 100, $"t{i}")));
        Assert.Equal(50, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(150, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0, await Balance(a));
        Assert.Equal(5000, await Balance(b));
    }

    [Fact]
    public async Task Parallel_retries_of_one_request_move_money_once()
    {
        var (a, b) = (await AccountWith(1000), await AccountWith(0));
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Transfer(a, b, 100, "same")));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(29, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(900, await Balance(a));
    }

    [Fact]
    public async Task Money_is_conserved_when_accounts_pay_each_other()
    {
        var accounts = new[] { await AccountWith(1000), await AccountWith(1000), await AccountWith(1000) };
        var random = new Random(42);
        var work = Enumerable.Range(0, 150).Select(i =>
        {
            var from = accounts[random.Next(3)];
            var to = accounts.First(x => x != from);
            return Transfer(from, to, random.Next(1, 400), $"mix{i}");
        });
        await Task.WhenAll(work);
        long total = 0;
        foreach (var id in accounts)
        {
            var balance = await Balance(id);
            Assert.True(balance >= 0);
            Assert.Equal(balance, await StatementSum(id));
            total += balance;
        }
        Assert.Equal(3000, total);
    }
}
