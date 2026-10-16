using ClearPlate.Core;
using System.Net;
using System.Text;
using System.Text.Json;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
foreach (var pair in new (string Date, string? Slot)[] {
    ("2026-10-14T07:59:59+08:00", null), ("2026-10-14T08:00:00+08:00", "08:00"),
    ("2026-10-14T08:04:59+08:00", "08:00"), ("2026-10-14T08:05:00+08:00", null),
    ("2026-10-14T11:59:59+08:00", null), ("2026-10-14T12:00:00+08:00", "12:00"),
    ("2026-10-14T12:04:59+08:00", "12:00"), ("2026-10-14T12:05:00+08:00", null),
    ("2026-10-14T17:59:59+08:00", null), ("2026-10-14T18:00:00+08:00", "18:00"),
    ("2026-10-14T18:04:59+08:00", "18:00"), ("2026-10-14T18:05:00+08:00", null),
    ("2026-10-14T00:00:00Z", "08:00") }) Check(Schedule.Due(DateTimeOffset.Parse(pair.Date)) == pair.Slot, "time boundary " + pair.Date);
var now = DateTimeOffset.Parse("2026-10-14T18:00:00+08:00");
Check(Schedule.Key("08:00", now) != Schedule.Key("12:00", now), "slots independent");
Check(Schedule.Key("18:00", now) != Schedule.Key("18:00", now.AddDays(1)), "days independent");
var photo = new byte[2048];
(byte[], string, string) Photo() => (photo, "image/png", "png");
var origin = new Session("fake-original", "account-A", "测试");
AppState Fresh() => new() { Location = "0,0", GroupId = "", ScheduleEnabled = true };
PlateEngine Engine(FakeHandler handler, AppState? state = null, Session? session = null, Action<Session?, AppState>? persist = null) =>
    new(new PlateApi(new HttpClient(handler), () => now), state ?? Fresh(), session ?? origin, persist ?? ((_, _) => { }), () => now);
var handler = new FakeHandler(() => now);
var engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(engine.State.Confirmed.Contains(Schedule.Key("18:00", now)), "successful result independently confirmed");
Check(handler.Punches == 1 && handler.Uploads == 1, "one upload and one submit");
var calls = handler.Calls;
await engine.SubmitAsync("18:00", Photo);
Check(handler.Calls == calls, "duplicate makes no requests");
Check(!handler.OssReceivedToken, "OSS never receives login token");

handler = new(() => now) { Mode = "offline" }; engine = Engine(handler);
await engine.TickAsync(Photo);
Check(!engine.State.Attempted.Contains(Schedule.Key("18:00", now)), "preflight offline does not consume slot");
Check(engine.State.Records[Schedule.Key("18:00", now)].Outcome == "等待网络", "offline phase saved");
Check(handler.Uploads == 0 && handler.Punches == 0 && handler.Calls == 1, "offline never uploads or submits");
handler.Mode = "ok";
var persisted = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(engine.State))!;
engine = Engine(handler, persisted);
await engine.TickAsync(Photo);
Check(handler.Punches == 1 && engine.State.Confirmed.Count == 1, "recovery after restart submits exactly once");

handler = new(() => now) { Mode = "offline" }; engine = Engine(handler);
now = now.AddMinutes(5);
await engine.TickAsync(Photo);
Check(handler.Calls == 0, "missed window never catches up");
now = now.AddMinutes(-5);

handler = new(() => now) { Mode = "upload-error" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo); calls = handler.Calls;
await engine.TickAsync(Photo);
Check(engine.State.Attempted.Count == 1 && handler.Calls == calls && handler.Punches == 0, "upload failure cannot be repeated");
Check(engine.State.Records[Schedule.Key("18:00", now)].Stage == "上传照片", "upload failure stage");

handler = new(() => now) { Mode = "timeout-submit" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo); calls = handler.Calls;
await engine.TickAsync(Photo);
Check(engine.State.Records[Schedule.Key("18:00", now)].Outcome == "结果未确认" && handler.Calls == calls, "submit timeout preserves uncertainty and guard");

handler = new(() => now) { Mode = "image-rejected" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(engine.State.Records[Schedule.Key("18:00", now)].Message.Contains("12012"), "image rejection code saved");
Check(engine.State.Confirmed.Count == 0 && engine.State.Attempted.Count == 1, "rejection cannot become success");

handler = new(() => now) { Mode = "no-increment" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(engine.State.Confirmed.Count == 0 && engine.State.Records[Schedule.Key("18:00", now)].Outcome == "结果未确认", "10007 alone cannot confirm success");

handler = new(() => now) { LastCheck = now.AddHours(-1) }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(handler.Uploads == 0 && handler.Punches == 0 && engine.State.Attempted.Count == 0, "two hour preflight prevents upload");
handler.LastCheck = now.AddHours(-2);
await engine.TickAsync(Photo);
Check(engine.State.Confirmed.Count == 1, "exactly two hours allowed");

handler = new(() => now) { Remaining = 0 }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(handler.Uploads == 0 && handler.Punches == 0 && engine.State.Attempted.Count == 0, "daily quota checked before upload without consuming slot");

handler = new(() => now) { Mode = "expired" }; engine = Engine(handler);
await engine.TickAsync(Photo); calls = handler.Calls; await engine.TickAsync(Photo);
Check(engine.State.NeedsLogin && engine.State.Attempted.Count == 0 && handler.Calls == calls, "expired session pauses and preserves pre-upload slot");
handler.Mode = "ok";
Check(await engine.ImportAsync("fake-new", _ => true) && !engine.State.NeedsLogin, "valid same-account import restores automation");

handler = new(() => now) { Mode = "other-account" }; engine = Engine(handler);
Check(!await engine.ImportAsync("fake-other", _ => true) && engine.Session!.Token == origin.Token, "other account import rejected");
handler.Mode = "ok";
foreach (var bad in new[] { "", "code|:|temporary", "fake\r\nheader" }) {
    calls = handler.Calls; Check(!await engine.ImportAsync(bad, _ => true) && handler.Calls == calls, "invalid token never sent");
}

handler = new(() => now) { Mode = "rotation" };
Session? savedSession = null;
engine = Engine(handler, persist: (session, _) => savedSession = session);
await engine.CheckAsync();
Check(engine.Session!.Token == "fake-rotated" && savedSession?.Token == "fake-rotated" && handler.Calls == 2, "rotated token checked before saved");
handler = new(() => now) { Mode = "wrong-rotation" }; engine = Engine(handler);
await engine.CheckAsync();
Check(engine.Session!.Token == origin.Token && !engine.State.NeedsLogin, "bad rotation preserves valid original");
handler = new(() => now); engine = Engine(handler, persist: (_, _) => throw new IOException("fake disk failure"));
Check(!await engine.ImportAsync("fake-new", _ => true) && engine.Session!.Token == origin.Token, "failed credential save preserves original session");

handler = new(() => now) { Mode = "tls-error" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo);
Check(engine.State.Attempted.Count == 0 && engine.State.Records[Schedule.Key("18:00", now)].RetryPolicy == "manual", "pre-upload TLS error permits only explicit manual retry");
calls = handler.Calls; await engine.TickAsync(Photo);
Check(handler.Calls == calls, "TLS failure is never automatically retried as offline");

handler = new(() => now) { Mode = "401" }; engine = Engine(handler);
await engine.CheckAsync(); Check(engine.State.NeedsLogin, "HTTP 401 requires login");
handler = new(() => now) { Mode = "500" }; engine = Engine(handler);
await engine.CheckAsync(); Check(!engine.State.NeedsLogin, "HTTP 500 is not called expired token");
handler = new(() => now); engine = Engine(handler, new() { Location = "NaN,0" });
await engine.SubmitAsync("18:00", Photo);
Check(handler.Calls == 0 && engine.State.Attempted.Count == 0, "invalid location never consumes slot or sends request");

Check(!new AppState().ScheduleEnabled, "fresh installation requires explicitly enabling schedule");
handler = new(() => now); engine = Engine(handler);
await engine.SubmitAsync("18:00", () => throw new IOException("fake photo read failure"));
var retryKey = Schedule.Key("18:00", now);
Check(handler.Calls == 0 && engine.State.Attempted.Count == 0 && engine.State.Records[retryKey].RetryPolicy == "manual", "photo failure preserves manual retry without requests");
Check(engine.SlotText("18:00").Contains("手动重试"), "record explains safe manual retry");
await engine.TickAsync(Photo);
Check(handler.Calls == 0, "manual-only preparation failure never automatically backfills");
persisted = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(engine.State))!;
engine = Engine(handler, persisted); await engine.TickAsync(Photo);
Check(handler.Calls == 0, "manual retry policy survives serialization and restart");
await engine.SubmitAsync("18:00", Photo);
Check(handler.Punches == 1 && engine.State.Confirmed.Contains(retryKey), "explicit retry after photo failure submits exactly once");

handler = new(() => now) { Mode = "500" }; engine = Engine(handler);
await engine.SubmitAsync("18:00", Photo); calls = handler.Calls;
await engine.TickAsync(Photo);
Check(engine.State.Attempted.Count == 0 && handler.Calls == calls && engine.State.Records[retryKey].RetryPolicy == "manual", "pre-upload server error does not consume slot or start automatic loop");
handler.Mode = "ok"; await engine.SubmitAsync("18:00", Photo);
Check(handler.Punches == 1, "server recovery permits explicit manual retry");

var legacyRecord = JsonSerializer.Deserialize<SlotRecord>("""
    {"StartedAt":"original-start","UpdatedAt":"original-time","Stage":"准备照片","Outcome":"未成功","Message":"fake legacy failure"}
    """)!;
Check(legacyRecord.UploadStarted is null && legacyRecord.RetryPolicy == "", "old schema loads without new retry fields");
Check(PreUploadRecovery.CanRelease(legacyRecord), "terminal legacy preparation failure is proven unsubmitted");
Check(PreUploadRecovery.CanRelease(legacyRecord with { Stage = "查询账号" }), "terminal account failure precedes every upload");
foreach (var unsafeRecord in new[] { legacyRecord with { Outcome = "执行中" }, legacyRecord with { Stage = "上传照片" },
    legacyRecord with { Stage = "提交打卡", Outcome = "结果未确认" }, legacyRecord with { UploadStarted = true }, legacyRecord with { SubmissionStarted = true } })
    Check(!PreUploadRecovery.CanRelease(unsafeRecord), "in-flight/uploaded/uncertain records remain protected");
var confirmedKey = Schedule.Key("08:00", now); var unknownKey = Schedule.Key("12:00", now);
var uploadedKey = Schedule.Key("18:00", now.AddDays(-1));
var legacyState = Fresh(); legacyState.Attempted.UnionWith([retryKey, confirmedKey, unknownKey, uploadedKey]);
legacyState.Confirmed.Add(confirmedKey);
legacyState.Records[retryKey] = legacyRecord; legacyState.Records[confirmedKey] = legacyRecord;
legacyState.Records[uploadedKey] = legacyRecord with { Stage = "上传照片" };
AppState? savedRecovery = null; var recoveryWrites = 0;
handler = new(() => now);
engine = Engine(handler, legacyState, persist: (_, state) => { savedRecovery = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state)); recoveryWrites++; });
Check(!engine.State.Attempted.Contains(retryKey) && savedRecovery?.Records[retryKey].RetryPolicy == "manual", "startup persistently releases proven legacy pre-upload failure");
Check(engine.State.Confirmed.Contains(confirmedKey) && engine.State.Attempted.IsSupersetOf([confirmedKey, unknownKey, uploadedKey]), "confirmed, unknown and uploaded guards survive migration");
Check(engine.State.Records[retryKey].Message == legacyRecord.Message && engine.State.Records[retryKey].UpdatedAt == legacyRecord.UpdatedAt, "migration preserves original failure and timestamp");
engine.RecoverUnsubmittedAttempts();
Check(recoveryWrites == 1 && handler.Calls == 0, "migration is idempotent and sends no requests");
await engine.TickAsync(Photo);
Check(handler.Calls == 0 && engine.SlotText("18:00").Contains("手动重试"), "legacy recovery never automatically submits missed slot");
var diskRecovery = Fresh(); diskRecovery.Attempted.Add(retryKey); diskRecovery.Records[retryKey] = legacyRecord;
try { _ = Engine(handler, diskRecovery, persist: (_, _) => throw new IOException("fake disk failure")); throw new Exception("startup continued without persisted recovery"); }
catch (IOException) { checks++; }

handler = new(() => now); engine = Engine(handler, persist: (_, _) => throw new IOException("fake disk failure"));
try { await engine.SubmitAsync("18:00", Photo); throw new Exception("submission continued without persisted record"); }
catch (IOException) { checks++; }
Check(handler.Calls == 0 && !engine.Busy, "failed local record prevents all requests and releases busy state");
handler = new(() => now); AppState? beforeBoundary = null;
engine = Engine(handler, persist: (_, state) => {
    if (state.Records.GetValueOrDefault(retryKey)?.Stage == "上传照片") throw new IOException("fake boundary write failure");
    beforeBoundary = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state));
});
try { await engine.SubmitAsync("18:00", Photo); throw new Exception("upload began without durable guard"); }
catch (IOException) { checks++; }
Check(handler.Calls == 1 && handler.Uploads == 0 && handler.Punches == 0, "network upload boundary is persisted before upload API request");
engine = Engine(handler, beforeBoundary);
Check(engine.State.Attempted.Contains(retryKey), "interrupted in-flight record remains protected on restart");

handler = new(() => now); engine = Engine(handler);
Check(await engine.ConnectAsync() == ConnectionResult.Connected && handler.Calls == 1, "valid saved login connects without capture");
Check(await engine.ConnectAsync() == ConnectionResult.Connected && handler.Calls == 2 && handler.Uploads == 0 && handler.Punches == 0, "repeated connect is read-only");
foreach (var mode in new[] { "expired", "401" }) {
    handler = new(() => now) { Mode = mode }; engine = Engine(handler);
    Check(await engine.ConnectAsync() == ConnectionResult.NeedsCapture && engine.State.NeedsLogin, "only explicit login expiry requests capture");
}
foreach (var mode in new[] { "offline", "500", "tls-error", "other-account" }) {
    handler = new(() => now) { Mode = mode }; engine = Engine(handler);
    Check(await engine.ConnectAsync() == ConnectionResult.Failed && !engine.State.NeedsLogin, "network/server/wrong-account errors never request capture");
}
handler = new(() => now);
engine = new(new PlateApi(new HttpClient(handler)), Fresh(), null, (_, _) => { }, () => now);
Check(await engine.ConnectAsync() == ConnectionResult.NeedsCapture && handler.Calls == 0, "missing login requests capture without sending blank credentials");

checks += await WechatLocationChecks.RunAsync();
checks += await UploadFailureChecks.RunAsync();
checks += await UploadFormChecks.RunAsync();
var captureChecks = await CaptureChecks.RunAsync();
Console.WriteLine($"PASS: {checks} business checks + {captureChecks} capture protocol / isolated local TLS checks; no live requests, no system trust or proxy changes.");

sealed class FakeHandler(Func<DateTimeOffset> now) : HttpMessageHandler
{
    public string Mode = "ok";
    public int Calls, Uploads, Punches, Successes = 110, Remaining = 3;
    public DateTimeOffset? LastCheck;
    public bool OssReceivedToken;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var route = Uri.UnescapeDataString(request.RequestUri!.Query).Split('=', 2).Last();
        object body;
        var http = HttpStatusCode.OK;
        if (request.RequestUri.Host == "clearplateimg.guangpanxingdong.com") {
            Uploads++; OssReceivedToken |= request.Headers.Contains("X-Token"); body = new { };
        }
        else if (route == "/user/user-info") {
            if (Mode == "offline") throw new HttpRequestException(HttpRequestError.ConnectionError, "fake offline");
            if (Mode == "tls-error") throw new HttpRequestException(HttpRequestError.SecureConnectionError, "fake TLS error");
            if (Mode == "401") http = HttpStatusCode.Unauthorized;
            if (Mode == "500") http = HttpStatusCode.InternalServerError;
            var token = request.Headers.GetValues("X-Token").Single();
            var id = Mode == "other-account" || Mode == "wrong-rotation" && token == "fake-rotated" ? "account-B" : "account-A";
            body = Mode == "expired" ? new { code = 10003 } : (object)new { code = 10007, data = new {
                user_id = id, nickname = "测试", clearplate_num_success = Successes.ToString(), today_clearplate_num = Remaining.ToString(),
                token = Mode is "rotation" or "wrong-rotation" ? "fake-rotated" : token,
                last_clearplate_clock_time = LastCheck?.ToUnixTimeSeconds().ToString() ?? "0" } };
        }
        else if (route == "/upload/my-upload") {
            if (Mode == "upload-error") throw new HttpRequestException(HttpRequestError.ConnectionError, "fake upload error");
            body = new { code = 10007, data = new { host = "https://clearplateimg.guangpanxingdong.com", dir = "clockImg/" + Schedule.Beijing(now()).ToString("yyyy/MM/dd") + "/",
                policy = "fake", accessid = "fake", signature = "fake", expire = now().ToUnixTimeSeconds() + 60 } };
        }
        else if (route == "/user-clear-plate/clear-plate") {
            Punches++;
            if (Mode == "timeout-submit") throw new TaskCanceledException("fake submit timeout");
            if (Mode != "image-rejected" && Mode != "no-increment") Successes++;
            body = new { code = Mode == "image-rejected" ? 12012 : 10007, msg = "fake response" };
        }
        else throw new Exception("Unexpected request: " + request.RequestUri.Host + route);
        return Task.FromResult(new HttpResponseMessage(http) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
    }
}
