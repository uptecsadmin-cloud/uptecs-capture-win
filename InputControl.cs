using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using SIPSorcery.Net;

static class InputControl
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    public static void Attach(RTCPeerConnection pc)
    {
        pc.ondatachannel += (dc) =>
        {
            Console.WriteLine("datachannel " + dc.label);
            dc.onmessage += (_, _, data) => Handle(data);
        };
    }

    static void Handle(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var t = doc.RootElement.GetProperty("t").GetString();
            var bounds = Screen.PrimaryScreen!.Bounds;
            if (t == "click")
            {
                var x = bounds.X + (int)(doc.RootElement.GetProperty("x").GetDouble() * bounds.Width);
                var y = bounds.Y + (int)(doc.RootElement.GetProperty("y").GetDouble() * bounds.Height);
                x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
                y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
                SetCursorPos(x, y);
                mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Console.WriteLine($"click {x},{y} on primary {bounds.Width}x{bounds.Height}");
            }
            else if (t == "key")
            {
                var k = doc.RootElement.GetProperty("k").GetString() ?? "";
                if (k.Length == 1) SendKeys.SendWait(k);
                else if (k == "Enter") SendKeys.SendWait("{ENTER}");
                else if (k == "Backspace") SendKeys.SendWait("{BACKSPACE}");
            }
        }
        catch (Exception ex) { Console.WriteLine("input " + ex.Message); }
    }
}
