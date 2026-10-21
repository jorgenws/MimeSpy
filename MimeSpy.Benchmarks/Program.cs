using BenchmarkDotNet.Running;
using MimeSpy.Benchmarks;

if (args.Contains("accuracy"))
{
    AccuracyReport.Run();
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
