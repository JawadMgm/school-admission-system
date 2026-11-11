using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    // 17-digit birth registration numbers, unique per test run.
    private static long _next = 20150000000000000 + Random.Shared.Next(1_000_000) * 1000L;
    private static string NewStudent() => Interlocked.Increment(ref _next).ToString();

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

    private static async Task<string> Hold(HttpClient c, string classId, string student) =>
        (await Json(await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", student))).GetProperty("holdId").GetString()!;

    [Fact]
    public async Task Concurrent_holds_never_exceed_total_seats()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 10);

        var responses = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Send(c, HttpMethod.Post, $"/classes/{classId}/holds", NewStudent())));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(40, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var cls = await Json(await c.GetAsync($"/classes/{classId}"));
        Assert.Equal(10, cls.GetProperty("heldSeats").GetInt32());
        Assert.Equal(0, cls.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Hold_response_matches_contract()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        var student = NewStudent();

        var r = await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", student);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var h = await Json(r);
        Assert.Equal(classId, h.GetProperty("classId").GetString());
        Assert.Equal(student, h.GetProperty("studentId").GetString());
        Assert.Equal("HELD", h.GetProperty("status").GetString());
        Assert.EndsWith("Z", h.GetProperty("expiresAt").GetString());
        Assert.False(string.IsNullOrEmpty(h.GetProperty("holdId").GetString()));
    }

    [Fact]
    public async Task Held_plus_confirmed_count_against_total()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 2);
        var a = NewStudent();

        var holdId = await Hold(c, classId, a);
        await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", a);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", NewStudent())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", NewStudent())).StatusCode);
    }

    [Fact]
    public async Task Same_student_cannot_hold_same_class_twice_even_concurrently()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 10);
        var student = NewStudent();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Send(c, HttpMethod.Post, $"/classes/{classId}/holds", student)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
    }

    [Fact]
    public async Task Confirm_is_idempotent_and_counts_once_under_concurrency()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        var student = NewStudent();
        var holdId = await Hold(c, classId, student);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", student)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal("CONFIRMED", (await Json(responses[0])).GetProperty("status").GetString());
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
        var holdId = await Hold(c, classId, NewStudent());
        var intruder = NewStudent();

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", intruder)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", intruder)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/holds/{holdId}/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task Cancel_releases_the_seat()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 1);
        var student = NewStudent();
        var holdId = await Hold(c, classId, student);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", student)).StatusCode);
        Assert.Equal("CANCELLED", (await Json(await c.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", NewStudent())).StatusCode);
    }

    [Fact]
    public async Task Expired_hold_frees_the_seat_without_any_api_call_and_cannot_be_confirmed()
    {
        var c = fx.Build(holdSeconds: 1).CreateClient();
        var classId = await CreateClass(c, 1);
        var student = NewStudent();
        var holdId = await Hold(c, classId, student);

        await Task.Delay(2500);

        Assert.Equal("EXPIRED", (await Json(await c.GetAsync($"/holds/{holdId}"))).GetProperty("status").GetString());
        Assert.Equal(1, (await Json(await c.GetAsync($"/classes/{classId}"))).GetProperty("availableSeats").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", student)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", NewStudent())).StatusCode);
    }

    [Fact]
    public async Task My_holds_is_a_plain_array_of_only_my_applications()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);
        var me = NewStudent();
        await Hold(c, classId, me);
        await Hold(c, classId, NewStudent());

        var mine = await Json(await Send(c, HttpMethod.Get, "/me/holds", me));
        Assert.Equal(JsonValueKind.Array, mine.ValueKind);
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal("HELD", mine[0].GetProperty("status").GetString());
        Assert.Equal(classId, mine[0].GetProperty("classId").GetString());
        Assert.Equal("Class 6 - Morning Shift", mine[0].GetProperty("className").GetString());
    }

    [Fact]
    public async Task Bad_input_returns_4xx_never_500()
    {
        var c = fx.Factory.CreateClient();
        var classId = await CreateClass(c, 5);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "", totalSeats = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "X", totalSeats = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/classes", new StringContent("{not json", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/classes", new StringContent("{\"name\":\"X\",\"totalSeats\":\"abc\"}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, HttpMethod.Post, $"/classes/{classId}/holds", "12345")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, HttpMethod.Get, "/me/holds", "abcdefghijklmnopq")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Post, "/classes/nope/holds", NewStudent())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/classes/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/holds/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, HttpMethod.Post, "/holds/nope/confirm", NewStudent())).StatusCode);
    }
}
