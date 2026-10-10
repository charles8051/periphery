// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using Periphery.Cli;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(CliApp.Configure);

return await app.RunAsync(args);
