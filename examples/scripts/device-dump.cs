#:property TargetFramework=net10.0
#:package Periphery@1.0.0-alpha.*
#:package Microsoft.Extensions.Logging.Console@9.0.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Periphery;

DeviceCategory category = DeviceCategory.All;
bool verbose = false;

var positional = new List<string>();
foreach (var arg in args)
{
    if (arg is "--verbose" or "-v") verbose = true;
    else positional.Add(arg);
}

if (positional.Count > 0)
{
    if (!Enum.TryParse(positional[0], ignoreCase: true, out category))
    {
        Console.Error.WriteLine($"Unknown category '{positional[0]}'.");
        Console.Error.WriteLine($"Valid values: {string.Join(", ", Enum.GetNames<DeviceCategory>())}");
        return 1;
    }
}

if (verbose)
{
    var loggerFactory = LoggerFactory.Create(b => b
        .AddConsole()
        .SetMinimumLevel(LogLevel.Debug));
    PeripheryLoggerFactory.SetLoggerFactory(loggerFactory);
}

// Status messages go to stderr so they don't contaminate the JSON stream.
var categoryLabel = category == DeviceCategory.All ? "all devices" : $"{category} devices";
Console.Error.Write($"Enumerating {categoryLabel}...");

var query = Devices.Enumerate();
if (category != DeviceCategory.All)
    query = query.OfCategory(category);

var devices = await query.ToListAsync();

Console.Error.WriteLine($" {devices.Count} found. Serializing...");

using var stdout = Console.OpenStandardOutput();
using var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true });
JsonSerializer.Serialize(writer, devices, typeof(List<DeviceInfo>), DeviceInfoJsonContext.Default);
return 0;
