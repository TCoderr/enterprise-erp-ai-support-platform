using System.Net;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Xunit;

namespace Kantar.IntegrationTests.Identity;

public sealed class AuthAndCsrfTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    [Fact]
    public async Task Unauthenticated_api_call_is_a_json_401_not_a_redirect()
    {
        var client = fixture.NewClient();
        using var response = await client.GetAsync("/api/products");

        await response.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Null(response.Headers.Location);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_without_the_pre_login_csrf_token_is_rejected()
    {
        var user = await _world.UserAsync([Roles.SalesAgent]);
        var client = fixture.NewClient();

        using var response = await client.PostAsync("/api/auth/login", new { userName = user.UserName, password = World.Password });

        await response.ProblemAsync(HttpStatusCode.Forbidden, "csrf_failed");
        Assert.DoesNotContain("__Host-kantar-session", client.Cookies.Keys);
    }

    [Fact]
    public async Task Csrf_bootstrap_goes_anonymous_then_login_then_authenticated()
    {
        var user = await _world.UserAsync([Roles.SystemAdmin]);
        var client = fixture.NewClient();

        using var anonymous = await client.GetAsync("/api/auth/csrf");
        var anonymousToken = (await anonymous.JsonAsync(HttpStatusCode.OK)).Str("token");
        Assert.Contains("__Host-kantar-pre", client.Cookies.Keys);

        using var login = await client.LoginAsync(user.UserName, World.Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain("__Host-kantar-pre", client.Cookies.Keys);
        var sessionToken = client.CsrfToken!;
        Assert.NotEqual(anonymousToken, sessionToken);

        // The token handed out before login must not authorise a signed-in request.
        client.CsrfToken = anonymousToken;
        using var withOldToken = await client.PostAsync("/api/auth/logout");
        await withOldToken.ProblemAsync(HttpStatusCode.Forbidden, "csrf_failed");

        using var withoutToken = await client.SendAsync(HttpMethod.Post, "/api/auth/logout", sendCsrf: false);
        await withoutToken.ProblemAsync(HttpStatusCode.Forbidden, "csrf_failed");

        client.CsrfToken = sessionToken;
        using var csrfAgain = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(sessionToken, (await csrfAgain.JsonAsync(HttpStatusCode.OK)).Str("token"));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var user = await _world.UserAsync([Roles.SalesAgent]);

        using var wrongPassword = await fixture.NewClient().LoginAsync(user.UserName, "Yanlis!Parola123");
        using var unknownUser = await fixture.NewClient().LoginAsync("yok-" + Guid.NewGuid().ToString("N")[..8], World.Password);

        var first = await wrongPassword.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        var second = await unknownUser.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Equal(first.Str("detail"), second.Str("detail"));
    }

    [Fact]
    public async Task Logout_revokes_a_previously_captured_cookie()
    {
        var user = await _world.UserAsync([Roles.SystemAdmin]);
        var client = await _world.LoginAsync(user);
        var captured = client.CloneWith(fixture.Factory.CreateDefaultClient(new Uri("https://localhost")));

        using var logout = await client.PostAsync("/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var replay = await captured.GetAsync("/api/auth/me");
        await replay.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Removing_a_role_ends_existing_sessions_and_the_permission_is_gone_after_new_login()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var user = await _world.UserAsync([Roles.SalesAgent, Roles.SupportAgent]);
        var session = await _world.LoginAsync(user);
        using var before = await session.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var current = await admin.GetAsync($"/api/admin/users/{user.Id}");
        using var changed = await admin.PutAsync($"/api/admin/users/{user.Id}/roles", new { roles = new[] { Roles.SupportAgent } }, current.ETag());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using var withOldCookie = await session.GetAsync("/api/customers");
        await withOldCookie.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");

        var fresh = await _world.LoginAsync(user);
        using var afterNewLogin = await fresh.GetAsync("/api/customers");
        await afterNewLogin.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Password_change_signs_out_other_sessions_and_keeps_the_current_one()
    {
        var user = await _world.UserAsync([Roles.SupportAgent]);
        var current = await _world.LoginAsync(user);
        var other = await _world.LoginAsync(user);
        const string newPassword = "Yeni!Parola456bB";

        using var change = await current.PostAsync("/api/auth/change-password", new { currentPassword = World.Password, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var currentStillWorks = await current.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, currentStillWorks.StatusCode);
        using var otherIsOut = await other.GetAsync("/api/auth/me");
        await otherIsOut.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");

        using var oldPassword = await fixture.NewClient().LoginAsync(user.UserName, World.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var withNewPassword = await fixture.NewClient().LoginAsync(user.UserName, newPassword);
        Assert.Equal(HttpStatusCode.OK, withNewPassword.StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_revokes_sessions_and_forces_a_change_before_anything_else()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var user = await _world.UserAsync([Roles.SupportAgent]);
        var session = await _world.LoginAsync(user);

        using var reset = await admin.PostAsync($"/api/admin/users/{user.Id}/reset-password");
        var temporary = (await reset.JsonAsync(HttpStatusCode.OK)).Str("temporaryPassword");

        using var oldSession = await session.GetAsync("/api/auth/me");
        await oldSession.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");

        var fresh = fixture.NewClient();
        using var login = await fresh.LoginAsync(user.UserName, temporary);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var blocked = await fresh.GetAsync("/api/support/tickets");
        await blocked.ProblemAsync(HttpStatusCode.Forbidden, "password_change_required");
        using var me = await fresh.GetAsync("/api/auth/me");
        Assert.True((await me.JsonAsync(HttpStatusCode.OK))["mustChangePassword"]!.GetValue<bool>());
    }

    [Fact]
    public async Task There_is_no_public_sign_up_and_non_admins_cannot_create_users()
    {
        var anonymous = fixture.NewClient();
        using (var csrf = await anonymous.GetAsync("/api/auth/csrf"))
        {
            anonymous.CsrfToken = (await csrf.JsonAsync(HttpStatusCode.OK)).Str("token");
        }

        var body = new { userName = "yeni.kullanici", displayName = "Yeni", roles = new[] { Roles.SystemAdmin }, poApprovalLimit = 0 };
        using var signUp = await anonymous.PostAsync("/api/admin/users", body);
        Assert.Contains(signUp.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        var manager = await _world.LoginAsAsync([Roles.SalesManager]);
        using var byManager = await manager.PostAsync("/api/admin/users", body);
        await byManager.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Session_expires_after_the_idle_timeout()
    {
        var client = await _world.LoginAsAsync([Roles.SupportAgent]);

        // The shared fake clock only moves forward. Every test signs in on its own, so moving it
        // ahead here cannot expire a session another test still relies on.
        fixture.Clock.Advance(TimeSpan.FromMinutes(29));
        using var stillValid = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);

        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        using var response = await client.GetAsync("/api/auth/me");
        await response.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Session_ends_at_the_absolute_lifetime_even_when_it_is_used_continuously()
    {
        var client = await _world.LoginAsAsync([Roles.SupportAgent]);

        for (var i = 0; i < 19; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(25));
            using var active = await client.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        using var expired = await client.GetAsync("/api/auth/me");
        await expired.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }
}
