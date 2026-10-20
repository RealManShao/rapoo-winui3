using System.Text.Json;

namespace Rapoo.Core;

public sealed class ConfigurationStore
{
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RapooControl");
    public string ProtocolPath => Path.Combine(DirectoryPath, "protocol-overrides.json");

    public IReadOnlyDictionary<int, byte> LoadExtraRateCodes()
    {
        if (!File.Exists(ProtocolPath)) return new Dictionary<int, byte>();
        var codes = JsonSerializer.Deserialize<Dictionary<int, byte>>(File.ReadAllText(ProtocolPath))
                    ?? throw new InvalidDataException("自定义回报率代码为空。");
        if (codes.Keys.Any(k => k is not (125 or 4000 or 8000)))
            throw new InvalidDataException("自定义代码仅支持 125、4000、8000 Hz；JSON 值使用十进制字节。");
        return codes;
    }

}
