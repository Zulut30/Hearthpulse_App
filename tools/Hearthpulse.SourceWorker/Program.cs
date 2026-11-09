using Hearthpulse.Core.SourceCapture.Processing;

try
{
    return await SourceWorkerCommand.RunAsync(args, Console.Out);
}
catch (Exception)
{
    // Even a closed stdout must not cause the runtime to print paths or exception details.
    return 1;
}
