using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MockQueryable.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MockQueryable.EntityFrameworkCore
{
    public class TestEFExtensionExpressionVisitor : TestExpressionVisitor
    {
        private static readonly MethodInfo _likeMethodInfo = typeof(DbFunctionsExtensions).GetTypeInfo().GetRuntimeMethod(
            nameof(DbFunctionsExtensions.Like), new[] { typeof(DbFunctions), typeof(string), typeof(string) });

        private static readonly MethodInfo _likeMethodInfoWithEscape = typeof(DbFunctionsExtensions).GetTypeInfo().GetRuntimeMethod(
            nameof(DbFunctionsExtensions.Like), new[] { typeof(DbFunctions), typeof(string), typeof(string), typeof(string) });

        private static readonly MethodInfo _inMemoryLikeMethodInfo =
            typeof(TestEFExtensionExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(InMemoryLike));

        // Regex special chars defined here:
        // https://msdn.microsoft.com/en-us/library/4edbef7e(v=vs.110).aspx
        private static readonly char[] _regexSpecialChars
            = { '.', '$', '^', '{', '[', '(', '|', ')', '*', '+', '?', '\\' };

        private static readonly string _defaultEscapeRegexCharsPattern = BuildEscapeRegexCharsPattern(_regexSpecialChars);

        private static readonly TimeSpan _regexTimeout = TimeSpan.FromMilliseconds(value: 1000.0);

        private static string BuildEscapeRegexCharsPattern(IEnumerable<char> regexSpecialChars)
            => string.Join("|", regexSpecialChars.Select(c => @"\" + c));

        public override Expression Visit(Expression node)
        {
            return base.Visit(node);
        }

        /// <inheritdoc />
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.Method == _likeMethodInfo
                || methodCallExpression.Method == _likeMethodInfoWithEscape)
            {
                // EF.Functions.Like
                var visitedArguments = new Expression[3];
                visitedArguments[2] = Expression.Constant(null, typeof(string));
                // Skip first DbFunctions argument
                for (var i = 1; i < methodCallExpression.Arguments.Count; i++)
                {
                    var argument = Visit(methodCallExpression.Arguments[i]);

                    visitedArguments[i - 1] = argument;
                }

                return Expression.Call(_inMemoryLikeMethodInfo, visitedArguments);
            }
            else
            {
                Expression[] arguments;
                Expression? @object = Visit(methodCallExpression.Object);

                arguments = new Expression[methodCallExpression.Arguments.Count];
                for (var i = 0; i < arguments.Length; i++)
                {
                    var argument = Visit(methodCallExpression.Arguments[i]);

                    arguments[i] = argument;
                }
                return methodCallExpression.Update(@object!, arguments);
            }
            return methodCallExpression;
        }

        private static bool InMemoryLike(string matchExpression, string pattern, string escapeCharacter)
        {
            //TODO: this fixes https://github.com/aspnet/EntityFramework/issues/8656 by insisting that
            // the "escape character" is a string but just using the first character of that string,
            // but we may later want to allow the complete string as the "escape character"
            // in which case we need to change the way we construct the regex below.
            var singleEscapeCharacter =
                (escapeCharacter == null || escapeCharacter.Length == 0)
                    ? (char?)null
                    : escapeCharacter.First();

            if (matchExpression == null
                || pattern == null)
            {
                return false;
            }

            if (matchExpression.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (matchExpression.Length == 0
                || pattern.Length == 0)
            {
                return false;
            }

            var escapeRegexCharsPattern
                = singleEscapeCharacter == null
                    ? _defaultEscapeRegexCharsPattern
                    : BuildEscapeRegexCharsPattern(_regexSpecialChars.Where(c => c != singleEscapeCharacter));

            var regexPattern
                = Regex.Replace(
                    pattern,
                    escapeRegexCharsPattern,
                    c => @"\" + c,
                    default,
                    _regexTimeout);

            var stringBuilder = new StringBuilder();

            for (var i = 0; i < regexPattern.Length; i++)
            {
                var c = regexPattern[i];
                var escaped = i > 0 && regexPattern[i - 1] == singleEscapeCharacter;

                switch (c)
                {
                    case '_':
                        {
                            stringBuilder.Append(escaped ? '_' : '.');
                            break;
                        }
                    case '%':
                        {
                            stringBuilder.Append(escaped ? "%" : ".*");
                            break;
                        }
                    default:
                        {
                            if (c != singleEscapeCharacter)
                            {
                                stringBuilder.Append(c);
                            }

                            break;
                        }
                }
            }

            regexPattern = stringBuilder.ToString();

            return Regex.IsMatch(
                matchExpression,
                @"\A" + regexPattern + @"\s*\z",
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                _regexTimeout);
        }

        [DebuggerStepThrough]
        private static bool TranslationFailed(Expression? original, Expression? translation)
            => original != null
            && (translation == NotTranslatedExpression);

        /// <summary>
        ///     <para>
        ///         Expression representing a not translated expression in query tree during translation phase.
        ///     </para>
        ///     <para>
        ///         This property is typically used by database providers (and other extensions). It is generally
        ///         not used in application code.
        ///     </para>
        /// </summary>
        public static readonly Expression NotTranslatedExpression = new NotTranslatedExpressionType();

        private sealed class NotTranslatedExpressionType : Expression
        {
            public override Type Type => typeof(object);
            public override ExpressionType NodeType => ExpressionType.Extension;
        }
    }
}