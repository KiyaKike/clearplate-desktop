using ClearPlate.Core;
using System.Diagnostics;

namespace ClearPlate.Windows;

public sealed class MainForm : Form
{
    private readonly PlateEngine engine;
    private readonly TextBox group = new() { Width = 200 };
    private readonly Label location = new() { Name = "WechatLocationStatus", AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly CheckBox schedule = new() { AutoSize = true, Text = "每天 08:00 / 12:00 / 18:00 自动用内置照片打卡" };
    private readonly ComboBox slot = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label account = new() { AutoSize = true };
    private readonly Label overview = new() { AutoSize = true };
    private readonly TextBox record = new() { ReadOnly = true, Multiline = true, Width = 640, Height = 100, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox status = new() { ReadOnly = true, Multiline = true, Width = 640, Height = 80, ScrollBars = ScrollBars.Vertical };
    private readonly List<Button> actionButtons = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private readonly NotifyIcon tray = new() { Text = "光盘打卡工具" };
    private readonly ContextMenuStrip moreMenu = new();
    private readonly Icon appIcon;
    private readonly Image preview;
    private WechatTokenCapture? capture;
    private readonly System.Windows.Forms.Timer captureTimer = new() { Interval = 180000 };
    private readonly Button stopCapture = new() { Text = "停止连接并恢复设置", AutoSize = true, Visible = false, Enabled = false };
    private bool importingCapture;
    public MainForm(WindowsStore store, HttpClient client, bool startTimer = true)
    {
        var saved = store.Load();
        engine = new(new PlateApi(client), saved.State, saved.Session, store.Save, syncWechatLocation: true);
        Text = $"光盘打卡 · Windows x64 · v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)} 试验版";
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ClearPlate.icon.ico") ?? throw new IOException("应用图标缺失。"))
        using (var originalIcon = new Icon(stream)) appIcon = (Icon)originalIcon.Clone();
        Icon = appIcon; tray.Icon = appIcon;
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(710, 760);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(22) };
        Controls.Add(content);
        var title = new Label { Text = "光盘打卡", AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold), ForeColor = Color.FromArgb(27, 115, 78) };
        content.Controls.Add(title);
        content.Controls.Add(Note("08:00 · 12:00 · 18:00（北京时间）  |  每餐一次，至少间隔 2 小时"));
        content.Controls.Add(account);
        content.Controls.Add(Row(new Label { Text = "组织 ID", Width = 100, AutoSize = false }, group));
        content.Controls.Add(Row(new Label { Text = "微信最近位置", Width = 120, AutoSize = false }, location));
        group.Text = engine.State.GroupId;
        content.Controls.Add(Note("位置沿用微信小程序最近记录；移动后请在小程序更新位置。每次打卡前同步，无需填写经纬度。"));
        using (var stream = new MemoryStream(DefaultPhoto().Bytes)) preview = new Bitmap(stream);
        content.Controls.Add(Row(new PictureBox { Image = preview, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(82, 82) },
            Note("内置照片：plate.png 原图\r\n自动打卡使用内置照片；手动可选择其他照片。")));
        content.Controls.Add(schedule);
        content.Controls.Add(Note("保持程序运行、电脑唤醒且联网。最小化进入托盘，关闭窗口会退出。"));
        content.Controls.Add(overview);
        slot.Items.AddRange(Schedule.Slots.Cast<object>().ToArray());
        slot.SelectedItem = Schedule.Suggested(DateTimeOffset.UtcNow);
        content.Controls.Add(Row(new Label { Text = "手动打卡时段", Width = 120, AutoSize = false }, slot));
        content.Controls.Add(record);
        moreMenu.Items.Add(MenuAction("选择照片打卡…", SelectPhotoAsync));
        moreMenu.Items.Add(MenuAction("检查账号状态", async () => { SaveSettings(); await engine.CheckAsync(); }));
        moreMenu.Items.Add(MenuAction("同步微信最近位置", async () => { SaveSettings(); await engine.CheckAsync(); }));
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add(MenuAction("打开光盘小程序", () => { OpenMiniProgram(); return Task.CompletedTask; }));
        moreMenu.Items.Add(MenuAction("重新获取微信登录…", StartCaptureAsync));
        moreMenu.Items.Add(MenuAction("手动导入登录凭据…", ShowTokenImportAsync));
        var more = ActionButton("更多", () => Task.CompletedTask);
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));
        var actions = Row(ActionButton("立即打卡", async () => { SaveSettings(); await engine.SubmitAsync(SelectedSlot, DefaultPhoto); }),
            ActionButton("连接微信", ConnectWeChatAsync), more);
        actions.Name = "PrimaryActions";
        content.Controls.Add(actions);
        content.Controls.Add(stopCapture);
        content.Controls.Add(Note("连接微信会先检查已有登录；有效时直接复用，需要重新登录时才请求采集授权。"));
        stopCapture.Click += (_, _) => {
            try { StopCapture(); status.Text = "采集已停止，临时证书已移除；代理已恢复或保留你采集期间的新设置。"; }
            catch (Exception error) { status.Text = error.Message; }
        };
        captureTimer.Tick += (_, _) => {
            try { StopCapture(); status.Text = "3 分钟内未捕获 token，已恢复代理并移除临时证书。可能是微信未使用系统代理或未接受证书；这不表示账号没有 token。"; }
            catch (Exception error) { status.Text = error.Message; }
        };
        content.Controls.Add(status);
        content.Controls.Add(Note("上传前失败可手动重试，查询时断网可在原窗口内自动重试；上传后结果不明不重复提交。\r\n登录过期暂停，请正常登录微信后点“连接微信”；目前没有独立续期机制。"));
        schedule.Checked = engine.State.ScheduleEnabled;
        schedule.CheckedChanged += (_, _) => { engine.State.ScheduleEnabled = schedule.Checked; SaveSettings(); };
        slot.SelectedIndexChanged += (_, _) => UpdateUi();
        engine.Changed += UpdateUi;
        timer.Tick += async (_, _) => {
            if (capture is not null || importingCapture) return;
            try { await engine.TickAsync(DefaultPhoto); }
            catch (Exception error) { status.Text = "本地记录保存失败，自动计划已停止：" + error.Message; timer.Stop(); }
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => RestoreWindow());
        menu.Items.Add("退出", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { Hide(); tray.Visible = true; } };
        FormClosing += (_, e) => {
            if (engine.Busy || importingCapture) { e.Cancel = true; status.Text = "正在执行请求，请等待结果后再退出。"; return; }
            try { StopCapture(); }
            catch (Exception error) { e.Cancel = true; status.Text = error.Message; return; }
            timer.Stop(); tray.Visible = false;
        };
        if (startTimer) timer.Start();
        UpdateUi();
    }
    private string SelectedSlot => slot.SelectedItem as string ?? "08:00";
    private bool ApproveAccount(UserProfile user) => MessageBox.Show(this,
        $"平台返回账号：{user.Nickname}\r\n用户 ID：{user.AccountId}\r\n确认这是本人账号并绑定？", "绑定本人账号",
        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    private static void OpenMiniProgram() => Process.Start(new ProcessStartInfo("weixin://dl/business/?appid=wx6a4976bd627fc4ca&path=pages/index/dataHomePage") { UseShellExecute = true });
    private async Task ConnectWeChatAsync()
    {
        SaveSettings();
        if (await engine.ConnectAsync() == ConnectionResult.NeedsCapture) await StartCaptureAsync();
    }
    private Task ShowTokenImportAsync()
    {
        using var dialog = new Form { Text = "手动导入登录凭据", ClientSize = new Size(520, 245),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = Font, Icon = appIcon };
        using var input = new TextBox { UseSystemPasswordChar = true, Width = 460, PlaceholderText = "本人完整 X-Token" };
        using var note = new Label { AutoSize = true, MaximumSize = new Size(460, 0), Text = "备用方式：核验原账号一致后保存，凭据由当前 Windows 用户的 DPAPI 加密。" };
        using var result = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        using var confirm = new Button { Text = "核验并保存", AutoSize = true };
        using var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        using var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20) };
        layout.Controls.AddRange([note, input, Row(confirm, cancel), result]); dialog.Controls.Add(layout);
        confirm.Click += async (_, _) => {
            input.Enabled = confirm.Enabled = cancel.Enabled = false;
            try
            {
                SaveSettings();
                if (await engine.ImportAsync(input.Text, ApproveAccount)) { input.Clear(); dialog.Close(); }
                else result.Text = engine.Status;
            }
            catch (Exception error) { result.Text = error.Message; }
            finally { if (!dialog.IsDisposed) input.Enabled = confirm.Enabled = cancel.Enabled = true; }
        };
        dialog.FormClosing += (_, e) => { if (engine.Busy) e.Cancel = true; };
        dialog.AcceptButton = confirm; dialog.CancelButton = cancel;
        dialog.ShowDialog(this); return Task.CompletedTask;
    }
    private Task StartCaptureAsync()
    {
        if (capture is not null || engine.Busy) return Task.CompletedTask;
        var consent = MessageBox.Show(this,
            "这一步将临时启用本机 HTTPS 代理，并信任仅用于光盘 API 域名的临时证书。\r\n\r\n" +
            "只有 clearplate-api.guangpanxingdong.com 的 HTTPS 内容会被本工具读取；其他 HTTPS 连接仅转发，不解密、不记录。捕获的 token 只向光盘平台核验，原账号一致才保存。\r\n\r\n" +
            "采集期间请正常打开小程序首页或“我的”，不要额外打卡。最多 3 分钟，结束后恢复原代理并移除证书；异常退出由独立恢复进程及下次启动恢复。若你修改了代理设置，本工具不会覆盖你的新设置。\r\n\r\n" +
            "Windows 微信可能不使用系统代理或拒绝此证书，此功能不保证在所有版本上可用。仍需你正常登录微信，不能绕过登录或独立续期。\r\n\r\n是否同意现在启用临时代理和证书？",
            "同意后才自动获取 token", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (consent != DialogResult.Yes) { status.Text = "未启用代理或证书，可以继续使用手动导入。"; return Task.CompletedTask; }
        // A timer may have started a request while the modal consent was open.
        // Never switch networking underneath an active upload/check-in.
        if (engine.Busy || capture is not null || importingCapture) { status.Text = "正在执行请求，请等待结束后再连接微信。"; return Task.CompletedTask; }
        try
        {
            SaveSettings();
            var helper = new WechatTokenCapture(); capture = helper;
            helper.Diagnostic += message => {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => { if (capture == helper) status.Text = CaptureDiagnostics.Describe(message); }));
            };
            helper.TokenFound += candidate => {
                if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(async () => {
                    if (capture != helper || importingCapture) return;
                    importingCapture = true;
                    try
                    {
                        StopCapture(); status.Text = "已捕获凭据并恢复代理，正在向光盘平台核验账号…";
                        // Fresh client after restoring original networking avoids cached temporary proxy settings.
                        using var checkClient = Program.CreateClient();
                        var profile = await new PlateApi(checkClient).UserAsync(candidate);
                        if (engine.Session is not null && profile.AccountId != engine.Session.AccountId) throw new IOException("捕获的是其他账号，原会话已保留。");
                        if (await engine.ImportAsync(candidate, ApproveAccount)) { status.Text = engine.Status + " 已从微信自动同步，无需复制粘贴。"; }
                        else status.Text = engine.Status;
                    }
                    catch (Exception error) { status.Text = "自动同步未完成：" + error.Message; }
                    finally { var completedStatus = status.Text; importingCapture = false; UpdateUi(); status.Text = completedStatus; }
                }));
            };
            helper.Start(); captureTimer.Start(); UpdateUi();
            status.Text = "正在等候微信请求。请打开光盘打卡首页或“我的”；如会话已过期，请正常登录。凭据捕获后会自动核验保存，最多等候 3 分钟。";
            try { OpenMiniProgram(); } catch { status.Text += " 请在 Windows 微信中手动打开小程序。"; }
        }
        catch (Exception error)
        {
            try { StopCapture(); } catch (Exception restoreError) { status.Text = restoreError.Message; return Task.CompletedTask; }
            status.Text = "无法开始自动获取：" + error.Message;
        }
        return Task.CompletedTask;
    }
    private void StopCapture()
    {
        captureTimer.Stop();
        var helper = capture; capture = null;
        try { helper?.Dispose(); }
        finally { stopCapture.Enabled = false; UpdateUi(); }
    }
    private static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 7, 0, 7) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Width = 640, WrapContents = true, Margin = new Padding(0, 5, 0, 5) };
        row.Controls.AddRange(controls); return row;
    }
    private Button ActionButton(string caption, Func<Task> action)
    {
        var button = new Button { Text = caption, AutoSize = true, Padding = new Padding(6), Margin = new Padding(0, 0, 8, 0) };
        button.Click += async (_, _) => {
            try { await action(); }
            catch (Exception error) { status.Text = "操作已停止：" + error.Message; }
        };
        actionButtons.Add(button); return button;
    }
    private ToolStripMenuItem MenuAction(string caption, Func<Task> action)
    {
        var item = new ToolStripMenuItem(caption);
        item.Click += async (_, _) => {
            try { await action(); }
            catch (Exception error) { status.Text = "操作已停止：" + error.Message; }
        };
        return item;
    }
    private void SaveSettings()
    {
        engine.State.GroupId = group.Text.Trim(); engine.Persist();
    }
    private async Task SelectPhotoAsync()
    {
        using var picker = new OpenFileDialog { Filter = "光盘照片|*.png;*.jpg;*.jpeg", Title = "选择当天真实光盘照片", Multiselect = false };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var path = picker.FileName;
        SaveSettings();
        await engine.SubmitAsync(SelectedSlot, () => {
            var info = new FileInfo(path);
            if (info.Length is < 1024 or > 20 * 1024 * 1024) throw new ArgumentException("照片需为 1 KB 至 20 MB。");
            using var image = Image.FromFile(path);
            var bytes = File.ReadAllBytes(path);
            var png = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);
            return (bytes, png ? "image/png" : "image/jpeg", png ? "png" : "jpg");
        });
    }
    internal static (byte[] Bytes, string Mime, string Extension) DefaultPhoto()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ClearPlate.plate.png") ?? throw new IOException("内置照片缺失。");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return (buffer.ToArray(), "image/png", "png");
    }
    private void UpdateUi()
    {
        account.Text = engine.Session is null ? "尚未配置会话" : "账号：" + engine.Session.Nickname;
        location.Text = engine.State.LocationSource == "wechat" && engine.State.Location.Length > 0
            ? "已从本人账号同步；打卡前再次检查" : "尚未取得有效位置，请在小程序更新后同步";
        status.Text = engine.Status; record.Text = engine.SlotText(SelectedSlot);
        overview.Text = string.Join(" · ", Schedule.Slots.Select(s => {
            var key = Schedule.Key(s, DateTimeOffset.UtcNow);
            var detail = engine.State.Records.GetValueOrDefault(key);
            return s + " " + (engine.State.Confirmed.Contains(key) ? "成功" : !engine.State.Attempted.Contains(key) && detail?.RetryPolicy == "manual"
                ? "未提交（可手动重试）" : detail?.Outcome ?? "待执行");
        }));
        var ready = !engine.Busy && capture is null && !importingCapture;
        foreach (var button in actionButtons) button.Enabled = ready;
        group.Enabled = slot.Enabled = schedule.Enabled = ready;
        stopCapture.Visible = stopCapture.Enabled = capture is not null;
    }
    private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); tray.Visible = false; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { capture?.Dispose(); captureTimer.Dispose(); timer.Dispose(); tray.Dispose(); moreMenu.Dispose(); preview.Dispose(); appIcon.Dispose(); }
        base.Dispose(disposing);
    }
}
