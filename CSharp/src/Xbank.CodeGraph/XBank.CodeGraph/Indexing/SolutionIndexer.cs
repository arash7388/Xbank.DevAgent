using System.Security.Cryptography;
using XBank.CodeGraph.Contracts;
using XBank.CodeGraph.Model;
using XBank.CodeGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace XBank.CodeGraph.Indexing;

public sealed class IndexOptions
{
    public bool IncludeExternal { get; init; } = false;
    public int MaxBodyChars { get; init; } = 6000;
    public bool Incremental { get; init; } = false;
}

public sealed class SolutionIndexer(GraphStore store, IndexOptions opt)
{
    private readonly HashSet<string> _seenExternal = [];

    public async Task IndexAsync(string solutionPath)
    {
        var codebaseId = Path.GetFullPath(solutionPath).Replace('\\', '/');
        store.UpsertCodebase(codebaseId, Path.GetFileNameWithoutExtension(solutionPath), solutionPath);
        using var ws = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["Configuration"] = "Debug"
        });

        ws.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                // Shared projects are containers, not standalone compilations. Their
                // C# files are loaded through the projects importing their .projitems.
                // Roslyn reports their unsupported extension as a failure even when
                // every compilable project in the solution loaded successfully.
                bool sharedProjectContainer = System.Text.RegularExpressions.Regex.IsMatch(
                    e.Diagnostic.Message,
                    @"^Cannot open project '[^'\r\n]+\.shproj' because the file extension '\.shproj' is not associated with a language\.$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Console.Error.WriteLine(sharedProjectContainer
                    ? $"[msbuild info] Shared project container skipped: {e.Diagnostic.Message}"
                    : $"[msbuild] {e.Diagnostic.Message}");
            }
        };

        Console.WriteLine($"در حال باز کردن {solutionPath} ...");
        var solution = solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? await ws.OpenSolutionAsync(solutionPath)
            : (await ws.OpenProjectAsync(solutionPath)).Solution;

        var oldHashes = store.GetFileHashes(codebaseId);
        var newHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = new List<string>();

        // مرحله‌ی ۱: تشخیص فایل‌های تغییریافته
        foreach (var project in solution.Projects)
        foreach (var doc in project.Documents)
        {
            if (doc.FilePath is null || IsGenerated(doc.FilePath)) continue;
            var hash = HashFile(doc.FilePath);
            newHashes[doc.FilePath] = hash;
            if (!oldHashes.TryGetValue(doc.FilePath, out var old) || old != hash)
                changed.Add(doc.FilePath);
        }

        var deleted = oldHashes.Keys
            .Where(path => !newHashes.ContainsKey(path))
            .ToList();

        if (opt.Incremental)
        {
            Console.WriteLine($"{changed.Count} فایل تغییر کرده و {deleted.Count} فایل حذف شده است.");
            if (changed.Count == 0 && deleted.Count == 0) return;
        }

        var target = opt.Incremental
            ? new HashSet<string>(changed, StringComparer.OrdinalIgnoreCase)
            : null;

        // مرحله‌ی ۲: استخراج
        store.BeginBatch(codebaseId: codebaseId);
        int fileCount = 0;

        try
        {
            store.PurgeFiles(opt.Incremental ? changed.Concat(deleted) : oldHashes.Keys, codebaseId);

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation is null) continue;

                var di = new DiScanner(store, opt);

                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath is null || IsGenerated(doc.FilePath)) continue;
                    if (target is not null && !target.Contains(doc.FilePath)) continue;

                    var tree = await doc.GetSyntaxTreeAsync();
                    if (tree is null) continue;
                    var model = compilation.GetSemanticModel(tree);
                    var root = await tree.GetRootAsync();

                    ExtractDeclarations(model, root, doc.FilePath, codebaseId);
                    ExtractRelations(model, root, doc.FilePath);
                    di.Scan(model, root, doc.FilePath);

                    if (++fileCount % 100 == 0) Console.WriteLine($"  {fileCount} فایل ...");
                }
            }

            Console.WriteLine("Finalizing graph: rebuilding edges, node degrees and search index...");
            store.Finalize_();
            store.CommitBatch();
        }
        catch
        {
            store.AbortBatch();
            throw;
        }

        store.SetFileHashes(codebaseId, newHashes);

        var (n, e) = store.Stats();
        Console.WriteLine($"تمام شد. گره: {n}  یال: {e}");
    }

    // ---------- اعلان‌ها (گره‌ها) ----------

    private void ExtractDeclarations(SemanticModel model, SyntaxNode root, string file, string codebaseId)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                {
                    var sym = model.GetDeclaredSymbol(node);
                    if (sym is null) break;
                    AddDeclared(sym, file, node, includeBody: false, codebaseId);

                    // CONTAINS: نوع -> اعضا
                    if (sym is INamedTypeSymbol t)
                        foreach (var m in t.GetMembers())
                            if (!m.IsImplicitlyDeclared && Id(m) is { } mid)
                                store.UpsertEdge(Id(sym)!, mid, EdgeKind.Contains, file, codebaseId);
                    break;
                }

                case MethodDeclarationSyntax or ConstructorDeclarationSyntax
                     or LocalFunctionStatementSyntax or PropertyDeclarationSyntax
                     or EventDeclarationSyntax or IndexerDeclarationSyntax:
                {
                    var sym = model.GetDeclaredSymbol(node);
                    if (sym is not null) AddDeclared(sym, file, node, includeBody: true, codebaseId);
                    break;
                }

                case FieldDeclarationSyntax f:
                {
                    foreach (var v in f.Declaration.Variables)
                    {
                        var sym = model.GetDeclaredSymbol(v);
                        if (sym is not null) AddDeclared(sym, file, v, includeBody: false, codebaseId);
                    }
                    break;
                }
            }
        }
    }

    private void AddDeclared(ISymbol sym, string file, SyntaxNode node, bool includeBody, string codebaseId)
    {
        var id = Id(sym);
        if (id is null) return;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string? body = null;
        if (includeBody)
        {
            var text = node.ToFullString().Trim();
            body = text.Length > opt.MaxBodyChars
                ? text[..opt.MaxBodyChars] + "\n/* ... truncated ... */"
                : text;
        }

        store.UpsertNode(new NodeRow(
            Id: id,
            Kind: KindOf(sym),
            Name: sym.Name,
            FullName: sym.ToDisplayString(),
            File: file,
            Line: line,
            Signature: Signature(sym),
            Body: body,
            IsExternal: false,
            Degree: 0), codebaseId);

        // وراثت و پیاده‌سازی
        if (sym is INamedTypeSymbol type)
        {
            if (type.BaseType is { SpecialType: SpecialType.None } bt)
                Link(id, bt, EdgeKind.Inherits, file);
            foreach (var iface in type.Interfaces)
                Link(id, iface, EdgeKind.Implements, file);

            // MediatR: Handler -> Request
            foreach (var i in type.AllInterfaces)
            {
                if (i.Name is "IRequestHandler" or "INotificationHandler"
                    && i.TypeArguments.Length > 0)
                    Link(id, i.TypeArguments[0], EdgeKind.Handles, file);
            }
        }

        if (sym is IMethodSymbol m)
        {
            if (m.OverriddenMethod is { } ov) Link(id, ov, EdgeKind.Overrides, file);
            foreach (var impl in m.ExplicitInterfaceImplementations)
                Link(id, impl, EdgeKind.Implements, file);

            if (m.ReturnType is { SpecialType: SpecialType.None }) Link(id, m.ReturnType, EdgeKind.UsesType, file);
            foreach (var p in m.Parameters)
                if (p.Type.SpecialType == SpecialType.None) Link(id, p.Type, EdgeKind.UsesType, file);

            // ASP.NET Core: گره‌ی Endpoint
            AddEndpointIfAny(m, id, file, codebaseId);
        }

        if (sym is IPropertySymbol p2 && p2.Type.SpecialType == SpecialType.None)
            Link(id, p2.Type, EdgeKind.UsesType, file);
        if (sym is IFieldSymbol f2 && f2.Type.SpecialType == SpecialType.None)
            Link(id, f2.Type, EdgeKind.UsesType, file);
    }

    private void AddEndpointIfAny(IMethodSymbol m, string methodId, string file, string codebaseId)
    {
        foreach (var attr in m.GetAttributes())
        {
            var an = attr.AttributeClass?.Name ?? "";
            var verb = an switch
            {
                "HttpGetAttribute"    => "GET",
                "HttpPostAttribute"   => "POST",
                "HttpPutAttribute"    => "PUT",
                "HttpDeleteAttribute" => "DELETE",
                "HttpPatchAttribute"  => "PATCH",
                _ => null
            };
            if (verb is null) continue;

            var route = attr.ConstructorArguments.FirstOrDefault().Value as string ?? "";
            var ctrlRoute = m.ContainingType.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "RouteAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value as string ?? "";
            var full = $"{verb} /{ctrlRoute.Trim('/')}/{route.Trim('/')}".Replace("//", "/");
            var epId = $"E:{methodId}:{full}";

            store.UpsertNode(new NodeRow(epId, NodeKind.Endpoint, full, full,
                file, 0, full, null, false, 0), codebaseId);
            store.UpsertEdge(epId, methodId, EdgeKind.EndpointOf, file, codebaseId);
        }
    }

    // ---------- روابط داخل بدنه (یال‌ها) ----------

    private void ExtractRelations(SemanticModel model, SyntaxNode root, string file)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax inv:
                {
                    var info = model.GetSymbolInfo(inv);
                    var callee = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (callee is IMethodSymbol) EmitFromOwner(model, node, callee, EdgeKind.Calls, file);
                    break;
                }
                case BaseObjectCreationExpressionSyntax oc:
                {
                    if (model.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor)
                    {
                        EmitFromOwner(model, node, ctor, EdgeKind.Instantiates, file);
                        EmitFromOwner(model, node, ctor.ContainingType, EdgeKind.UsesType, file);
                    }
                    break;
                }
                case MemberAccessExpressionSyntax ma:
                {
                    var s = model.GetSymbolInfo(ma).Symbol;
                    if (s is IPropertySymbol or IFieldSymbol)
                    {
                        var kind = ma.Parent is AssignmentExpressionSyntax assignment && assignment.Left == ma
                            ? EdgeKind.Writes
                            : EdgeKind.Reads;
                        EmitFromOwner(model, node, s, kind, file);
                    }
                    break;
                }
            }
        }
    }

    private void EmitFromOwner(SemanticModel model, SyntaxNode at, ISymbol target, string kind, string file)
    {
        var owner = NormalizeOwner(model.GetEnclosingSymbol(at.SpanStart));
        var ownerId = owner is null ? null : Id(owner);
        if (ownerId is null) return;
        Link(ownerId, target, kind, file);
    }

    private void Link(string srcId, ISymbol rawTarget, string kind, string file)
    {
        var target = Canonical(rawTarget);
        var tid = Id(target);
        if (tid is null) return;

        var inSource = target.Locations.Any(l => l.IsInSource);
        if (!inSource)
        {
            if (!opt.IncludeExternal) return;
            if (_seenExternal.Add(tid))
                store.UpsertNode(new NodeRow(tid, KindOf(target), target.Name,
                    target.ToDisplayString(), null, 0, Signature(target), null, true, 0));
        }

        store.UpsertEdge(srcId, tid, kind, file);
    }

    // ---------- کمکی‌ها ----------

    public static string? Id(ISymbol s)
    {
        var symbol = Canonical(s);
        var documentationId = symbol.GetDocumentationCommentId();
        var assembly = symbol.ContainingAssembly?.Identity.Name;
        return documentationId is null || string.IsNullOrWhiteSpace(assembly)
            ? documentationId
            : $"{assembly}|{documentationId}";
    }

    public static ISymbol Canonical(ISymbol s)
    {
        if (s is IMethodSymbol m) s = m.ReducedFrom ?? m;
        return s.OriginalDefinition;
    }

    /// <summary>نزدیک‌ترین نماد بالادستی که ارزش گره شدن دارد.</summary>
    public static ISymbol? NormalizeOwner(ISymbol? s)
    {
        while (s is not null)
        {
            if (s is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction })
            {
                s = s.ContainingSymbol;
                continue;
            }
            if (s is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamedTypeSymbol)
                return s;
            s = s.ContainingSymbol;
        }
        return null;
    }

    private static string KindOf(ISymbol s) => s switch
    {
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Interface => NodeKind.Interface,
            TypeKind.Struct    => NodeKind.Struct,
            TypeKind.Enum      => NodeKind.Enum,
            _ => t.IsRecord ? NodeKind.Record : NodeKind.Class
        },
        IMethodSymbol { MethodKind: MethodKind.Constructor } => NodeKind.Constructor,
        IMethodSymbol   => NodeKind.Method,
        IPropertySymbol => NodeKind.Property,
        IFieldSymbol    => NodeKind.Field,
        IEventSymbol    => NodeKind.Event,
        _ => "Other"
    };

    private static readonly SymbolDisplayFormat SigFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
                     | SymbolDisplayMemberOptions.IncludeType
                     | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                        | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string Signature(ISymbol s) => s.ToDisplayString(SigFormat);

    public static bool IsGenerated(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}")
            || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
