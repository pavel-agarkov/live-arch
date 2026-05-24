using System.Linq.Expressions;

namespace LiveArch.Deployment
{
    internal static class PropagationExpressionHelper
    {
        public static string? GetSourcePath(Expression<Func<object, object>> expression)
        {
            var body = StripConversion(expression.Body);
            if (TryGetMemberPath(body, expression.Parameters[0], out var path))
            {
                return path;
            }

            if (body is MethodCallExpression methodCall &&
                string.Equals(methodCall.Method.Name, "Apply", StringComparison.Ordinal) &&
                TryGetApplySourcePath(methodCall, expression.Parameters[0], out path))
            {
                return path;
            }

            return null;
        }

        private static bool TryGetApplySourcePath(MethodCallExpression methodCall, ParameterExpression rootParameter, out string? path)
        {
            var sourceExpression = methodCall.Object ?? methodCall.Arguments.FirstOrDefault();
            var lambdaArgument = (methodCall.Object == null ? methodCall.Arguments.Skip(1) : methodCall.Arguments)
                .Select(StripQuote)
                .OfType<LambdaExpression>()
                .FirstOrDefault();

            if (sourceExpression == null || lambdaArgument == null)
            {
                path = null;
                return false;
            }

            var applyBody = lambdaArgument.Body is ConditionalExpression conditional
                ? (conditional.IfFalse is ConstantExpression { Value: null } ? conditional.IfTrue : conditional.IfFalse)
                : lambdaArgument.Body;

            if (!TryGetMemberPath(sourceExpression, rootParameter, out var outerPath) ||
                !TryGetMemberPath(applyBody, lambdaArgument.Parameters[0], out var innerPath))
            {
                path = null;
                return false;
            }

            path = string.IsNullOrWhiteSpace(outerPath)
                ? innerPath
                : string.IsNullOrWhiteSpace(innerPath)
                    ? outerPath
                    : $"{outerPath}.{innerPath}";
            return true;
        }

        private static bool TryGetMemberPath(Expression expression, ParameterExpression rootParameter, out string? path)
        {
            expression = StripConversion(expression);
            if (expression == rootParameter)
            {
                path = string.Empty;
                return true;
            }

            if (expression is MemberExpression member && TryGetMemberPath(member.Expression!, rootParameter, out var parentPath))
            {
                var currentPath = ToCamelCase(member.Member.Name);
                path = string.IsNullOrWhiteSpace(parentPath)
                    ? currentPath
                    : $"{parentPath}.{currentPath}";
                return true;
            }

            path = null;
            return false;
        }

        private static Expression StripConversion(Expression expression)
        {
            while (expression is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked || unary.NodeType == ExpressionType.TypeAs))
            {
                expression = unary.Operand;
            }

            return expression;
        }

        private static Expression StripQuote(Expression expression)
        {
            while (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Quote)
            {
                expression = unary.Operand;
            }

            return expression;
        }

        private static string ToCamelCase(string value)
        {
            return string.IsNullOrEmpty(value) || char.IsLower(value[0])
                ? value
                : char.ToLowerInvariant(value[0]) + value[1..];
        }
    }
}
