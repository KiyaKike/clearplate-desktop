using ClearPlate.Core;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class WechatLocationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED WeChat location: " + name); count++; }
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00");
        var handler = new LocationHandler { Location = "12.5,45.25" };
        var state = new AppState { Location = "1,2", ScheduleEnabled = true };
        var session = new Session("fake-location-token", "account-A", "测试");
        PlateEngine Engine(AppState settings, Session? saved = null, Action<Session?, AppState>? persist = null) =>
            new(new PlateApi(new HttpClient(handler), () => now), settings, saved ?? session,
                persist ?? ((_, _) => { }), () => now, syncWechatLocation: true);
        var engine = Engine(state);
        Check(await engine.ConnectAsync() == ConnectionResult.Connected && state.Location == "12.5,45.25"
            && state.LocationSource == "wechat", "connect replaces old manual location with same-account WeChat location");
        handler.Location = "13,46";
        await engine.CheckAsync();
        Check(state.Location == "13,46", "account check refreshes WeChat location");
        handler.AccountId = "account-B"; handler.Location = "14,47";
        await engine.ConnectAsync();
        Check(state.Location == "13,46", "different account cannot overwrite saved location");
        handler.AccountId = "account-A";
        var emptyState = new AppState();
        engine = new(new PlateApi(new HttpClient(handler), () => now), emptyState, null, (_, _) => { }, () => now, syncWechatLocation: true);
        Check(await engine.ImportAsync(session.Token, _ => true) && emptyState.Location == "14,47", "token import also saves verified WeChat location");
        var rollback = new AppState { Location = "1,2", LocationSource = "legacy" };
        engine = Engine(rollback, persist: (_, _) => throw new IOException("fake storage failure"));
        Check(!await engine.ImportAsync(session.Token, _ => true) && rollback.Location == "1,2" && rollback.LocationSource == "legacy",
            "failed import persistence restores previous location");
        foreach (var connect in new[] { true, false })
        {
            var failedState = new AppState { Location = "1,2", LocationSource = "legacy", NeedsLogin = true };
            engine = Engine(failedState, persist: (_, _) => throw new IOException("fake storage failure"));
            if (connect) await engine.ConnectAsync(); else await engine.CheckAsync();
            Check(failedState.Location == "1,2" && failedState.LocationSource == "legacy" && failedState.NeedsLogin,
                "failed account check or connection persistence restores previous location and login state");
        }
        handler.Location = "15,48";
        var submissionState = new AppState();
        engine = Engine(submissionState);
        await engine.SubmitAsync("12:00", () => (new byte[2048], "image/png", "png"));
        Check(handler.PunchLocation == "15,48" && handler.Uploads == 1 && submissionState.Confirmed.Count == 1,
            "submit refreshes location before upload even without previous manual coordinates");
        foreach (var location in new object?[] { null, "", "NaN,20", "91,20", "20,181", "20", "20,30,40", new { latitude = 20, longitude = 30 } })
        {
            handler.Location = location; handler.Uploads = 0; handler.PunchLocation = null;
            var invalidState = new AppState { Location = "1,2", LocationSource = "wechat" };
            engine = Engine(invalidState);
            await engine.SubmitAsync("12:00", () => (new byte[2048], "image/png", "png"));
            Check(handler.Uploads == 0 && handler.PunchLocation is null && invalidState.Attempted.Count == 0
                && invalidState.Location == "" && engine.Status.Contains("小程序", StringComparison.Ordinal),
                "missing or malformed WeChat coordinates block upload without using old location");
        }
        handler.Rotate = true; handler.Location = "16,49"; handler.RotatedLocation = "17,50";
        var rotatedState = new AppState();
        engine = Engine(rotatedState);
        await engine.ConnectAsync();
        Check(engine.Session?.Token == "fake-rotated" && rotatedState.Location == "17,50",
            "location is taken from the verified rotated-token profile");
        handler.RotatedLocation = null; handler.Uploads = 0;
        engine = Engine(new AppState { Location = "1,2" });
        await engine.SubmitAsync("12:00", () => (new byte[2048], "image/png", "png"));
        Check(handler.Uploads == 0 && engine.State.Location == "", "missing location on verified rotated profile blocks upload");
        handler.Rotate = false;
        handler.Location = "25,50";
        var manual = new AppState { Location = "1,2" };
        engine = new(new PlateApi(new HttpClient(handler), () => now), manual, session, (_, _) => { }, () => now);
        await engine.ConnectAsync();
        Check(manual.Location == "1,2", "manual-location clients preserve existing behavior");
        return count;
    }

    private sealed class LocationHandler : HttpMessageHandler
    {
        internal object? Location;
        internal string AccountId = "account-A";
        internal string? PunchLocation;
        internal int Uploads, Successes = 10;
        internal bool Rotate;
        internal object? RotatedLocation;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            var route = Uri.UnescapeDataString(request.RequestUri!.Query).Split('=', 2).Last();
            if (request.RequestUri.Host == "clearplateimg.guangpanxingdong.com") { Uploads++; body = new { }; }
            else if (route == "/user/user-info") body = new { code = 10007, data = new {
                user_id = AccountId, nickname = "测试", clearplate_num_success = Successes, today_clearplate_num = 3,
                token = Rotate ? "fake-rotated" : "fake-location-token", last_clearplate_clock_time = "0",
                last_location = Rotate && request.Headers.GetValues("X-Token").Single() == "fake-rotated" ? RotatedLocation : Location } };
            else if (route == "/upload/my-upload") body = new { code = 10007, data = new {
                host = "https://clearplateimg.guangpanxingdong.com", dir = "clockImg/2026/10/15/", policy = "fake",
                accessid = "fake", signature = "fake", expire = 4102444800L } };
            else if (route == "/user-clear-plate/clear-plate")
            {
                var form = await request.Content!.ReadAsStringAsync(cancellationToken);
                PunchLocation = form.Split('&').Select(x => x.Split('=', 2)).Where(x => x[0] == "location")
                    .Select(x => Uri.UnescapeDataString(x[1].Replace('+', ' '))).Single();
                Successes++; body = new { code = 10007 };
            }
            else throw new Exception("Unexpected mock route");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
