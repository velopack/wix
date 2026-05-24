namespace wixc;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WixToolset.Core;
using WixToolset.Core.WindowsInstaller;
using WixToolset.Data;
using WixToolset.Extensibility;
using WixToolset.Extensibility.Data;
using WixToolset.Extensibility.Services;

internal sealed class BuildOptions
{
    public required IReadOnlyList<string> SourceFiles { get; init; }
    public required string OutputPath { get; init; }
    public Platform Platform { get; init; } = Platform.X86;
    public string? OutputType { get; init; }
    public PdbType PdbType { get; init; } = PdbType.Full;
}

internal sealed class BuildRunner
{
    private readonly IWixToolsetCoreServiceProvider serviceProvider;
    private readonly IMessaging messaging;
    private readonly IExtensionManager extensionManager;
    private readonly string intermediateFolder;

    public BuildRunner()
    {
        this.serviceProvider = WixToolsetServiceProviderFactory
            .CreateServiceProvider()
            .AddWindowsInstallerBackend();

        this.messaging = this.serviceProvider.GetService<IMessaging>();
        this.messaging.SetListener(new MessageListener());

        this.extensionManager = this.serviceProvider.GetService<IExtensionManager>();
        this.intermediateFolder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    }

    public Task<int> RunAsync(BuildOptions options, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(this.intermediateFolder);

            var creator = this.serviceProvider.GetService<ISymbolDefinitionCreator>();
            var pdbPath = options.PdbType == PdbType.None
                ? null
                : Path.ChangeExtension(options.OutputPath, ".wixpdb");

            var intermediates = this.CompilePhase(options, cancellationToken);
            if (this.messaging.EncounteredError)
            {
                return Task.FromResult(this.messaging.LastErrorNumber);
            }

            this.OptimizePhase(intermediates, options.Platform, cancellationToken);
            if (this.messaging.EncounteredError)
            {
                return Task.FromResult(this.messaging.LastErrorNumber);
            }

            var linked = this.LinkPhase(intermediates, options, creator, cancellationToken);
            if (this.messaging.EncounteredError || linked == null)
            {
                return Task.FromResult(this.messaging.LastErrorNumber);
            }

            using (new IntermediateFieldContext("wix.bind"))
            {
                this.BindPhase(linked, options, pdbPath, cancellationToken);
            }

            return Task.FromResult(this.messaging.LastErrorNumber);
        }
        finally
        {
            TryDeleteDirectory(this.intermediateFolder);
        }
    }

    private IReadOnlyList<Intermediate> CompilePhase(BuildOptions options, CancellationToken cancellationToken)
    {
        var includePaths = Array.Empty<string>();
        var preprocessorVariables = new Dictionary<string, string>();
        var intermediates = new List<Intermediate>();

        foreach (var sourceFile in options.SourceFiles)
        {
            var document = this.Preprocess(preprocessorVariables, sourceFile, includePaths, options, cancellationToken);
            if (document == null)
            {
                continue;
            }

            var context = this.serviceProvider.GetService<ICompileContext>();
            context.Extensions = this.extensionManager.GetServices<ICompilerExtension>();
            context.IntermediateFolder = this.intermediateFolder;
            context.OutputPath = options.OutputPath;
            context.Platform = options.Platform;
            context.Source = document;
            context.CancellationToken = cancellationToken;

            try
            {
                var compiler = this.serviceProvider.GetService<ICompiler>();
                var intermediate = compiler.Compile(context);
                if (intermediate != null && !this.messaging.EncounteredError)
                {
                    intermediates.Add(intermediate);
                }
            }
            catch (WixException e)
            {
                this.messaging.Write(e.Error);
            }
        }

        return intermediates;
    }

    private XDocument? Preprocess(IDictionary<string, string> preprocessorVariables, string sourcePath, IReadOnlyCollection<string> includeSearchPaths, BuildOptions options, CancellationToken cancellationToken)
    {
        var context = this.serviceProvider.GetService<IPreprocessContext>();
        context.Extensions = this.extensionManager.GetServices<IPreprocessorExtension>();
        context.Platform = options.Platform;
        context.IncludeSearchPaths = includeSearchPaths;
        context.IntermediateFolder = this.intermediateFolder;
        context.OutputPath = options.OutputPath;
        context.SourcePath = sourcePath;
        context.Variables = preprocessorVariables;
        context.CancellationToken = cancellationToken;

        try
        {
            var preprocessor = this.serviceProvider.GetService<IPreprocessor>();
            return preprocessor.Preprocess(context)?.Document;
        }
        catch (WixException e)
        {
            this.messaging.Write(e.Error);
            return null;
        }
    }

    private void OptimizePhase(IReadOnlyCollection<Intermediate> intermediates, Platform platform, CancellationToken cancellationToken)
    {
        var context = this.serviceProvider.GetService<IOptimizeContext>();
        context.Extensions = this.extensionManager.GetServices<IOptimizerExtension>();
        context.IntermediateFolder = this.intermediateFolder;
        context.BindPaths = Array.Empty<IBindPath>();
        context.BindVariables = new Dictionary<string, string>();
        context.Platform = platform;
        context.Intermediates = intermediates;
        context.Localizations = Array.Empty<Localization>();
        context.CancellationToken = cancellationToken;

        this.serviceProvider.GetService<IOptimizer>().Optimize(context);
    }

    private Intermediate? LinkPhase(IReadOnlyCollection<Intermediate> intermediates, BuildOptions options, ISymbolDefinitionCreator creator, CancellationToken cancellationToken)
    {
        var context = this.serviceProvider.GetService<ILinkContext>();
        context.Extensions = this.extensionManager.GetServices<ILinkerExtension>();
        context.ExtensionData = this.extensionManager.GetServices<IExtensionData>();
        context.ExpectedOutputType = ParseOutputType(options.OutputType, options.OutputPath);
        context.IntermediateFolder = this.intermediateFolder;
        context.Intermediates = intermediates;
        context.OutputPath = options.OutputPath;
        context.Platform = options.Platform;
        context.SkipStdWixlib = false;
        context.SymbolDefinitionCreator = creator;
        context.CancellationToken = cancellationToken;

        return this.serviceProvider.GetService<ILinker>().Link(context);
    }

    private void BindPhase(Intermediate linked, BuildOptions options, string? pdbPath, CancellationToken cancellationToken)
    {
        IResolveResult resolveResult;
        {
            var context = this.serviceProvider.GetService<IResolveContext>();
            context.BindPaths = Array.Empty<IBindPath>();
            context.BindVariables = new Dictionary<string, string>();
            context.Extensions = this.extensionManager.GetServices<IResolverExtension>();
            context.ExtensionData = this.extensionManager.GetServices<IExtensionData>();
            context.FilterCultures = Array.Empty<string>();
            context.IntermediateFolder = this.intermediateFolder;
            context.IntermediateRepresentation = linked;
            context.Localizations = Array.Empty<Localization>();
            context.OutputPath = options.OutputPath;
            context.CancellationToken = cancellationToken;

            resolveResult = this.serviceProvider.GetService<IResolver>().Resolve(context);
        }

        if (this.messaging.EncounteredError)
        {
            return;
        }

        IBindResult? bindResult = null;
        try
        {
            var context = this.serviceProvider.GetService<IBindContext>();
            context.BackwardCompatibleGuidGeneration = false;
            context.BindPaths = Array.Empty<IBindPath>();
            context.CabbingThreadCount = 0;
            context.CabCachePath = null;
            context.ResolvedCodepage = resolveResult.Codepage;
            context.ResolvedSummaryInformationCodepage = resolveResult.SummaryInformationCodepage;
            context.ResolvedLcid = resolveResult.PackageLcid;
            context.DefaultCompressionLevel = null;
            context.DelayedFields = resolveResult.DelayedFields;
            context.ExpectedEmbeddedFiles = resolveResult.ExpectedEmbeddedFiles;
            context.Extensions = this.extensionManager.GetServices<IBinderExtension>();
            context.FileSystemExtensions = this.extensionManager.GetServices<IFileSystemExtension>();
            context.IntermediateFolder = this.intermediateFolder;
            context.IntermediateRepresentation = resolveResult.IntermediateRepresentation;
            context.OutputPath = options.OutputPath;
            context.OutputType = string.IsNullOrEmpty(options.OutputType)
                ? context.IntermediateRepresentation.Sections.First().Type.ToString()
                : options.OutputType;
            context.PdbType = options.PdbType;
            context.PdbPath = pdbPath;
            context.CancellationToken = cancellationToken;

            bindResult = this.serviceProvider.GetService<IBinder>().Bind(context);
            if (this.messaging.EncounteredError)
            {
                return;
            }

            this.LayoutFiles(bindResult, options.OutputPath, cancellationToken);
        }
        finally
        {
            bindResult?.Dispose();
        }
    }

    private void LayoutFiles(IBindResult bindResult, string outputPath, CancellationToken cancellationToken)
    {
        var context = this.serviceProvider.GetService<ILayoutContext>();
        context.Extensions = this.extensionManager.GetServices<ILayoutExtension>();
        context.TrackedFiles = bindResult.TrackedFiles;
        context.FileTransfers = bindResult.FileTransfers;
        context.IntermediateFolder = this.intermediateFolder;
        context.OutputPath = outputPath;
        context.TrackingFile = null;
        context.ResetAcls = false;
        context.CancellationToken = cancellationToken;

        this.serviceProvider.GetService<ILayoutCreator>().Layout(context);
    }

    private static OutputType ParseOutputType(string? outputType, string outputPath)
    {
        var key = string.IsNullOrEmpty(outputType) ? Path.GetExtension(outputPath) : outputType;
        return key?.ToLowerInvariant() switch
        {
            "bundle" or ".exe" => OutputType.Bundle,
            "library" or ".wixlib" => OutputType.Library,
            "module" or ".msm" => OutputType.Module,
            "patch" or ".msp" => OutputType.Patch,
            "product" or "package" or ".msi" => OutputType.Package,
            "transform" or ".mst" => OutputType.Transform,
            "intermediatepostlink" or ".wixipl" => OutputType.IntermediatePostLink,
            _ => OutputType.Package,
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
