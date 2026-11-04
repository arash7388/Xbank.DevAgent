using XBank.CodeGraph.Model;
using XBank.CodeGraph.Contracts;
using XBank.CodeGraph.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XBank.CodeGraph.Indexing;

/// <summary>ثبت‌های DI را به یال RESOLVES_TO تبدیل می‌کند.</summary>
public sealed class DiScanner(GraphStore store, IndexOptions opt)
{
    private static readonly HashSet<string> Methods =
    [
        "AddScoped", "AddTransient", "AddSingleton",
        "TryAddScoped", "TryAddTransient", "TryAddSingleton",
        "TryAddEnumerable", "AddHostedService", "AddDbContext",
        "AddDbContextFactory", "AddHttpClient"
    ];

    public void Scan(SemanticModel model, SyntaxNode root, string file)
    {
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name,
                GenericNameSyntax g             => (SimpleNameSyntax)g,
                IdentifierNameSyntax i          => i,
                _ => null
            };
            if (name is null || !Methods.Contains(name.Identifier.ValueText)) continue;

            var typeArgs = (name as GenericNameSyntax)?.TypeArgumentList.Arguments
                           ?? default;

            var types = new List<ITypeSymbol>();

            foreach (var ta in typeArgs)
                if (model.GetTypeInfo(ta).Type is { } t) types.Add(t);

            // حالت غیرجنریک: AddScoped(typeof(IFoo), typeof(Foo))
            if (types.Count == 0)
            {
                foreach (var arg in inv.ArgumentList.Arguments)
                    if (arg.Expression is TypeOfExpressionSyntax to
                        && model.GetTypeInfo(to.Type).Type is { } t)
                        types.Add(t);
            }

            if (types.Count >= 2)
            {
                Emit(types[0], types[1], file);
            }
            else if (types.Count == 1)
            {
                // AddScoped<IFoo>(sp => new Foo(...))  یا  AddScoped<Foo>()
                var impl = FindFactoryImpl(model, inv) ?? types[0];
                Emit(types[0], impl, file);
            }
        }
    }

    private static ITypeSymbol? FindFactoryImpl(SemanticModel model, InvocationExpressionSyntax inv)
    {
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda) continue;
            var oc = lambda.DescendantNodesAndSelf()
                           .OfType<BaseObjectCreationExpressionSyntax>()
                           .FirstOrDefault();
            if (oc is not null && model.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor)
                return ctor.ContainingType;
        }
        return null;
    }

    private void Emit(ITypeSymbol service, ITypeSymbol impl, string file)
    {
        var sid = SolutionIndexer.Id(service);
        var iid = SolutionIndexer.Id(impl);
        if (sid is null || iid is null || sid == iid) return;

        // فقط اگر پیاده‌سازی در سورس ما باشد ارزش دارد
        if (!impl.Locations.Any(l => l.IsInSource) && !opt.IncludeExternal) return;

        store.UpsertEdge(sid, iid, EdgeKind.ResolvesTo, file);
    }
}
