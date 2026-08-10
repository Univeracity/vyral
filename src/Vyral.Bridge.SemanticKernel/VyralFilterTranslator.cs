using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
using Vyral.Abstractions.Models;

namespace Vyral.Bridge.SemanticKernel;

internal static class VyralFilterTranslator
{
    public static FilterNode Translate<TRecord>(
        Expression<Func<TRecord, bool>> predicate,
        IReadOnlyDictionary<string, string> propertyPaths)
    {
        return TranslateExpression(StripConvert(predicate.Body), propertyPaths);
    }

    public static List<OrderExpression>? TranslateOrderBy<TRecord>(
        Func<FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition, FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition>? configureOrderBy,
        IReadOnlyDictionary<string, string> propertyPaths)
    {
        if (configureOrderBy == null)
        {
            return null;
        }

        var orderBy = configureOrderBy(new FilteredRecordRetrievalOptions<TRecord>.OrderByDefinition());
        if (orderBy?.Values == null || orderBy.Values.Count == 0)
        {
            return null;
        }

        var orderExpressions = new List<OrderExpression>();
        foreach (var sort in orderBy.Values)
        {
            if (!TryResolvePath(sort.PropertySelector.Body, propertyPaths, out var path))
            {
                throw new NotSupportedException("Semantic Kernel filtered retrieval ordering must use a mapped VYRAL property.");
            }

            orderExpressions.Add(new OrderExpression
            {
                Path = path,
                Direction = sort.Ascending ? "asc" : "desc"
            });
        }

        return orderExpressions;
    }

    private static FilterNode TranslateExpression(Expression expression, IReadOnlyDictionary<string, string> propertyPaths)
    {
        expression = StripConvert(expression);
        if (expression.NodeType == ExpressionType.AndAlso)
        {
            var children = FlattenBinaryConditions(expression, ExpressionType.AndAlso, propertyPaths);
            return new FilterNode { Combine = "all", Children = children };
        }

        if (expression.NodeType == ExpressionType.OrElse)
        {
            var children = FlattenBinaryConditions(expression, ExpressionType.OrElse, propertyPaths);
            return new FilterNode { Combine = "any", Children = children };
        }

        return TranslateLeafCondition(expression, propertyPaths);
    }

    private static List<FilterNode> FlattenBinaryConditions(
        Expression expression,
        ExpressionType nodeType,
        IReadOnlyDictionary<string, string> propertyPaths)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary && binary.NodeType == nodeType)
        {
            var nodes = FlattenBinaryConditions(binary.Left, nodeType, propertyPaths);
            nodes.AddRange(FlattenBinaryConditions(binary.Right, nodeType, propertyPaths));
            return nodes;
        }

        if (expression is BinaryExpression nested && nested.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
        {
            throw new NotSupportedException("Mixed AND/OR Semantic Kernel filters are not supported by the VYRAL bridge yet.");
        }

        return new List<FilterNode> { TranslateLeafCondition(expression, propertyPaths) };
    }

    private static FilterNode TranslateLeafCondition(Expression expression, IReadOnlyDictionary<string, string> propertyPaths)
    {
        expression = StripConvert(expression);
        return expression switch
        {
            BinaryExpression binary => TranslateBinaryLeaf(binary, propertyPaths),
            MethodCallExpression call => TranslateMethodCallLeaf(call, propertyPaths),
            _ => throw new NotSupportedException($"Semantic Kernel filter expression '{expression.NodeType}' is not supported by the VYRAL bridge.")
        };
    }

    private static FilterNode TranslateBinaryLeaf(BinaryExpression binary, IReadOnlyDictionary<string, string> propertyPaths)
    {
        var left = StripConvert(binary.Left);
        var right = StripConvert(binary.Right);

        if (TryResolvePath(left, propertyPaths, out var leftPath))
            return new FilterNode { Path = leftPath, Op = ToOperator(binary.NodeType, reversed: false), Value = GetValue(right) };

        if (TryResolvePath(right, propertyPaths, out var rightPath))
            return new FilterNode { Path = rightPath, Op = ToOperator(binary.NodeType, reversed: true), Value = GetValue(left) };

        throw new NotSupportedException("Semantic Kernel filter comparisons must compare a mapped property to a constant value.");
    }

    private static FilterNode TranslateMethodCallLeaf(MethodCallExpression call, IReadOnlyDictionary<string, string> propertyPaths)
    {
        if (call.Object != null &&
            call.Object.Type == typeof(string) &&
            call.Arguments.Count == 1 &&
            TryResolvePath(call.Object, propertyPaths, out var path))
        {
            var value = GetValue(call.Arguments[0]);
            if (value is not string)
                throw new NotSupportedException($"Semantic Kernel string filter '{call.Method.Name}' requires a string value.");

            var op = call.Method.Name switch
            {
                nameof(string.Contains) => "contains",
                nameof(string.StartsWith) => "startsWith",
                _ => null
            };

            if (op != null)
                return new FilterNode { Path = path, Op = op, Value = value };
        }

        if (call.Method.Name == nameof(Enumerable.Contains) && TryTranslateContainsLeaf(call, propertyPaths, out var node))
            return node;

        throw new NotSupportedException($"Semantic Kernel method filter '{call.Method.Name}' is not supported by the VYRAL bridge.");
    }

    private static bool TryTranslateContainsLeaf(
        MethodCallExpression call,
        IReadOnlyDictionary<string, string> propertyPaths,
        out FilterNode node)
    {
        node = new FilterNode();

        Expression? valuesExpression = null;
        Expression? propertyExpression = null;
        if (call.Object != null && call.Arguments.Count == 1)
        {
            valuesExpression = call.Object;
            propertyExpression = call.Arguments[0];
        }
        else if (call.Arguments.Count == 2)
        {
            valuesExpression = call.Arguments[0];
            propertyExpression = call.Arguments[1];
        }

        if (valuesExpression == null || propertyExpression == null ||
            !TryResolvePath(propertyExpression, propertyPaths, out var path))
            return false;

        var values = GetValue(valuesExpression);
        if (values is string || values is not IEnumerable)
            return false;

        node = new FilterNode { Path = path, Op = "in", Value = values };
        return true;
    }

    private static bool TryResolvePath(
        Expression expression,
        IReadOnlyDictionary<string, string> propertyPaths,
        out string path)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && member.Expression is ParameterExpression)
        {
            if (propertyPaths.TryGetValue(member.Member.Name, out path!))
            {
                return true;
            }

            path = member.Member.Name switch
            {
                "Id" => "/id",
                "PartitionKey" => "/partitionKey",
                _ => string.Empty
            };
            return path.Length > 0;
        }

        path = string.Empty;
        return false;
    }

    private static string ToOperator(ExpressionType nodeType, bool reversed)
    {
        return nodeType switch
        {
            ExpressionType.Equal => "eq",
            ExpressionType.NotEqual => "neq",
            ExpressionType.GreaterThan => reversed ? "lt" : "gt",
            ExpressionType.GreaterThanOrEqual => reversed ? "lte" : "gte",
            ExpressionType.LessThan => reversed ? "gt" : "lt",
            ExpressionType.LessThanOrEqual => reversed ? "gte" : "lte",
            _ => throw new NotSupportedException($"Semantic Kernel comparison '{nodeType}' is not supported by the VYRAL bridge.")
        };
    }

    private static object? GetValue(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        var lambda = Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object)));
        return lambda.Compile().Invoke();
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            expression = ((UnaryExpression)expression).Operand;
        }

        return expression;
    }
}
