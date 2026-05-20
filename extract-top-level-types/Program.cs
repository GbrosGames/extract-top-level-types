using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Any(static arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var apply = args.Any(static arg => string.Equals(arg, "--apply", StringComparison.OrdinalIgnoreCase));
        var folderArgument = args.FirstOrDefault(static arg => !arg.StartsWith("-", StringComparison.Ordinal));

        if (string.IsNullOrWhiteSpace(folderArgument))
        {
            PrintUsage();
            return 1;
        }

        var folderPath = Path.GetFullPath(folderArgument);

        if (!Directory.Exists(folderPath))
        {
            Console.Error.WriteLine($"Folder not found: {folderPath}");
            return 1;
        }

        var filePaths = Directory
            .EnumerateFiles(folderPath, "*.cs", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var plannedFiles = filePaths
            .Select(filePath => (FilePath: filePath, Plan: CreatePlan(filePath)))
            .ToList();

        var deleteSourcePaths = plannedFiles
            .Where(static item => item.Plan.DeleteSource)
            .Select(static item => item.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var whitespaceOnlySourcePaths = plannedFiles
            .Where(static item => item.Plan.IsWhitespaceOnlySource)
            .Select(static item => item.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outputOwners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var plannedFile in plannedFiles)
        {
            if (!plannedFile.Plan.IsSupported || !plannedFile.Plan.NeedsChange)
            {
                continue;
            }

            foreach (var output in plannedFile.Plan.Outputs)
            {
                if (!outputOwners.TryGetValue(output.Path, out var owners))
                {
                    owners = new List<string>();
                    outputOwners.Add(output.Path, owners);
                }

                owners.Add(plannedFile.FilePath);
            }
        }

        var invalidPlanReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputOwner in outputOwners.Where(static pair => pair.Value.Count > 1))
        {
            var reason = $"batch output collision: {GetDisplayPath(outputOwner.Key)}";

            foreach (var owner in outputOwner.Value)
            {
                invalidPlanReasons[owner] = reason;
            }
        }

        foreach (var plannedFile in plannedFiles)
        {
            if (!plannedFile.Plan.IsSupported || !plannedFile.Plan.NeedsChange || invalidPlanReasons.ContainsKey(plannedFile.FilePath))
            {
                continue;
            }

            foreach (var output in plannedFile.Plan.Outputs)
            {
                if (!File.Exists(output.Path))
                {
                    continue;
                }

                if (string.Equals(output.Path, plannedFile.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (deleteSourcePaths.Contains(output.Path))
                {
                    continue;
                }

                if (whitespaceOnlySourcePaths.Contains(output.Path))
                {
                    continue;
                }

                invalidPlanReasons[plannedFile.FilePath] = $"output file already exists: {GetDisplayPath(output.Path)}";
                break;
            }
        }

        var inspectedFiles = plannedFiles.Count;
        var changedFiles = 0;
        var skippedFiles = 0;
        var unchangedFiles = 0;
        var executablePlans = new List<(string FilePath, (bool IsSupported, bool NeedsChange, bool DeleteSource, string Message, List<(string Path, string Content)> Outputs, Encoding? Encoding, bool IsWhitespaceOnlySource) Plan)>();

        foreach (var plannedFile in plannedFiles)
        {
            var filePath = plannedFile.FilePath;
            var plan = plannedFile.Plan;

            if (!plan.IsSupported)
            {
                skippedFiles++;
                Console.WriteLine($"SKIP {GetDisplayPath(filePath)} :: {plan.Message}");
                continue;
            }

            if (invalidPlanReasons.TryGetValue(filePath, out var invalidReason))
            {
                skippedFiles++;
                Console.WriteLine($"SKIP {GetDisplayPath(filePath)} :: {invalidReason}");
                continue;
            }

            if (!plan.NeedsChange)
            {
                unchangedFiles++;
                continue;
            }

            changedFiles++;
            executablePlans.Add(plannedFile);

            var outputTargets = string.Join(
                ", ",
                plan.Outputs.Select(static output => GetDisplayPath(output.Path)));

            var deleteSuffix = plan.DeleteSource ? " ; delete source" : string.Empty;
            Console.WriteLine($"PLAN {GetDisplayPath(filePath)} :: {outputTargets}{deleteSuffix}");
        }

        if (apply)
        {
            ApplyPlans(executablePlans);
        }

        Console.WriteLine(
            $"SUMMARY inspected={inspectedFiles} changed={changedFiles} unchanged={unchangedFiles} skipped={skippedFiles} mode={(apply ? "apply" : "dry-run")}");

        return 0;
    }

    private static (bool IsSupported, bool NeedsChange, bool DeleteSource, string Message, List<(string Path, string Content)> Outputs, Encoding? Encoding, bool IsWhitespaceOnlySource) CreatePlan(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var sourceText = SourceText.From(stream, encoding: null);
        var sourceContent = sourceText.ToString();
        var isWhitespaceOnlySource = string.IsNullOrWhiteSpace(sourceContent);
        var newLine = DetectNewLine(sourceContent);
        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview, documentationMode: DocumentationMode.Parse);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, filePath);
        var root = syntaxTree.GetCompilationUnitRoot();

        if (syntaxTree.GetDiagnostics().Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return (false, false, false, "parse errors", new List<(string Path, string Content)>(), sourceText.Encoding, isWhitespaceOnlySource);
        }

        if (root.AttributeLists.Count > 0)
        {
            return (false, false, false, "assembly or module attributes", new List<(string Path, string Content)>(), sourceText.Encoding, isWhitespaceOnlySource);
        }

        var collectedDeclarations = new List<(MemberDeclarationSyntax Declaration, string GroupKey, string BaseName)>();

        if (!TryCollectTopLevelDeclarations(root.Members, string.Empty, collectedDeclarations, out var reason))
        {
            return (false, false, false, reason, new List<(string Path, string Content)>(), sourceText.Encoding, isWhitespaceOnlySource);
        }

        if (collectedDeclarations.Count <= 1)
        {
            return (true, false, false, "single top-level declaration", new List<(string Path, string Content)>(), sourceText.Encoding, isWhitespaceOnlySource);
        }

        var groups = collectedDeclarations
            .GroupBy(static declaration => declaration.GroupKey, StringComparer.Ordinal)
            .Select(static group => (
                GroupKey: group.Key,
                BaseName: group.First().BaseName,
                Declarations: group.Select(static item => item.Declaration).ToList()))
            .ToList();

        if (groups.Count <= 1)
        {
            return (true, false, false, "single output group", new List<(string Path, string Content)>(), sourceText.Encoding, isWhitespaceOnlySource);
        }

        var baseNameCollisions = groups
            .GroupBy(static group => group.BaseName, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (baseNameCollisions.Count > 0)
        {
            return (
                false,
                false,
                false,
                $"output path collisions: {string.Join(", ", baseNameCollisions)}",
                new List<(string Path, string Content)>(),
                sourceText.Encoding,
                isWhitespaceOnlySource);
        }

        var sourceFileName = Path.GetFileNameWithoutExtension(filePath);
        var sourceDirectory = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
        var groupToKeepInSource = groups.FirstOrDefault(group => string.Equals(group.BaseName, sourceFileName, StringComparison.OrdinalIgnoreCase));
        var keepSourceFile = !string.IsNullOrWhiteSpace(groupToKeepInSource.BaseName);
        var outputs = new List<(string Path, string Content)>();

        foreach (var group in groups)
        {
            var outputPath = keepSourceFile && string.Equals(group.BaseName, groupToKeepInSource.BaseName, StringComparison.OrdinalIgnoreCase)
                ? filePath
                : Path.Combine(sourceDirectory, $"{group.BaseName}.cs");

            var includedDeclarations = new HashSet<SyntaxNode>(group.Declarations);
            var nodesToRemove = collectedDeclarations
                .Select(static declaration => declaration.Declaration)
                .Where(declaration => !includedDeclarations.Contains(declaration))
                .Cast<SyntaxNode>()
                .ToArray();

            var filteredRoot = (CompilationUnitSyntax)root.RemoveNodes(
                nodesToRemove,
                SyntaxRemoveOptions.KeepExteriorTrivia | SyntaxRemoveOptions.KeepUnbalancedDirectives)!;

            filteredRoot = PruneEmptyNamespaces(filteredRoot);
            var outputContent = NormalizeOutputContent(filteredRoot.ToFullString(), newLine);
            outputs.Add((outputPath, outputContent));
        }

        var deleteSource = !keepSourceFile;
        return (true, true, deleteSource, "planned", outputs, sourceText.Encoding, isWhitespaceOnlySource);
    }

    private static void ApplyPlans(IEnumerable<(string FilePath, (bool IsSupported, bool NeedsChange, bool DeleteSource, string Message, List<(string Path, string Content)> Outputs, Encoding? Encoding, bool IsWhitespaceOnlySource) Plan)> executablePlans)
    {
        var outputs = executablePlans
            .SelectMany(static item => item.Plan.Outputs.Select(output => (output.Path, output.Content, Encoding: item.Plan.Encoding)))
            .OrderBy(static output => output.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deletePaths = executablePlans
            .Where(static item => item.Plan.DeleteSource)
            .Select(static item => item.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var deletePath in deletePaths)
        {
            if (!File.Exists(deletePath))
            {
                continue;
            }

            File.Delete(deletePath);
            Console.WriteLine($"DELETE {GetDisplayPath(deletePath)}");
        }

        foreach (var output in outputs)
        {
            var directoryPath = Path.GetDirectoryName(output.Path);
            var targetEncoding = output.Encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.WriteAllText(output.Path, output.Content, targetEncoding);
            Console.WriteLine($"WRITE {GetDisplayPath(output.Path)}");
        }
    }

    private static bool TryCollectTopLevelDeclarations(
        SyntaxList<MemberDeclarationSyntax> members,
        string containerPath,
        List<(MemberDeclarationSyntax Declaration, string GroupKey, string BaseName)> collectedDeclarations,
        out string reason)
    {
        foreach (var member in members)
        {
            if (member is BaseTypeDeclarationSyntax baseTypeDeclaration)
            {
                if (baseTypeDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
                {
                    reason = $"partial declaration: {baseTypeDeclaration.Identifier.ValueText}";
                    return false;
                }

                var baseName = baseTypeDeclaration.Identifier.ValueText;
                collectedDeclarations.Add((baseTypeDeclaration, BuildGroupKey(containerPath, baseName), baseName));
                continue;
            }

            if (member is DelegateDeclarationSyntax delegateDeclaration)
            {
                var baseName = delegateDeclaration.Identifier.ValueText;
                collectedDeclarations.Add((delegateDeclaration, BuildGroupKey(containerPath, baseName), baseName));
                continue;
            }

            if (member is NamespaceDeclarationSyntax namespaceDeclaration)
            {
                var nextContainerPath = BuildContainerPath(containerPath, namespaceDeclaration.Name.ToString());

                if (!TryCollectTopLevelDeclarations(namespaceDeclaration.Members, nextContainerPath, collectedDeclarations, out reason))
                {
                    return false;
                }

                continue;
            }

            if (member is FileScopedNamespaceDeclarationSyntax fileScopedNamespaceDeclaration)
            {
                var nextContainerPath = BuildContainerPath(containerPath, fileScopedNamespaceDeclaration.Name.ToString());

                if (!TryCollectTopLevelDeclarations(fileScopedNamespaceDeclaration.Members, nextContainerPath, collectedDeclarations, out reason))
                {
                    return false;
                }

                continue;
            }

            reason = $"unsupported top-level member: {member.Kind()}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static string BuildContainerPath(string containerPath, string nextSegment)
    {
        return string.IsNullOrWhiteSpace(containerPath) ? nextSegment : $"{containerPath}.{nextSegment}";
    }

    private static string BuildGroupKey(string containerPath, string baseName)
    {
        return $"{containerPath}|{baseName}";
    }

    private static string GetDisplayPath(string path)
    {
        return Path.GetRelativePath(Environment.CurrentDirectory, path);
    }

    private static string DetectNewLine(string sourceContent)
    {
        return sourceContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string NormalizeOutputContent(string content, string newLine)
    {
        var normalizedLineEndings = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalizedLineEndings.Split('\n');
        var builder = new StringBuilder(normalizedLineEndings.Length);
        var wroteContent = false;
        var previousWasBlank = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var isBlank = string.IsNullOrWhiteSpace(line);

            if (!wroteContent)
            {
                if (isBlank)
                {
                    continue;
                }

                builder.Append(line);
                wroteContent = true;
                previousWasBlank = false;
                continue;
            }

            if (isBlank)
            {
                if (previousWasBlank)
                {
                    continue;
                }

                builder.Append(newLine);
                previousWasBlank = true;
                continue;
            }

            builder.Append(newLine);
            builder.Append(line);
            previousWasBlank = false;
        }

        while (builder.Length >= newLine.Length)
        {
            var trailingSegment = builder.ToString(builder.Length - newLine.Length, newLine.Length);

            if (!string.Equals(trailingSegment, newLine, StringComparison.Ordinal))
            {
                break;
            }

            builder.Length -= newLine.Length;
        }

        if (builder.Length == 0)
        {
            return string.Empty;
        }

        builder.Append(newLine);
        return builder.ToString();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project .scripts/extract-top-level-types/TopLevelTypeExtractor.csproj -- <folder> [--apply]");
    }

    private static CompilationUnitSyntax PruneEmptyNamespaces(CompilationUnitSyntax root)
    {
        return (CompilationUnitSyntax)new EmptyNamespacePruner().Visit(root)!;
    }

    private sealed class EmptyNamespacePruner : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visitedNode = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
            return visitedNode.Members.Count == 0 ? null : visitedNode;
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visitedNode = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            return visitedNode.Members.Count == 0 ? null : visitedNode;
        }
    }
}
