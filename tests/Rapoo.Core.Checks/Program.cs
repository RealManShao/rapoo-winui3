using Rapoo.Core;
using Rapoo.Hid;

if (args.Contains("--device"))
{
    await using var mouse = new RapooMouseService();
    mouse.Log += line =>
    {
        if (line.StartsWith("TX ") || line.StartsWith("RX ") || line.Contains("回报率")) Console.WriteLine(line);
    };
    await mouse.ConnectAsync();
    var configuration = await mouse.ReadDeviceConfigurationAsync();
    if (configuration is null) throw new InvalidOperationException("Device configuration read failed; see diagnostic response above.");
    Console.WriteLine($"Device configured polling rate: {configuration.PollingRate} Hz (queried once; no settings written).");
    Console.WriteLine("Device DPI stages: " + string.Join(", ", configuration.Stages.Select(s => $"{s.Dpi}:{s.Enabled}")));
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var time = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
var log = new RecentCommunicationLog(3);
for (var i = 0; i < 100_000; i++) log.Add("BAT BBB071AA050123", time.AddMilliseconds(i));
var lines = log.Format().Split(Environment.NewLine);
Check(lines.Length == 1, "Identical reports must occupy one entry.");
Check(lines[0].StartsWith($"[{time.AddMilliseconds(99_999).LocalDateTime:HH:mm:ss.fff}]"),
    "Duplicate reports must retain the newest timestamp.");

log.Add("RX A", time);
log.Add("TX A", time);
log.Add("RX A", time.AddSeconds(1));
log.Add("BAT changed", time.AddSeconds(2));
lines = log.Format().Split(Environment.NewLine);
Check(lines.Length == 3, "Distinct messages must respect the capacity limit.");
Check(!log.Format().Contains("BBB071"), "The least recently seen entry must be evicted.");
Check(lines[0].EndsWith("TX A") && lines[1].EndsWith("RX A"),
    "Directions must remain distinct and a repeated message must move to the newest position.");

log.Clear();
Check(log.Format() == "", "Clearing must remove all cached records.");
log.Add(new string('x', 100_000), time);
Check(log.Format().Length < 2100, "Oversized diagnostics must not bypass the memory limit.");

var concurrent = new RecentCommunicationLog();
Parallel.For(0, 20_000, i =>
{
    concurrent.Add($"RX {i % 256}", time.AddMilliseconds(i));
    if (i % 100 == 0) _ = concurrent.Format();
});
Check(concurrent.Format().Split(Environment.NewLine).Length == 128,
    "Concurrent producers and readers must preserve the capacity limit.");

Check(RapooProtocol.TryParseBattery(Convert.FromHexString("BBB071AA050123"), out var reading) &&
    reading!.Percent == 35 && reading.CurrentDpi == 1450,
    "The passive report must supply both battery and DPI.");
Check(!RapooProtocol.TryParseBattery(Convert.FromHexString("BBB071AA0501FF"), out _),
    "Invalid battery reports must not overwrite the latest valid reading.");
foreach (var option in RapooProtocol.PollingRates())
{
    var response = Convert.FromHexString($"01000000{option.Code:X2}0101FF");
    Check(RapooProtocol.TryParsePollingRateResponse(response, out var actual) && actual == option.Hertz,
        $"Captured response must decode {option.Hertz} Hz.");
    var hidResponse = new byte[33];
    hidResponse[0] = 0xBA;
    response.CopyTo(hidResponse, 1);
    Check(RapooProtocol.TryParsePollingRateResponse(hidResponse, out actual) && actual == option.Hertz,
        "HID report ID prefix and trailing padding must be supported.");
}
foreach (var invalid in new[] { "0100", "0200", "01000000FF0101FF", "0100000001010100", "00000000010101FF" })
    Check(!RapooProtocol.TryParsePollingRateResponse(Convert.FromHexString(invalid), out _),
        "Acknowledgements, malformed headers and unknown codes must not produce a current rate.");
Check(Convert.ToHexString(RapooProtocol.CreatePollingRateQuery()) ==
    "BAA5A40480080000000000000000000000000000000000000000000000000000",
    "Read request must match the captured four-byte read at 0x0880.");
var fullResponse = Convert.FromHexString("01000000040101FF020001002003AA059001B004800C0019C8AF0600");
Check(RapooProtocol.TryParseDeviceConfigurationResponse(fullResponse, out var configurationRead) &&
    configurationRead!.PollingRate == 250 && configurationRead.Stages.All(s => s.Enabled) &&
    configurationRead.Stages.Select(s => s.Dpi).SequenceEqual(new[] { 800, 1450, 400, 1200, 3200, 6400, 45000 }),
    "Full configuration must match capture frame 454, including seven enabled stages.");
fullResponse[26] = 2;
Check(RapooProtocol.TryParseDeviceConfigurationResponse(fullResponse, out configurationRead) &&
    configurationRead!.Stages.Count(s => s.Enabled) == 3 && !configurationRead.Stages[3].Enabled,
    "Enabled stages must be decoded from the count-minus-one field, not local defaults.");
fullResponse[26] = 7;
Check(!RapooProtocol.TryParseDeviceConfigurationResponse(fullResponse, out _), "Invalid enabled counts must be rejected.");
Check(!RapooProtocol.TryParseDeviceConfigurationResponse(fullResponse.AsSpan(0, 8), out _),
    "A short rate response must not be accepted as a complete DPI configuration.");
Console.WriteLine("PASS: bounded logs, concurrency, telemetry parsing, all seven polling rates, HID framing and invalid responses.");
