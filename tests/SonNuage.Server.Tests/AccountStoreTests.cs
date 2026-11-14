using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonNuage.Contracts;
using SonNuage.UI.Services;

namespace SonNuage.Server.Tests;

public class AccountStoreTests
{
    private static readonly Uri ServerA = new("https://a.example.org/");
    private static readonly Uri ServerB = new("https://b.example.org/musique/");

    [Fact]
    public async Task The_single_session_of_an_old_install_becomes_the_first_account()
    {
        var store = new MemoryStore();
        var tokens = Tokens("alice", "access-1");
        store.Data["sonnuage.server"] = ServerA.ToString();
        store.Data["sonnuage.session"] = Json(tokens);
        store.Data["sonnuage.queue"] = "{\"ids\":[]}";
        store.Data["sonnuage.quality"] = "high";

        var accounts = new AccountStore(store, multiAccount: true);
        await accounts.LoadAsync();

        var account = Assert.Single(accounts.Accounts);
        Assert.Same(account, accounts.Active);
        Assert.Equal(ServerA, account.Server);
        Assert.Equal("alice", account.Username);
        Assert.Equal(tokens.User.Id, account.UserId);
        Assert.Equal(Json(tokens), store.Data[account.SessionKey]);
        Assert.Equal("{\"ids\":[]}", store.Data[accounts.ScopedKey("sonnuage.queue")]);
        Assert.Equal("high", store.Data[accounts.ScopedKey("sonnuage.quality")]);
        foreach (var legacy in new[] { "sonnuage.server", "sonnuage.session", "sonnuage.queue", "sonnuage.quality" })
        {
            Assert.False(store.Data.ContainsKey(legacy), legacy);
        }

        // La migration n'a lieu qu'une fois : un nouveau démarrage relit la liste.
        var reloaded = new AccountStore(store, multiAccount: true);
        await reloaded.LoadAsync();
        Assert.Equal(account, reloaded.Active);

        // Et la session migrée est bien celle que lit AuthSession.
        var auth = new AuthSession(store, new FixedEndpoint(null), reloaded, new Handler(_ => throw new InvalidOperationException()).Factory());
        Assert.Equal("access-1", await auth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_server_chosen_but_never_signed_in_resumes_at_the_login_screen()
    {
        var store = new MemoryStore();
        store.Data["sonnuage.server"] = ServerA.ToString();

        var accounts = new AccountStore(store, multiAccount: true);
        await accounts.LoadAsync();

        Assert.Empty(accounts.Accounts);
        Assert.False(accounts.NeedsServer);
        Assert.Equal(ServerA, accounts.LoginServer);
        Assert.False(store.Data.ContainsKey("sonnuage.server"));
    }

    [Fact]
    public async Task A_fresh_install_asks_for_a_server()
    {
        var accounts = new AccountStore(new MemoryStore(), multiAccount: true);
        await accounts.LoadAsync();
        Assert.Empty(accounts.Accounts);
        Assert.Null(accounts.Active);
        Assert.True(accounts.NeedsServer);
    }

    [Fact]
    public async Task Signing_in_again_reuses_the_account_and_the_active_one_survives_a_restart()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var alice = Tokens("alice", "a").User;
        var bob = Tokens("bob", "b").User;

        var a = await accounts.SaveSignedInAsync(ServerA, alice);
        var b = await accounts.SaveSignedInAsync(ServerB, bob);
        await accounts.SwitchAsync(b.Id);
        var again = await accounts.SaveSignedInAsync(new Uri("HTTPS://A.example.org/"), alice);

        Assert.Equal(a.Id, again.Id);
        Assert.Equal(2, accounts.Accounts.Count);
        Assert.NotEqual(a.SessionKey, b.SessionKey);
        Assert.StartsWith("sonnuage.session.", a.SessionKey);

        var reloaded = new AccountStore(store, multiAccount: true);
        await reloaded.LoadAsync();
        Assert.Equal(b.Id, reloaded.Active?.Id);
        Assert.Equal(["alice", "bob"], reloaded.Accounts.Select(x => x.Username));
    }

    [Fact]
    public async Task Adding_an_account_keeps_the_active_one_until_the_new_one_signs_in()
    {
        var accounts = new AccountStore(new MemoryStore(), multiAccount: true);
        var a = await accounts.SaveSignedInAsync(ServerA, Tokens("alice", "a").User);
        await accounts.SwitchAsync(a.Id);

        accounts.BeginAdd();
        Assert.True(accounts.NeedsServer);
        Assert.True(accounts.CanCancelAdd);
        Assert.Equal(a, accounts.Active);

        accounts.SetPendingServer(ServerB);
        Assert.False(accounts.NeedsServer);
        Assert.Equal(ServerB, accounts.LoginServer);
        Assert.Equal("sonnuage.queue." + a.Id.ToString("N"), accounts.ScopedKey("sonnuage.queue"));

        accounts.CancelAdd();
        Assert.False(accounts.IsAdding);
        Assert.Equal(ServerA, accounts.LoginServer);
    }

    [Fact]
    public async Task Removing_the_active_account_forgets_its_data_and_switches_to_the_next()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var a = await accounts.SaveSignedInAsync(ServerA, Tokens("alice", "a").User);
        var b = await accounts.SaveSignedInAsync(ServerB, Tokens("bob", "b").User);
        await accounts.SwitchAsync(a.Id);
        store.Data[a.SessionKey] = "{}";
        store.Data[accounts.ScopedKey("sonnuage.queue")] = "{}";
        store.Data[AccountStore.Scoped("sonnuage.queue", b.Id)] = "{}";
        var switched = 0;
        accounts.ActiveChanged += () => switched++;

        await accounts.RemoveAsync(a.Id);

        Assert.Equal(1, switched);
        Assert.Equal(b, accounts.Active);
        Assert.False(store.Data.ContainsKey(a.SessionKey));
        Assert.False(store.Data.ContainsKey(AccountStore.Scoped("sonnuage.queue", a.Id)));
        Assert.True(store.Data.ContainsKey(AccountStore.Scoped("sonnuage.queue", b.Id)));
    }

    [Fact]
    public async Task The_web_stays_single_account_with_its_historical_keys()
    {
        var store = new MemoryStore();
        store.Data["sonnuage.session"] = Json(Tokens("alice", "web-token"));
        store.Data["sonnuage.queue"] = "q";

        var accounts = new AccountStore(store, multiAccount: false);
        await accounts.LoadAsync();
        var auth = new AuthSession(store, new FixedEndpoint(ServerA), accounts, new Handler(_ => throw new InvalidOperationException()).Factory());

        Assert.Equal("sonnuage.queue", accounts.ScopedKey("sonnuage.queue"));
        Assert.Equal("sonnuage.session", accounts.SessionKey);
        Assert.False(accounts.NeedsServer);
        Assert.Equal("web-token", await auth.GetAccessTokenAsync());
        Assert.Equal("q", store.Data["sonnuage.queue"]);
        Assert.False(store.Data.ContainsKey(AccountStore.AccountsKey));
    }

    [Fact]
    public async Task Each_account_refreshes_its_own_tokens_against_its_own_server()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var aliceTokens = Tokens("alice", "a-old", expired: true);
        var bobTokens = Tokens("bob", "b-1");
        var a = await accounts.SaveSignedInAsync(ServerA, aliceTokens.User);
        var b = await accounts.SaveSignedInAsync(ServerB, bobTokens.User);
        store.Data[a.SessionKey] = Json(aliceTokens);
        store.Data[b.SessionKey] = Json(bobTokens);
        await accounts.SwitchAsync(a.Id);

        var requests = new List<string>();
        var handler = new Handler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Tokens("alice", "a-new") with { User = aliceTokens.User }, options: JsonDefaults.Options) };
        });
        var auth = new AuthSession(store, new FixedEndpoint(ServerA), accounts, handler.Factory());

        // Deux appels simultanés : un seul refresh (le refresh token est à usage unique).
        var tokens = await Task.WhenAll(auth.GetAccessTokenAsync(), auth.GetAccessTokenAsync());
        Assert.Equal(["a-new", "a-new"], tokens);
        Assert.Equal(["https://a.example.org/api/auth/refresh"], requests);
        Assert.Contains("a-new", store.Data[a.SessionKey]);

        await accounts.SwitchAsync(b.Id);
        Assert.Equal("b-1", await auth.GetAccessTokenAsync());
        Assert.Equal("bob", auth.User?.Username);
        Assert.Equal(Json(bobTokens), store.Data[b.SessionKey]);
        Assert.Single(requests);
    }

    [Fact]
    public async Task Logging_out_of_one_account_revokes_it_on_its_server_and_keeps_the_others()
    {
        var store = new MemoryStore();
        var accounts = new AccountStore(store, multiAccount: true);
        var a = await accounts.SaveSignedInAsync(ServerA, Tokens("alice", "a").User);
        var b = await accounts.SaveSignedInAsync(ServerB, Tokens("bob", "b").User);
        store.Data[a.SessionKey] = Json(Tokens("alice", "a"));
        store.Data[b.SessionKey] = Json(Tokens("bob", "b"));
        await accounts.SwitchAsync(a.Id);

        var requests = new List<string>();
        var auth = new AuthSession(store, new FixedEndpoint(ServerA), accounts, new Handler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }).Factory());

        await auth.LogoutAsync(b);

        Assert.Equal(["https://b.example.org/musique/api/auth/logout"], requests);
        Assert.Equal(a, Assert.Single(accounts.Accounts));
        Assert.False(store.Data.ContainsKey(b.SessionKey));
        Assert.Equal("a", await auth.GetAccessTokenAsync());
    }

    private static TokenResponse Tokens(string user, string access, bool expired = false) => new(
        access,
        DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 15),
        "refresh-" + access,
        "media-" + access,
        DateTimeOffset.UtcNow.AddDays(1),
        new UserDto(GuidFor(user), user, Roles.User, DateTimeOffset.UnixEpoch));

    private static Guid GuidFor(string name) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    private static string Json(TokenResponse tokens) => JsonSerializer.Serialize(tokens, JsonDefaults.Options);
}
