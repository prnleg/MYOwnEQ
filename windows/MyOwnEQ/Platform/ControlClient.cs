using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MyOwnEQ.Platform;

/// <summary>Sends one request to the running app over the local control channel
/// (<c>control.json</c> in the data folder: port + token). Used when Windows starts a
/// second copy for a <c>myowneq://</c> link: it hands the link over and exits.</summary>
public static class ControlClient
{
    public static bool TrySend(JsonObject request, out string? error)
    {
        error = null;
        try
        {
            var info = JsonNode.Parse(File.ReadAllText(Path.Combine(CoreBridge.DataDir, "control.json")))!;
            request["token"] = info["token"]!.GetValue<string>();
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync("127.0.0.1", info["port"]!.GetValue<int>()).Wait(2000)) { error = "timeout"; return false; }
            using var s = tcp.GetStream();
            s.ReadTimeout = 10_000;
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
            s.Write(bytes, 0, bytes.Length);
            using var reader = new StreamReader(s, Encoding.UTF8);
            var reply = JsonNode.Parse(reader.ReadLine() ?? "{}");
            if (reply?["ok"]?.GetValue<bool>() == false) error = reply["error"]?.GetValue<string>();
            return error is null;
        }
        catch (Exception e) { error = e.Message; return false; }
    }
}
