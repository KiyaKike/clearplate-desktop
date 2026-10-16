using System.Globalization;

namespace ClearPlate.Core;

public sealed class PlateEngine
{
    private readonly PlateApi api;
    private readonly Action<Session?, AppState> save;
    private readonly Func<DateTimeOffset> now;
    private readonly bool syncWechatLocation;
    public AppState State { get; }
    public Session? Session { get; private set; }
    public PlateEngine(PlateApi api, AppState state, Session? session, Action<Session?, AppState> save, Func<DateTimeOffset>? clock = null,
        bool syncWechatLocation = false)
    {
        this.api = api; this.save = save; now = clock ?? (() => DateTimeOffset.UtcNow);
        this.syncWechatLocation = syncWechatLocation;
        if (syncWechatLocation) Status = "首次使用点击“连接微信”获取本人登录，并同步微信小程序最近位置。";
        State = state; Session = session;
        State.Attempted.UnionWith(State.Confirmed);
        RecoverUnsubmittedAttempts();
    }
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "首次使用点击“连接微信”获取本人登录，并填写组织及真实位置。";
    public event Action? Changed;
    public void Persist() { save(Session, State); Changed?.Invoke(); }
    private void Message(string value) { Status = value; Changed?.Invoke(); }
    public void RecoverUnsubmittedAttempts()
    {
        var released = State.Attempted.Where(key => !State.Confirmed.Contains(key)
            && State.Records.TryGetValue(key, out var record) && PreUploadRecovery.CanRelease(record)).ToArray();
        if (released.Length == 0) return;
        foreach (var key in released)
        {
            State.Attempted.Remove(key);
            State.Records[key] = State.Records[key] with { RetryPolicy = "manual", UploadStarted = false, SubmissionStarted = false };
        }
        // Keep the original error/time. Persistence failure aborts startup, so no
        // requests can run using a recovery that only exists in memory.
        Persist();
    }
    private void Record(string key, string stage, string outcome, string message,
        bool? uploadStarted = null, bool? submissionStarted = null, string? retryPolicy = null)
    {
        if (Session is { Token.Length: > 0 }) message = message.Replace(Session.Token, "[已隐藏]", StringComparison.Ordinal);
        if (State.Location.Length > 0) message = message.Replace(State.Location, "[已隐藏]", StringComparison.Ordinal);
        var stamp = Schedule.Timestamp(now());
        var previous = State.Records.GetValueOrDefault(key);
        State.Records[key] = new(previous?.StartedAt ?? stamp, stamp, stage, outcome,
            message.Length > 600 ? message[..600] : message)
        {
            UploadStarted = uploadStarted ?? previous?.UploadStarted,
            SubmissionStarted = submissionStarted ?? previous?.SubmissionStarted,
            RetryPolicy = retryPolicy ?? previous?.RetryPolicy ?? ""
        };
        Persist();
    }
    public string SlotText(string slot)
    {
        var key = Schedule.Key(slot, now());
        if (State.Confirmed.Contains(key)) return $"{slot} 已核对成功。";
        if (State.Records.TryGetValue(key, out var record))
        {
            var text = $"{slot} {record.UpdatedAt} · {record.Outcome} · {record.Stage}\r\n{record.Message}";
            if (!State.Attempted.Contains(key) && record.RetryPolicy == "manual")
                return text + "\r\n尚未上传或提交；问题解决后可点击“立即打卡”手动重试，仍检查剩余次数及两小时间隔。";
            if (State.Attempted.Contains(key)) return text + "\r\n已有上传尝试或结果未确认，请在小程序核对；不会重复提交。";
            return text;
        }
        return State.Attempted.Contains(key) ? $"{slot} 已尝试，首次错误未记录；请在小程序核对。" : $"{slot} 待执行。";
    }
    public async Task<bool> ImportAsync(string token, Func<UserProfile, bool> approveFirstAccount)
    {
        if (Busy) return false;
        Busy = true; Changed?.Invoke();
        try
        {
            token = token.Trim();
            var user = await api.UserAsync(token);
            if (Session is not null && Session.AccountId != user.AccountId) throw new BusinessException(0, "新 token 属于其他账号，原会话已保留。");
            if (Session is null && !approveFirstAccount(user)) { Message("已取消绑定账号，没有保存 token。"); return false; }
            var candidate = new Session(token, user.AccountId, user.Nickname);
            var paused = State.NeedsLogin;
            var previousLocation = State.Location;
            var previousSource = State.LocationSource;
            State.NeedsLogin = false;
            SynchronizeLocation(user);
            try { save(candidate, State); }
            catch { State.NeedsLogin = paused; State.Location = previousLocation; State.LocationSource = previousSource; throw; }
            Session = candidate; Changed?.Invoke();
            Message($"会话已核验并保存，累计成功 {user.Successes} 次。服务器未提供 token 到期时间。");
            return true;
        }
        catch (Exception error) { Message("导入失败：" + error.Message); return false; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    private async Task<UserProfile> UserAsync()
    {
        if (Session is null) throw new LoginRequiredException();
        var original = Session;
        var user = await api.UserAsync(original.Token);
        if (user.AccountId != original.AccountId) throw new BusinessException(0, "账号与绑定记录不一致，已停止操作。");
        if (user.IssuedToken.Length > 0 && user.IssuedToken != original.Token)
        {
            try
            {
                var verified = await api.UserAsync(user.IssuedToken);
                if (verified.AccountId != original.AccountId) throw new BusinessException(0, "平台新 token 账号不一致。");
                var candidate = new Session(user.IssuedToken, verified.AccountId, verified.Nickname);
                save(candidate, State);
                Session = candidate;
                return verified;
            }
            catch { /* Retain the valid original when a candidate cannot be verified or saved. */ }
        }
        return user;
    }
    public async Task CheckAsync()
    {
        if (Busy) return;
        Busy = true; Changed?.Invoke();
        try
        {
            var user = await UserAsync(); SaveConnectedProfile(user);
            Message($"当前登录有效，累计成功 {user.Successes} 次，当天剩余计数 {user.Remaining}。token 到期时间未知。");
        }
        catch (Exception error) { Handle(error); }
        finally { Busy = false; Changed?.Invoke(); }
    }
    public async Task<ConnectionResult> ConnectAsync()
    {
        if (Busy) return ConnectionResult.Failed;
        if (Session is null) return ConnectionResult.NeedsCapture;
        Busy = true; Changed?.Invoke();
        try
        {
            var user = await UserAsync(); SaveConnectedProfile(user);
            Message("已连接，保存的登录仍有效，无需重新采集或设置代理和证书。");
            return ConnectionResult.Connected;
        }
        catch (LoginRequiredException error)
        {
            Handle(error); return ConnectionResult.NeedsCapture;
        }
        catch (Exception error)
        {
            Message("未能检查已保存的登录，请稍后重试。" + error.Message);
            return ConnectionResult.Failed;
        }
        finally { Busy = false; Changed?.Invoke(); }
    }
    private void Handle(Exception error)
    {
        if (error is LoginRequiredException) { State.NeedsLogin = true; Persist(); }
        Message(error is HttpRequestException or TaskCanceledException ? "网络请求失败：" + error.Message : error.Message);
    }
    private void SynchronizeLocation(UserProfile user)
    {
        if (!syncWechatLocation) return;
        State.Location = user.WechatLocation;
        State.LocationSource = "wechat";
    }
    private void SaveConnectedProfile(UserProfile user)
    {
        var previousLocation = State.Location;
        var previousSource = State.LocationSource;
        var previousLogin = State.NeedsLogin;
        SynchronizeLocation(user); State.NeedsLogin = false;
        try { Persist(); }
        catch
        {
            State.Location = previousLocation; State.LocationSource = previousSource; State.NeedsLogin = previousLogin;
            throw;
        }
    }
    private void ValidateSettings(AppState state, bool beforeAccountQuery = false)
    {
        if (state.GroupId.Length > 0 && !int.TryParse(state.GroupId, out _)) throw new ArgumentException("组织 ID 需为整数，或按本人请求留空。");
        if (syncWechatLocation && beforeAccountQuery) return;
        if (PlateLocation.Normalize(state.Location).Length == 0)
            throw new ArgumentException(syncWechatLocation
                ? "未取得微信小程序的有效最近位置。请在小程序更新位置后重试，未上传照片。"
                : "请填写当天真实位置，格式为纬度,经度。");
    }
    private static bool TransientNetwork(Exception error) => error is TaskCanceledException
        || error is HttpRequestException { HttpRequestError: System.Net.Http.HttpRequestError.ConnectionError
            or System.Net.Http.HttpRequestError.NameResolutionError or System.Net.Http.HttpRequestError.ResponseEnded };
    public async Task TickAsync(Func<(byte[] Bytes, string Mime, string Extension)> defaultPhoto)
    {
        var slot = Schedule.Due(now());
        if (!State.ScheduleEnabled || State.NeedsLogin || Session is null || Busy || slot is null) return;
        var key = Schedule.Key(slot, now());
        if (!State.Attempted.Contains(key) && !State.Confirmed.Contains(key) && State.Records.GetValueOrDefault(key)?.RetryPolicy != "manual")
            await SubmitAsync(slot, defaultPhoto);
    }
    public async Task SubmitAsync(string slot, Func<(byte[] Bytes, string Mime, string Extension)> readPhoto)
    {
        if (Busy || !Schedule.Slots.Contains(slot)) return;
        var key = Schedule.Key(slot, now());
        if (State.NeedsLogin || Session is null) { Message("请先点击“连接微信”获取有效的本人登录。"); return; }
        if (State.Attempted.Contains(key) || State.Confirmed.Contains(key)) { Message(SlotText(slot) + " 本时段不会重复提交，请先核对小程序。"); return; }
        try { ValidateSettings(State, beforeAccountQuery: true); } catch (Exception error) { Message(error.Message); return; }
        Busy = true;
        var stage = "准备照片"; var uploadStarted = false; var submissionStarted = false; var rejected = false;
        try
        {
            State.Attempted.Add(key); Record(key, stage, "执行中", "", uploadStarted: false, submissionStarted: false, retryPolicy: "");
            var photo = readPhoto();
            if (photo.Bytes.Length is < 1024 or > 20 * 1024 * 1024 || photo.Mime is not ("image/png" or "image/jpeg")
                || photo.Extension is not ("png" or "jpg")) throw new ArgumentException("请选择 1 KB 至 20 MB 的有效 JPG 或 PNG 照片。");
            stage = "查询账号"; Record(key, stage, "执行中", "");
            var before = await UserAsync();
            if (syncWechatLocation) { SynchronizeLocation(before); ValidateSettings(State); }
            if (before.LastCheckIn is { } last && now() < last.AddHours(2))
            {
                State.Attempted.Remove(key);
                Record(key, stage, "等待间隔", $"上次打卡尚未满 2 小时，最早 {Schedule.Timestamp(last.AddHours(2))} 可提交；错过定时窗口不补交。", retryPolicy: "automatic");
                Message(SlotText(slot)); return;
            }
            if (before.Remaining <= 0) throw new BusinessException(12010, "当天剩余次数为 0，未上传照片。");
            stage = "上传照片"; Record(key, stage, "执行中", "", uploadStarted: true); uploadStarted = true;
            var path = await api.UploadAsync(Session!.Token, photo.Bytes, photo.Mime, photo.Extension);
            stage = "提交打卡"; Record(key, stage, "执行中", "", submissionStarted: true); submissionStarted = true;
            try { await api.PunchAsync(Session!.Token, State.GroupId, State.Location, path); }
            catch (BusinessException error) when (error.Code != 0) { rejected = true; throw; }
            stage = "核对成功记录"; Record(key, stage, "执行中", "");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(1000);
                var after = await UserAsync();
                if (after.Successes > before.Successes)
                {
                    State.Confirmed.Add(key); Record(key, stage, "成功", "接口返回 10007，且累计成功次数增加。");
                    Message($"{slot} 打卡成功，累计 {after.Successes} 次。"); return;
                }
            }
            throw new BusinessException(0, "提交返回成功，但未查到新增成功记录，请在小程序核对，勿重复提交。");
        }
        catch (Exception error)
        {
            if (!uploadStarted)
            {
                State.Attempted.Remove(key);
                var offline = stage == "查询账号" && TransientNetwork(error);
                var quota = error is BusinessException { Code: 12010 };
                var outcome = offline ? "等待网络" : quota ? "等待条件" : "未提交";
                var message = offline ? "尚未上传或提交。网络恢复后仅在原 5 分钟窗口内重试；错过窗口不补交。 " + error.Message : error.Message;
                Record(key, stage, outcome, message, uploadStarted: false, submissionStarted: false, retryPolicy: offline || quota ? "automatic" : "manual");
                Handle(error);
                Message(SlotText(slot)); return;
            }
            Record(key, stage, submissionStarted && !rejected ? "结果未确认" : "未成功", error.Message);
            Handle(error);
        }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
