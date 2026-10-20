using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Dvmp.ScenariosAnalyzer;

/// <summary>
/// A scenario's orchestrator runs in DvmpScenarios.exe, outside the games (docs/design/scenarios.md),
/// where game code can't run: Unity's engine calls throw, and game state isn't there. In the methods
/// of a class deriving from Scenario&lt;&gt; (not its nested types: those hold the in-game steps and
/// the data they return), this fails the build:
/// <list type="bullet">
/// <item>DVMP0101: using a game type or member (UnityEngine, Assembly-CSharp, DV.*, Multiplayer, the
/// dev agent, the probe library Dvmp.Lib). Inside <c>() =&gt; Step(...)</c> passed to Run/Start, the
/// step itself is the one thing allowed: its arguments are evaluated here too.</item>
/// <item>DVMP0102: calling a [Step] method directly, which would run it here, not in a game.</item>
/// </list>
/// And anywhere in the scenarios, in-game code too:
/// <list type="bullet">
/// <item>DVMP0103: the game's NetworkClient, ClientPlayerManager or NetworkLifecycle.Client. Stage 3 of
/// docs/design/host-local-session.md removes the host's client, so a step reaches the player at this
/// keyboard through NetworkLifecycle.LocalSession (ILocalPlayerSession), and runs unchanged on both.</item>
/// </list>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ScenarioAnalyzer : DiagnosticAnalyzer
{
    public const string GameCodeId = "DVMP0101";
    public const string DirectStepId = "DVMP0102";

    private static readonly DiagnosticDescriptor GameCode = new(
        GameCodeId,
        "Game code in a scenario's orchestrator",
        "{0} is game code, and a scenario's Run executes outside the games: move it into a [Step] and call that through pair.Host/Client/Both. See docs/design/scenarios.md.",
        "Scenarios", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DirectStep = new(
        DirectStepId,
        "A [Step] called directly",
        "{0} is a [Step]: call it through pair.Host/Client/Both.Run(() => {0}(...)), or it runs outside the games. See docs/design/scenarios.md.",
        "Scenarios", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // Assemblies orchestrator code may use; everything else is the game's, the mod's or the agent's.
    private static readonly string[] Allowed = { "mscorlib", "netstandard", "System", "Newtonsoft.Json", "Mono.Cecil" };

    public const string HostClientId = "DVMP0103";

    private static readonly DiagnosticDescriptor HostClient = new(
        HostClientId,
        "The host's NetworkClient, which stage 3 removes",
        "{0}: reach the local player through NetworkLifecycle.Instance.LocalSession (ILocalPlayerSession), not the game's client: stage 3 removes the host's NetworkClient (docs/design/host-local-session.md).",
        "Scenarios", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly string[] ClientTypes = { "Multiplayer.Client.Session.NetworkClient", "Multiplayer.Client.Players.ClientPlayerManager" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(GameCode, DirectStep, HostClient);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Check,
            OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.FieldReference,
            OperationKind.ObjectCreation, OperationKind.VariableDeclarator, OperationKind.MethodReference);
    }

    private static void Check(OperationAnalysisContext context)
    {
        IOperation op = context.Operation;
        if (NamesHostClient(op) is string what)
            context.ReportDiagnostic(Diagnostic.Create(HostClient, op.Syntax.GetLocation(), what));
        if (!IsOrchestrator(context.ContainingSymbol)) return;

        if (op is IInvocationOperation call && IsStep(call.TargetMethod))
        {
            if (!IsStepCallBody(call, context))
                context.ReportDiagnostic(Diagnostic.Create(DirectStep, op.Syntax.GetLocation(), Name(call.TargetMethod)));
            return;   // the step's arguments are checked as operations of their own
        }

        ISymbol? used = op switch
        {
            IInvocationOperation i => i.TargetMethod,
            IPropertyReferenceOperation p => p.Property,
            IFieldReferenceOperation f => f.Field,
            IObjectCreationOperation o => o.Type,
            IVariableDeclaratorOperation v => v.Symbol.Type,
            IMethodReferenceOperation m => m.Method,
            _ => null
        };
        if (used != null && IsGame(used, context.Compilation.Assembly))
            context.ReportDiagnostic(Diagnostic.Create(GameCode, op.Syntax.GetLocation(), Name(used)));
    }

    // A method (or a lambda or local function in one) declared in a class deriving from Scenario<>,
    // not a [Step].
    private static bool IsOrchestrator(ISymbol symbol)
    {
        ISymbol s = symbol;
        while (s is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } && s.ContainingSymbol != null)
            s = s.ContainingSymbol;
        if (s is IMethodSymbol method && IsStep(method)) return false;
        for (INamedTypeSymbol? t = s.ContainingType?.BaseType; t != null; t = t.BaseType)
            if (t.Name == "Scenario" && t.IsGenericType && t.ContainingNamespace?.ToDisplayString() == "Dvmp.Scenarios")
                return true;
        return false;
    }

    // NetworkLifecycle.Client, or a member or a local of the client's types.
    private static string? NamesHostClient(IOperation op)
    {
        if (op is IPropertyReferenceOperation { Property: { Name: "Client" } p } && p.ContainingType?.ToDisplayString() == "Multiplayer.Shared.Session.NetworkLifecycle")
            return "NetworkLifecycle.Client";
        ITypeSymbol? type = op switch
        {
            IInvocationOperation i => i.TargetMethod.ContainingType,
            IPropertyReferenceOperation p2 => p2.Property.ContainingType,
            IFieldReferenceOperation f => f.Field.ContainingType,
            IVariableDeclaratorOperation v => v.Symbol.Type,
            IObjectCreationOperation o => o.Type,
            _ => null
        };
        string? name = type?.ToDisplayString();
        return name != null && ClientTypes.Contains(name) ? name.Substring(name.LastIndexOf('.') + 1) : null;
    }

    private static bool IsStep(IMethodSymbol m) => m.GetAttributes().Any(a => a.AttributeClass?.Name == "StepAttribute");

    // The call that is the whole body of a lambda converted to an expression tree: `() => Step(...)`.
    private static bool IsStepCallBody(IInvocationOperation call, OperationAnalysisContext context)
    {
        if (call.Syntax.Parent is not LambdaExpressionSyntax lambda || lambda.Body != call.Syntax) return false;
        ITypeSymbol? converted = call.SemanticModel?.GetTypeInfo(lambda, context.CancellationToken).ConvertedType;
        return converted is INamedTypeSymbol { Name: "Expression", IsGenericType: true } e
               && e.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions";
    }

    private static bool IsGame(ISymbol symbol, IAssemblySymbol own)
    {
        if (symbol is IArrayTypeSymbol array) return IsGame(array.ElementType, own);
        IAssemblySymbol? assembly = symbol.ContainingAssembly;
        if (assembly == null) return false;
        if (SymbolEqualityComparer.Default.Equals(assembly, own))
            return symbol.ContainingNamespace?.ToDisplayString() is "Dvmp.Lib";   // the probe library: in-game helpers
        string name = assembly.Name;
        return !Allowed.Any(a => name == a || name.StartsWith(a + ".", System.StringComparison.Ordinal));
    }

    private static string Name(ISymbol s) => s is IMethodSymbol m ? $"{m.ContainingType?.Name}.{m.Name}" : s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}
