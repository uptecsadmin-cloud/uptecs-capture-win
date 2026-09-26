using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

var api = Environment.GetEnvironmentVariable("UPTECS_RMM_API") ?? "https://monitoring.uptecs.com/api/rmm";
var tenant = Environment.GetEnvironmentVariable("UPTECS_TENANT") ?? "IMC";
var enroll = Environment.GetEnvironmentVariable("UPTECS_ENROLL") ?? "uptecs-rmm-enroll-2026";
var hostname = Environment.GetEnvironmentVariable("UPTECS_HOSTNAME");
if (string.IsNullOrWhiteSpace(hostname)) hostname = Environment.MachineName;
var cfgPath = Path.Combine(AppContext.BaseDirectory, "agent.json");

Console.WriteLine("UPTecs RMM capture helper v2.3-ice");
using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/") };

string? deviceId = Environment.GetEnvironmentVariable("UPTECS_DEVICE_ID");
string? deviceToken = Environment.GetEnvironmentVariable("UPTECS_DEVICE_TOKEN");
if (File.Exists(cfgPath))
{
    try
    {
        using var cfg = JsonDocument.Parse(File.ReadAllText(cfgPath));
        if (cfg.RootElement.TryGetProperty("api", out var a) && a.GetString() is string api2 && api2.Length > 0)
            http.BaseAddress = new Uri(api2.TrimEnd('/') + "/");
        if (string.IsNullOrWhiteSpace(deviceId) && cfg.RootElement.TryGetProperty("deviceId", out var d))
            deviceId = d.GetString();
        if (string.IsNullOrWhiteSpace(deviceToken) && cfg.RootElement.TryGetProperty("deviceToken", out var t))
            deviceToken = t.GetString();
    }
    catch (Exception ex) { Console.WriteLine("agent.json " + ex.Message); }
}

if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(deviceToken))
{
    var enrolled = await http.PostAsJsonAsync("devices/enroll", new {
        tenantCode = tenant,
        enrollmentToken = enroll,
        hostname,
        os = new { family = "windows", version = Environment.OSVersion.VersionString },
        identity = new { kind = "software_key" },
        agent = new { version = "capture-2.3-ice" }
    });
    var enrollJson = await enrolled.Content.ReadAsStringAsync();
    if (!enrolled.IsSuccessStatusCode)
    {
        Console.Error.WriteLine(enrollJson);
        return 1;
    }
    using var doc = JsonDocument.Parse(enrollJson);
    deviceId = doc.RootElement.GetProperty("deviceId").GetString();
    deviceToken = doc.RootElement.GetProperty("deviceToken").GetString();
    File.WriteAllText(cfgPath, JsonSerializer.Serialize(new { api, deviceId, deviceToken, hostname }));
    Console.WriteLine("wrote " + cfgPath);
}

Console.WriteLine("bound " + deviceId + " as " + hostname);
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

var ice = new RTCConfiguration
{
    iceServers = new List<RTCIceServer>
    {
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        new RTCIceServer { urls = "turn:rmm.uptecs.com:3478?transport=udp", username = "uptecs", credential = "Turn-7kQ2mN9pX4" },
        new RTCIceServer { urls = "turn:rmm.uptecs.com:3478?transport=tcp", username = "uptecs", credential = "Turn-7kQ2mN9pX4" }
    }
};
var formats = new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.video, 96, "VP8", 90000) };

RTCPeerConnection? livePc = null;
var encoder = new VpxVideoEncoder();
var pumpCts = new CancellationTokenSource();
_ = Task.Run(() => PumpScreen(() => livePc, encoder, pumpCts.Token));
Console.WriteLine("full-screen pump started");

var answeredSession = "";
string? watchSession = null;
while (true)
{
    try
    {
        await http.PostAsJsonAsync($"devices/{deviceId}/heartbeat", new { health = HealthSampler.Read(), iis = IisSampler.Read() });
        var latest = await http.GetAsync($"devices/{deviceId}/latest-session");
        var latestText = await latest.Content.ReadAsStringAsync();
        if (latest.IsSuccessStatusCode && latestText.Contains("sessionId"))
        {
            using var sdoc = JsonDocument.Parse(latestText);
            var id = sdoc.RootElement.GetProperty("sessionId").GetString();
            if (id != watchSession) watchSession = id;
        }
        if (!string.IsNullOrWhiteSpace(watchSession) && watchSession != answeredSession)
        {
            var sigRes = await http.GetAsync("sessions/" + watchSession + "/signal");
            var sigText = await sigRes.Content.ReadAsStringAsync();
            if (sigRes.IsSuccessStatusCode && sigText.Contains("\"sdp\""))
            {
                using var bag = JsonDocument.Parse(sigText);
                string? sdp = null;
                foreach (var item in bag.RootElement.EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() == "offer")
                    {
                        sdp = item.GetProperty("payload").GetProperty("sdp").GetString();
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(sdp))
                {
                    answeredSession = watchSession;
                    try { livePc?.close(); } catch { }
                    var pc = new RTCPeerConnection(ice);
                    pc.addTrack(new MediaStreamTrack(SDPMediaTypesEnum.video, false, formats, MediaStreamStatusEnum.SendOnly));
                    InputControl.Attach(pc);
                    livePc = pc;
                    Console.WriteLine("setRemote " + pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }));
                    var answer = pc.createAnswer();
                    pc.setLocalDescription(answer);
                    Console.WriteLine("gathering ICE 4.5s");
                    await Task.Delay(4500);
                    await http.PostAsJsonAsync("sessions/" + watchSession + "/signal", new {
                        type = "answer", from = "agent", payload = new { sdp = answer.sdp }
                    });
                    Console.WriteLine("answer posted for " + watchSession);
                }
            }
        }
    }
    catch (Exception ex) { Console.WriteLine(ex.Message); }
    await Task.Delay(2000);
}

static void PumpScreen(Func<RTCPeerConnection?> current, VpxVideoEncoder encoder, CancellationToken ct)
{
    var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
    int w = bounds.Width;
    int h = bounds.Height;
    if (w > 1280) { h = h * 1280 / w; w = 1280; }
    w -= w % 2; h -= h % 2;
    Console.WriteLine($"capture {bounds.Width}x{bounds.Height} -> {w}x{h}");
    while (!ct.IsCancellationRequested)
    {
        var pc = current();
        try
        {
            if (pc != null)
            {
                using var src = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(src))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
                using var bmp = new Bitmap(src, new Size(w, h));
                var i420 = BgraToI420(bmp);
                var encoded = encoder.EncodeVideo(w, h, i420, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8);
                if (encoded != null && encoded.Length > 0)
                    pc.SendVideo(90000 / 12, encoded);
            }
        }
        catch (Exception ex) { Console.WriteLine("pump " + ex.Message); }
        Thread.Sleep(80);
    }
}

static byte[] BgraToI420(Bitmap bmp)
{
    var w = bmp.Width; var h = bmp.Height;
    var rect = new Rectangle(0, 0, w, h);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bgra = new byte[Math.Abs(data.Stride) * h];
    Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
    var stride = Math.Abs(data.Stride);
    bmp.UnlockBits(data);
    var ySize = w * h;
    var dst = new byte[ySize * 3 / 2];
    int yi = 0, ui = ySize, vi = ySize + ySize / 4;
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            dst[yi++] = (byte)Math.Clamp((77 * r + 150 * g + 29 * b) >> 8, 0, 255);
            if ((y & 1) == 0 && (x & 1) == 0)
            {
                dst[ui++] = (byte)Math.Clamp(((-43 * r - 85 * g + 128 * b) >> 8) + 128, 0, 255);
                dst[vi++] = (byte)Math.Clamp(((128 * r - 107 * g - 21 * b) >> 8) + 128, 0, 255);
            }
        }
    }
    return dst;
}
