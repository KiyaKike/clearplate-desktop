using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClearPlate.Core;

public sealed class PlateApi(HttpClient http, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    public static bool ValidToken(string token) => token.Length is > 0 and <= 4096
        && !token.StartsWith("code|:|", StringComparison.Ordinal)
        && token.All(c => c is >= (char)33 and <= (char)126);
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string step)
    {
        try { return await http.SendAsync(request); }
        catch (HttpRequestException error)
        {
            // Fixed step labels and native codes only: exception text can contain
            // URLs, credentials or profile data and must not enter local records.
            var detail = error.HttpRequestError.ToString();
            var depth = 0;
            for (Exception? inner = error.InnerException; inner is not null && depth++ < 8; inner = inner.InnerException)
            {
                if (inner is System.Net.Sockets.SocketException socket) { detail += "/" + socket.SocketErrorCode; break; }
                if (inner is System.ComponentModel.Win32Exception native) { detail += "/0x" + native.NativeErrorCode.ToString("X8"); break; }
                if (inner is System.Security.Authentication.AuthenticationException) detail += "/TLS";
                else if (inner is IOException) detail += "/IO:0x" + inner.HResult.ToString("X8");
            }
            throw new HttpRequestException(error.HttpRequestError, step + "网络请求失败（" + detail + "）。", error, error.StatusCode);
        }
        catch (TaskCanceledException error) { throw new TaskCanceledException(step + "请求超时（TIMEOUT）。", error); }
    }
    private async Task<JsonElement> RequestAsync(string token, string route, Dictionary<string, string>? form = null)
    {
        if (!ValidToken(token)) throw new ArgumentException("请输入完整的本人 X-Token，微信临时 code 不能作为 token。");
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post,
            "https://clearplate-api.guangpanxingdong.com/index.php?r=" + Uri.EscapeDataString(route));
        request.Headers.Add("X-Token", token);
        request.Headers.Add("X-Version", "8.2.8");
        request.Headers.Add("X-Oauth-Type", "1");
        request.Headers.Add("X-App-Platform", "wx");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.UserAgent.ParseAdd("ClearPlateDesktop/1.0");
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        var step = route switch {
            "/user/user-info" => "查询账号", "/upload/my-upload" => "申请上传凭证", "/user-clear-plate/clear-plate" => "提交打卡", _ => "平台请求" };
        using var response = await SendAsync(request, step);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new LoginRequiredException();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new BusinessException(0, $"{step}返回 HTTP {(int)response.StatusCode}，未确认业务成功。");
        JsonElement result;
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            result = document.RootElement.Clone();
        }
        catch (JsonException) { throw new BusinessException(0, "接口未返回有效 JSON，已停止提交。"); }
        if (Number(result, "code") == 10003) throw new LoginRequiredException();
        return result;
    }
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.ValueKind is JsonValueKind.String or JsonValueKind.Number
        ? field.ToString() : "";
    internal static int Number(JsonElement value, string key) => int.TryParse(Text(value, key), out var n) ? n : 0;
    public async Task<UserProfile> UserAsync(string token)
    {
        var result = await RequestAsync(token, "/user/user-info");
        if (Number(result, "code") != 10007 || !result.TryGetProperty("data", out var data))
            throw new BusinessException(Number(result, "code"), "账号查询未成功，未更新会话。");
        var id = Text(data, "user_id");
        if (id.Length == 0 || !int.TryParse(Text(data, "clearplate_num_success"), out var successes)
            || !int.TryParse(Text(data, "today_clearplate_num"), out var remaining))
            throw new BusinessException(0, "账号数据格式不符合已验证的结构。");
        DateTimeOffset? last = null;
        if (long.TryParse(Text(data, "last_clearplate_clock_time"), out var seconds) && seconds > 0)
        {
            try { last = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { throw new BusinessException(0, "最近打卡时间异常，已停止操作。"); }
        }
        return new(id, Text(data, "nickname"), successes, remaining, Text(data, "token"), last)
        {
            WechatLocation = PlateLocation.Normalize(Text(data, "last_location"))
        };
    }
    public async Task<string> UploadAsync(string token, byte[] photo, string mime, string extension)
    {
        var prefix = "clockImg/" + Schedule.Beijing(now()).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "/";
        var result = await RequestAsync(token, "/upload/my-upload", new() { ["files"] = prefix });
        if (Number(result, "code") != 10007 || !result.TryGetProperty("data", out var data))
            throw new BusinessException(Number(result, "code"), "获取上传凭证失败，未提交打卡。");
        var host = Text(data, "host");
        if (!Uri.TryCreate(host, UriKind.Absolute, out var destination) || destination.Scheme != "https"
            || destination.Host != "clearplateimg.guangpanxingdong.com" || destination.Port != 443
            || destination.UserInfo.Length != 0 || destination.AbsolutePath != "/" || destination.Query.Length != 0
            || Text(data, "dir") != prefix || Text(data, "policy").Length == 0
            || Text(data, "accessid").Length == 0 || Text(data, "signature").Length == 0
            || !double.TryParse(Text(data, "expire"), CultureInfo.InvariantCulture, out var expiry)
            || expiry <= now().ToUnixTimeSeconds() + 2)
            throw new BusinessException(0, "上传凭证格式或目标地址异常，未上传照片。");
        var key = prefix + Guid.NewGuid().ToString("N") + "." + extension;
        using var body = new MultipartFormDataContent();
        // The upload gateway rejects .NET's default multipart headers. Match
        // browser forms: bare boundary, quoted names, no text-part Content-Type,
        // and Content-Disposition before the file Content-Type (file stays last).
        var boundary = body.Headers.ContentType!.Parameters.Single(p => p.Name == "boundary");
        boundary.Value = boundary.Value!.Trim('"');
        foreach (var pair in new Dictionary<string, string> {
            ["key"] = key, ["policy"] = Text(data, "policy"), ["OSSAccessKeyId"] = Text(data, "accessid"),
            ["success_action_status"] = "200", ["signature"] = Text(data, "signature"), ["Content-Type"] = mime })
        {
            var field = new StringContent(pair.Value);
            field.Headers.ContentType = null;
            field.Headers.ContentDisposition = new("form-data") { Name = $"\"{pair.Key}\"" };
            body.Add(field);
        }
        var file = new ByteArrayContent(photo);
        file.Headers.ContentDisposition = new("form-data") { Name = "\"file\"", FileName = $"\"{Path.GetFileName(key)}\"" };
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mime);
        body.Add(file);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination) { Content = body };
        // OSS gets its signed form only; no login token is attached.
        using var response = await SendAsync(request, "发送照片");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new BusinessException(0, $"照片上传返回 HTTP {(int)response.StatusCode}，未提交打卡。");
        return "/" + key;
    }
    public async Task PunchAsync(string token, string groupId, string location, string path)
    {
        var result = await RequestAsync(token, "/user-clear-plate/clear-plate",
            new() { ["group_id"] = groupId, ["location"] = location, ["path_url"] = path });
        var code = Number(result, "code");
        if (code == 12012) throw new BusinessException(code, "图片校验失败（12012），请使用当天真实光盘照片。");
        if (code == 12011) throw new BusinessException(code, "打卡间隔不足（12011）：两次打卡至少间隔 2 小时。");
        if (code == 12010) throw new BusinessException(code, "今天的打卡次数已用完（12010）。");
        if (code != 10007) throw new BusinessException(code, $"提交未成功，接口返回 {code}：{Text(result, "msg")}");
    }
}
