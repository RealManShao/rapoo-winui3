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
    Console.WriteLine("Device Y DPI stages: " + string.Join(", ", configuration.Stages.Select(s => s.EffectiveYDpi)));
    var sensors = await mouse.ReadSensorConfigurationAsync();
    if (sensors is null) throw new InvalidOperationException("Sensor read failed.");
    Console.WriteLine($"Sensors: {sensors.Settings}");
    if (args.Contains("--verify-writes"))
    {
        // Reapply only values just read, then compare. Never substitute default configuration.
        var actualSensors = await mouse.SetSensorSettingsAsync(sensors.Settings);
        if (actualSensors.Settings != sensors.Settings) throw new InvalidOperationException("Sensor write/readback mismatch.");
        Console.WriteLine("PASS: unchanged sensor settings written, committed and read back.");
        await mouse.SetDpiAsync(configuration.Stages);
        var actualDpi = await mouse.ReadDeviceConfigurationAsync();
        if (actualDpi is null || actualDpi.PollingRate != configuration.PollingRate || !actualDpi.Stages.SequenceEqual(configuration.Stages))
            throw new InvalidOperationException("DPI write/readback mismatch.");
        Console.WriteLine("PASS: unchanged X/Y DPI written, committed and read back; polling rate preserved.");
    }
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
var reportedResponse = Convert.FromHexString("01000000010001FF03000100200340069001B004800C0019C8AF010000000000");
Check(RapooProtocol.TryParseDeviceConfigurationResponse(reportedResponse, out var reportedConfiguration) &&
    reportedConfiguration!.PollingRate == 1000 && reportedConfiguration.Stages.Count(s => s.Enabled) == 2 &&
    reportedConfiguration.Stages.Select(s => s.Dpi).SequenceEqual(new[] { 800, 1600, 400, 1200, 3200, 6400, 45000 }),
    "Accept the observed zero auxiliary byte and decode the reported DPI configuration.");
reportedResponse[5] = 2;
Check(!RapooProtocol.TryParseDeviceConfigurationResponse(reportedResponse, out _), "Reject unsupported auxiliary values.");
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

Check(RapooProtocol.TryDecodeSensors(Convert.FromHexString("02000100"), Convert.FromHexString("02020503"),
    Convert.FromHexString("00000100"), out var originalSensors), "Captured sensor blocks must decode.");
var originalSettings = originalSensors!.Settings;
Check(originalSettings == new SensorSettings(false, false, false, false, 2, 2, 2, 5, 0), "Decode captured baseline.");
var pressChange = RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { PressDebounceMs = 4 });
Check(Convert.ToHexString(pressChange[0]) == "BAA5A504C0080000040205030000000000000000000000000000000000000000",
    "Press debounce must match round 2 frame 39 and preserve release/sleep/correction.");
var releaseChange = RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { ReleaseDebounceMs = 8 });
Check(Convert.ToHexString(releaseChange[0].AsSpan(8, 4)) == "02080503", "Independent release debounce, frame 107.");
var sleepChange = RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { SleepMinutes = 6 });
Check(Convert.ToHexString(sleepChange[0].AsSpan(8, 4)) == "02020603", "Sleep minute encoding, frame 175.");
var negativeAngle = RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { Angle = -1 });
Check(Convert.ToHexString(negativeAngle[2].AsSpan(8, 4)) == "FF000100", "Negative angle, frame 329.");
var glassOn = RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { GlassTracking = true });
Check(Convert.ToHexString(glassOn[2].AsSpan(8, 4)) == "00010100", "Glass tracking, round 1 frame 1967.");
foreach (var (lod, expected) in new[] { (1, "01000100"), (2, "02000100"), (3, "03000100") })
    Check(Convert.ToHexString(RapooProtocol.CreateSensorTransaction(originalSensors, originalSettings with { LodCode = lod })[4].AsSpan(8, 4)) == expected,
        "LOD must use one-generation codes for 0.7/1/2 mm.");
var reserved = new SensorConfiguration(originalSettings, [2, 0, 0xA1, 0xB2], [2, 2, 5, 0xF3], [0, 0, 0xC3, 0xD4]);
var combined = RapooProtocol.CreateSensorTransaction(reserved, originalSettings with { LinearCorrection = true, RippleCorrection = true, MotionSync = true });
Check(Convert.ToHexString(combined[0].AsSpan(8, 4)) == "020205F0" &&
    Convert.ToHexString(combined[2].AsSpan(8, 4)) == "0000C3D4" &&
    Convert.ToHexString(combined[4].AsSpan(8, 4)) == "0201A1B2", "Preserve every reserved byte and upper correction bits.");
var copy = reserved.Block84; copy[2] = 0;
Check(reserved.Block84[2] == 0xA1, "Original register snapshots must not alias caller buffers.");
foreach (var invalidSettings in new[] { originalSettings with { LodCode = 4 }, originalSettings with { PressDebounceMs = 3 },
    originalSettings with { ReleaseDebounceMs = 0 }, originalSettings with { SleepMinutes = 4 }, originalSettings with { Angle = -31 } })
{
    var rejected = false;
    try { RapooProtocol.CreateSensorTransaction(originalSensors, invalidSettings); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Invalid sensor settings must never create a write transaction.");
}
foreach (var invalidResponse in new[] { "0100", "0200", "01000000FFFF", "0000000002000100" })
    Check(!RapooProtocol.TryParseRegisterResponse(Convert.FromHexString(invalidResponse), 4, out _), "Reject ACKs and malformed read responses.");
Check(RapooProtocol.TryParseRegisterResponse(Convert.FromHexString("BA0100000002000100000000"), 4, out var parsedBlock) &&
    Convert.ToHexString(parsedBlock) == "02000100", "Allow HID ID and padding in an explicit register read.");
var xyStages = new[] { new DpiStage(400, true, 450), new(800, false, 850), new(1600, true, 1650),
    new(1200, false, 1250), new(3200, false, 3250), new(6400, false, 6450), new(45000, false, 44950) };
var xy = RapooProtocol.CreateDpiTransaction(xyStages);
Check(Convert.ToHexString(xy[1].AsSpan(8, 4)) == "90014006" && Convert.ToHexString(xy[2].AsSpan(8, 4)) == "C2017206" &&
    xy[1][22] == 1 && xy[2][22] == 1, "Keep X/Y paired when packing enabled stages first.");
var yResponse = Convert.FromHexString("01000000C20120034006B004800C0019C8AF0200");
Check(RapooProtocol.TryParseDpiYResponse(yResponse, out var yParsed) && yParsed[0].Dpi == 450 && yParsed.Count(s => s.Enabled) == 3,
    "Decode seven Y stages including enable count.");
yResponse[4] = 0;
Check(!RapooProtocol.TryParseDpiYResponse(yResponse, out _), "Reject invalid Y DPI instead of guessing linked values.");
Console.WriteLine("PASS: captured sensor encodings, reserved fields, independent X/Y packing and invalid inputs.");
