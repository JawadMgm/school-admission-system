using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace SchoolAdmission.Tests;

/// <summary>
/// Starts a throwaway PostgreSQL container (needs Docker), or uses the database in
/// TEST_DB_CONNECTION when that env var is set. Each test creates its own class, so tests do not collide.
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    public string ConnectionString = "";
    public WebApplicationFactory<Program> Factory = null!;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");
        if (external is null)
        {
            _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _pg.StartAsync();
        }
        ConnectionString = (external ?? _pg!.GetConnectionString()) + ";Maximum Pool Size=80";
        Factory = Build(null);
    }

    public WebApplicationFactory<Program> Build(int? holdSeconds) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", ConnectionString);
            if (holdSeconds is not null) b.UseSetting("HOLD_DURATION_SECONDS", holdSeconds.ToString());
        });

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }
}

public class AdmissionTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static async Task<string> CreateClass(HttpClient c, int seats)
    {
        var r = await c.PostAsJsonAsync("/classes", new { name = "Class 6 - Morning Shift", totalSeats = seats });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, string student)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("X-Student-Id", student);
        return c.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Concurrent_holds_never_exceed_total_seats()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Send(c, HttpMethod.Post, $"/classes/{classId}/holds", $"student-{i}")));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(40, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var cls = await Json(await c.GetAsync($"/classes/{classId}"));
        Assert.Equal(10, cls.GetProperty("heldSeats").GetInt32());
        Assert.Equal(0, cls.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Held_plus_confirmed_count_against_total()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 2);

        var h1 = await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "a"));
        await Send(c, HttpMethod.Post, $"/holds/{h1.GetProperty("holdId").GetString()}/confirm", "a");
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "b")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "c")).StatusCode);
    }

    [Fact]
    public async Task Same_student_cannot_hold_same_class_twice_even_concurrently()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "same-student")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Confirm_is_idempotent_and_counts_once_under_concurrency()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        var holdId = (await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "s1"))).GetProperty("holdId").GetString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", "s1")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var cls = await Json(await c.GetAsync($"/classes/{classId}"));
        Assert.Equal(1, cls.GetProperty("confirmedSeats").GetInt32());
        Assert.Equal(0, cls.GetProperty("heldSeats").GetInt32());
        Assert.Equal(4, cls.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Only_the_owner_can_confirm_or_cancel()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        var holdId = (await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "owner"))).GetProperty("holdId").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", "intruder")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", "intruder")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/holds/{holdId}/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task Cancel_releases_the_seat()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 1);
        var holdId = (await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "s1"))).GetProperty("holdId").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", "s1")).StatusCode);
        Assert.Equal("CANCELLED", (await Json(await c.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "s2")).StatusCode);
    }

    [Fact]
    public async Task Expired_hold_frees_the_seat_without_any_api_call_and_cannot_be_confirmed()
    {
        var c = fx.Build(holdSeconds: 1).CreateClient();
        var classId = await CreateClass(c, 1);
        var holdId = (await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "s1"))).GetProperty("holdId").GetString();

        await Task.Delay(2500);

        Assert.Equal("EXPIRED", (await Json(await c.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString());
        Assert.Equal(1, (await Json(await c.GetAsync($"/classes/{classId}"))).GetProperty("availableSeats").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", "s1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "s2")).StatusCode);
    }

    [Fact]
    public async Task My_holds_lists_only_my_applications()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "me-1");
        await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "other");

        var mine = (await Json(await Send(c, HttpMethod.Get, "/me/holds", "me-1"))).GetProperty("holds");
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal("HELD", mine[0].GetProperty("status").GetString());
        Assert.Equal("Class 6 - Morning Shift", mine[0].GetProperty("className").GetString());
    }

    [Fact]
    public async Task Invalid_input_returns_400_and_unknown_class_404()
    {
        var c = fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "", totalSeats = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "X", totalSeats = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Post, "/classes/nope/holds", "s1")).StatusCode);
    }
}
