using System.Text;
using Mailserver.Core;
using Mailserver.Core.Accounts;
using Mailserver.Core.Queue;

namespace Mailserver.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Verifies_correct_password_only()
    {
        var hash = PasswordHasher.Hash("geheim-123456");

        Assert.True(PasswordHasher.Verify("geheim-123456", hash));
        Assert.False(PasswordHasher.Verify("geheim-123457", hash));
        Assert.False(PasswordHasher.Verify("geheim-123456", null));
        Assert.False(PasswordHasher.Verify("geheim-123456", "garbage"));
    }
}

public class EmailAddressTests
{
    [Theory]
    [InlineData("Max@Example.DE", "Max", "example.de")]
    [InlineData("<max@example.de>", "max", "example.de")]
    [InlineData("info@müller.de", "info", "xn--mller-kva.de")]
    public void Parses_and_normalizes(string input, string local, string domain)
    {
        var address = EmailAddress.Parse(input);

        Assert.Equal(local, address.LocalPart);
        Assert.Equal(domain, address.Domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("@example.de")]
    [InlineData("max@")]
    [InlineData("max@localhost")]
    [InlineData("ma x@example.de")]
    public void Rejects_invalid(string input) => Assert.False(EmailAddress.TryParse(input, out _));

    [Fact]
    public void Compares_case_insensitively() =>
        Assert.Equal(EmailAddress.Parse("MAX@example.de"), EmailAddress.Parse("max@EXAMPLE.de"));
}

public class AccountStoreTests : TestData
{
    private static readonly EmailAddress Alice = EmailAddress.Parse("alice@example.test");
    private static readonly EmailAddress Bob = EmailAddress.Parse("bob@example.test");

    public AccountStoreTests()
    {
        Accounts.AddDomain("example.test");
        Accounts.AddAccount(Alice, "password-alice");
        Accounts.AddAccount(Bob, "password-bob");
    }

    [Fact]
    public void Authenticates_with_full_address_case_insensitively()
    {
        Assert.NotNull(Accounts.Authenticate("ALICE@example.test", "password-alice"));
        Assert.Null(Accounts.Authenticate("alice@example.test", "password-bob"));
        Assert.Null(Accounts.Authenticate("nobody@example.test", "password-alice"));
    }

    [Fact]
    public void Disabled_accounts_cannot_log_in_or_receive()
    {
        Accounts.SetEnabled(Alice, false);

        Assert.Null(Accounts.Authenticate("alice@example.test", "password-alice"));
        Assert.True(Accounts.Resolve(Alice).IsEmpty);
    }

    [Fact]
    public void Resolves_nested_aliases_and_external_forwards()
    {
        var team = EmailAddress.Parse("team@example.test");
        var all = EmailAddress.Parse("all@example.test");
        Accounts.AddAlias(team, [Alice, Bob]);
        Accounts.AddAlias(all, [team, Alice, EmailAddress.Parse("boss@elsewhere.test")]);

        var resolution = Accounts.Resolve(all);

        Assert.Equal([Alice, Bob], resolution.LocalAccounts.Select(a => a.Address).Order(Comparer<EmailAddress>.Create((x, y) => string.CompareOrdinal(x.ToString(), y.ToString()))));
        Assert.Equal([EmailAddress.Parse("boss@elsewhere.test")], resolution.ExternalAddresses);
    }

    [Fact]
    public void Alias_loops_terminate()
    {
        var a = EmailAddress.Parse("a@example.test");
        var b = EmailAddress.Parse("b@example.test");
        Accounts.AddAlias(a, [b]);
        Accounts.AddAlias(b, [a, Alice]);

        Assert.Equal([Alice], Accounts.Resolve(a).LocalAccounts.Select(x => x.Address));
    }

    [Fact]
    public void Unknown_addresses_resolve_empty()
    {
        Assert.True(Accounts.Resolve(EmailAddress.Parse("nobody@example.test")).IsEmpty);
        Assert.True(Accounts.Resolve(EmailAddress.Parse("someone@elsewhere.test")).IsEmpty);
    }

    [Fact]
    public void Users_may_send_as_themselves_and_their_aliases_only()
    {
        Accounts.AddAlias(EmailAddress.Parse("sales@example.test"), [Alice]);
        var alice = Accounts.FindAccount(Alice)!;

        Assert.True(Accounts.MaySendAs(alice, Alice));
        Assert.True(Accounts.MaySendAs(alice, EmailAddress.Parse("sales@example.test")));
        Assert.False(Accounts.MaySendAs(alice, Bob));
    }

    [Fact]
    public void Account_and_alias_names_cannot_collide()
    {
        Assert.Throws<InvalidOperationException>(() => Accounts.AddAlias(Alice, [Bob]));
        Accounts.AddAlias(EmailAddress.Parse("x@example.test"), [Bob]);
        Assert.Throws<InvalidOperationException>(() => Accounts.AddAccount(EmailAddress.Parse("x@example.test"), "pw-123456789"));
    }
}

public class MailboxStoreTests : TestData
{
    [Fact]
    public async Task Assigns_ascending_uids_and_tracks_usage()
    {
        Accounts.AddDomain("example.test");
        var account = Accounts.AddAccount(EmailAddress.Parse("alice@example.test"), "password-alice", quotaBytes: 20);

        var first = await Mailboxes.AppendAsync(account, Encoding.ASCII.GetBytes("Subject: 1\r\n\r\nHello"));
        var second = await Mailboxes.AppendAsync(account, Encoding.ASCII.GetBytes("Subject: 2\r\n\r\nWorld"));

        Assert.Equal(1, first.Uid);
        Assert.Equal(2, second.Uid);
        Assert.Equal(first.Size + second.Size, Mailboxes.GetUsage(account.Id));
        Assert.True(Mailboxes.IsOverQuota(account));
        await using var stream = Mailboxes.OpenMessage(second);
        Assert.EndsWith("World", await new StreamReader(stream).ReadToEndAsync());
    }

    [Fact]
    public void Inbox_name_is_case_insensitive()
    {
        Accounts.AddDomain("example.test");
        var account = Accounts.AddAccount(EmailAddress.Parse("alice@example.test"), "password-alice");

        Assert.Equal(Mailboxes.GetOrCreateFolder(account.Id, "inbox").Id, Mailboxes.GetOrCreateFolder(account.Id, "INBOX").Id);
    }
}

public class OutboundQueueTests : TestData
{
    [Fact]
    public async Task Groups_by_domain_and_removes_file_after_completion()
    {
        var queue = new OutboundQueue(Database, Paths);
        await queue.EnqueueAsync(Encoding.ASCII.GetBytes("Subject: x\r\n\r\nbody"), "alice@example.test",
            [EmailAddress.Parse("a@one.test"), EmailAddress.Parse("b@one.test"), EmailAddress.Parse("c@two.test")]);

        var batches = queue.GetDueBatches(DateTimeOffset.UtcNow);
        Assert.Equal(2, batches.Count);
        Assert.Equal(2, batches.Single(b => b.Domain == "one.test").Entries.Count);

        queue.Defer(batches[0].Entries, "busy");
        Assert.Single(queue.GetDueBatches(DateTimeOffset.UtcNow));

        queue.Complete(batches.SelectMany(b => b.Entries));
        Assert.Empty(queue.List());
        Assert.Empty(System.IO.Directory.GetFiles(Paths.QueueRoot));
    }
}
