namespace wixc;

using System;
using System.CommandLine;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WixToolset.Data;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("wixc7.exe : error WIXC0001: wixc7 only supports Windows.");
            return 1;
        }

        var archOption = new Option<Platform?>("-arch")
        {
            Description = "Architecture for the output (x86, x64, arm64).",
        };
        var outputTypeOption = new Option<string?>("-outputType")
        {
            Description = "Output type (msi, msm, package, etc.). Default: msi.",
        };
        var pdbTypeOption = new Option<PdbType?>("-pdbType")
        {
            Description = "PDB type: full (default) or none.",
        };
        var outOption = new Option<string?>("-out")
        {
            Description = "Output file path.",
        };
        outOption.Aliases.Add("-o");
        var sourcesArgument = new Argument<string[]>("sources")
        {
            Description = "One or more .wxs source files.",
            Arity = ArgumentArity.OneOrMore,
        };

        var buildCommand = new Command("build", "Build one or more .wxs source files into an .msi.")
        {
            archOption,
            outputTypeOption,
            pdbTypeOption,
            outOption,
            sourcesArgument,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            cts.Cancel();
            e.Cancel = true;
        };

        buildCommand.SetAction(parseResult =>
        {
            var sources = parseResult.GetValue(sourcesArgument) ?? Array.Empty<string>();
            var outputPath = parseResult.GetValue(outOption);
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.ChangeExtension(sources[0], ".msi");
            }

            var options = new BuildOptions
            {
                SourceFiles = sources,
                OutputPath = outputPath!,
                Platform = parseResult.GetValue(archOption) ?? Platform.X86,
                OutputType = parseResult.GetValue(outputTypeOption),
                PdbType = parseResult.GetValue(pdbTypeOption) ?? PdbType.Full,
            };

            try
            {
                var runner = new BuildRunner();
                return runner.RunAsync(options, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return -1;
            }
            catch (WixException e)
            {
                Console.Error.WriteLine($"wixc7.exe : error WIXC0003: {e.Message}");
                return e.Error?.Id ?? e.HResult;
            }
        });

        var root = new RootCommand("wixc7 - minimal WiX wrapper. Builds .wxs sources into an .msi.")
        {
            buildCommand,
        };

        var invocationConfig = new InvocationConfiguration();
        return await root.Parse(args).InvokeAsync(invocationConfig, cts.Token);
    }
}
