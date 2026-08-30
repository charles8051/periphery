using BenchmarkDotNet.Running;

// `dotnet run -c Release` runs all benchmarks in this assembly.
// `dotnet run -c Release -- --filter "*PoolBenchmark*"` runs a subset.
// `dotnet run -c Release -- --list flat` lists everything.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
