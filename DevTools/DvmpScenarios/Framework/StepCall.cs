using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Dvmp.Scenarios
{
    /// <summary>
    /// A step call read from an expression: <c>() =&gt; Cars.Delete(train.Cars)</c> or
    /// <c>s =&gt; InGame.Watch(s, train.Cars, 60)</c>. The step itself never runs here: its
    /// declaring type, name and parameter types go to the game, with its arguments evaluated here
    /// and sent as JSON.
    /// </summary>
    internal sealed class StepCall
    {
        public MethodInfo Method { get; private set; }
        public object[] Args { get; private set; }
        /// <summary>What the step gives back: Step&lt;T&gt;'s T, the method's return type, or null.</summary>
        public Type ResultType { get; private set; }
        public string Label => $"{Method.DeclaringType.Name}.{Method.Name}";
        public string[] Parameters => Method.GetParameters().Select(p => Signature(p.ParameterType)).ToArray();

        public static StepCall From(LambdaExpression lambda)
        {
            if (lambda.Body is not MethodCallExpression call || !call.Method.IsStatic)
                throw new ScenarioException($"a step call is one call to a static [Step] method, not `{lambda.Body}`");
            MethodInfo method = call.Method;
            if (method.GetCustomAttribute<StepAttribute>() == null)
                throw new ScenarioException($"{method.DeclaringType.Name}.{method.Name} is not a [Step]");

            ParameterInfo[] parameters = method.GetParameters();
            bool hasStep = parameters.Length > 0 && IsStep(parameters[0].ParameterType);
            if (hasStep != (lambda.Parameters.Count == 1) || hasStep && call.Arguments[0] != lambda.Parameters[0])
                throw new ScenarioException(hasStep
                    ? $"{method.Name} takes its Step<> first: call it as s => {method.Name}(s, ...)"
                    : $"{method.Name} takes no Step<>: call it as () => {method.Name}(...)");

            List<object> args = new();
            for (int i = hasStep ? 1 : 0; i < parameters.Length; i++)
            {
                CheckContract(parameters[i].ParameterType, $"{method.Name}'s parameter {parameters[i].Name}");
                Expression arg = call.Arguments[i];
                if (Uses(arg, lambda.Parameters))
                    throw new ScenarioException($"{method.Name}: only the first argument may use the step parameter");
                args.Add(Expression.Lambda<Func<object>>(Expression.Convert(arg, typeof(object))).Compile()());
            }

            Type result = hasStep ? parameters[0].ParameterType.GetGenericArguments()[0]
                : method.ReturnType == typeof(void) || method.ReturnType == typeof(IEnumerator) ? null
                : method.ReturnType;
            if (result != null) CheckContract(result, $"{method.Name}'s result");
            return new StepCall { Method = method, Args = args.ToArray(), ResultType = result };
        }

        /// <summary>Whether a [Step] method can be called at all: plain data in and out, and a
        /// Step&lt;&gt; only on a coroutine. For `DvmpScenarios check`.</summary>
        public static void CheckSignature(MethodInfo method)
        {
            string name = $"{method.DeclaringType.Name}.{method.Name}";
            if (!method.IsStatic) throw new ScenarioException($"{name}: a [Step] is static");
            ParameterInfo[] parameters = method.GetParameters();
            bool hasStep = parameters.Length > 0 && IsStep(parameters[0].ParameterType);
            if (hasStep && method.ReturnType != typeof(IEnumerator))
                throw new ScenarioException($"{name}: a step that takes a Step<> is a coroutine (returns IEnumerator)");
            foreach (ParameterInfo p in parameters.Skip(hasStep ? 1 : 0))
                CheckContract(p.ParameterType, $"{name}'s parameter {p.Name}");
            if (hasStep) CheckContract(parameters[0].ParameterType.GetGenericArguments()[0], $"{name}'s result");
            else if (method.ReturnType != typeof(void) && method.ReturnType != typeof(IEnumerator)) CheckContract(method.ReturnType, $"{name}'s result");
        }

        /// <summary>A type as the agent names it (ScenarioSteps.Signature): no assembly names, so the
        /// renamed copy loaded into a game still matches.</summary>
        public static string Signature(Type t) =>
            t.IsArray ? Signature(t.GetElementType()) + "[]"
            : t.IsGenericType ? t.GetGenericTypeDefinition().FullName + "<" + string.Join(",", t.GetGenericArguments().Select(Signature)) + ">"
            : t.FullName;

        private static bool IsStep(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Step<>);

        /// <summary>
        /// Step arguments and results are plain data: primitives, strings, enums, arrays of them,
        /// and classes of this assembly made of those. Never a game object or a Unity value: the
        /// agent's JSON writes Unity types but can't read them back, and a TrainCar means nothing
        /// in another process.
        /// </summary>
        private static void CheckContract(Type t, string what, HashSet<Type> seen = null)
        {
            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return;
            if (t.IsArray) { CheckContract(t.GetElementType(), what, seen); return; }
            if (t.Assembly != typeof(StepCall).Assembly || IsStep(t))
                throw new ScenarioException($"{what} is a {t.FullName}: steps take and return plain data " +
                                            "(primitives, strings, arrays, and classes of the scenarios project)");
            seen ??= new HashSet<Type>();
            if (!seen.Add(t)) return;
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) CheckContract(f.FieldType, $"{what} ({t.Name}.{f.Name})", seen);
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) CheckContract(p.PropertyType, $"{what} ({t.Name}.{p.Name})", seen);
        }

        private static bool Uses(Expression e, IReadOnlyCollection<ParameterExpression> parameters)
        {
            UsesVisitor v = new(parameters);
            v.Visit(e);
            return v.Found;
        }

        private sealed class UsesVisitor(IReadOnlyCollection<ParameterExpression> parameters) : ExpressionVisitor
        {
            public bool Found;
            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (parameters.Contains(node)) Found = true;
                return node;
            }
        }
    }

    /// <summary>A mistake in a scenario's code, found when it runs a step (a step call that isn't one).</summary>
    public sealed class ScenarioException(string message) : Exception(message);

    /// <summary>The harness couldn't do its part: no games, a stale install, a pair of the wrong
    /// kind. Not a test result: exit 3, an ERROR in gate.sh.</summary>
    public sealed class HarnessException(string message) : Exception(message);
}
