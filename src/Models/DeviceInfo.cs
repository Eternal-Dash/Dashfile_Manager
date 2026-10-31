using System;
using System.Text.RegularExpressions;

namespace DashFile.Models;

public sealed class DeviceInfo
{
    public string Serial { get; init; } = "";
    public string State { get; init; } = "";
    public string Model { get; init; } = "";
    public string Product { get; init; } = "";

    public bool IsOnline => State.Equals("device", StringComparison.OrdinalIgnoreCase);

    public string Display
    {
        get
        {
            var m = string.IsNullOrWhiteSpace(Model) ? Product : Model;
            var name = string.IsNullOrWhiteSpace(m) ? Serial : $"{m}  ·  {Serial}";
            if (!IsOnline) name += $"   [{State}]";
            return name;
        }
    }

    public override string ToString() => Display;

    public static DeviceInfo? Parse(string line)
    {
        // 1234567890abc  device product:xxx model:OPD2413 device:yyy transport_id:3
        var m = Regex.Match(line, @"^(?<serial>\S+)\s+(?<state>\S+)(?<rest>.*)$");
        if (!m.Success) return null;
        var rest = m.Groups["rest"].Value;
        string Pick(string key)
        {
            var mm = Regex.Match(rest, key + @":(?<v>\S+)");
            return mm.Success ? mm.Groups["v"].Value : "";
        }
        return new DeviceInfo
        {
            Serial = m.Groups["serial"].Value,
            State = m.Groups["state"].Value,
            Model = Pick("model"),
            Product = Pick("product"),
        };
    }
}
