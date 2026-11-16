using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace TeleSave.TdLib;

public static class TdLibInfo
{
    /// <summary>Loads tdjson and runs a synchronous request; throws if the native library is missing or broken.</summary>
    public static string SelfCheck()
    {
        var raw = Native.td_execute(new JsonObject { ["@type"] = "getLogVerbosityLevel" }.ToJsonString());
        var json = raw == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(raw);
        return json?.Contains("logVerbosityLevel") == true ? "tdjson ok" : throw new InvalidOperationException("tdjson answered: " + json);
    }
}
