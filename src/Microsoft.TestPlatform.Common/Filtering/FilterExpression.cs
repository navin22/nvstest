// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Common.Filtering
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;

    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using CommonResources = Microsoft.VisualStudio.TestPlatform.Common.Resources.Resources;

    /// <summary>
    /// Represents an expression tree.
    /// Supports:
    ///     Logical Operators:  &, |
    ///     Equality Operators: =, !=
    ///     Parenthesis (, ) for grouping.
    /// </summary>
    internal class FilterExpression
    {

        /// <summary>
        /// Seperator string to seperate various tokens in input string.
        /// </summary>
        private static string filterExpressionSeperatorString = @"(\&)|(\|)|(\()|(\))";


        internal const string FullyQualifiedNamePropertyName = "FullyQualifiedName";
        internal const string NormalizedFullyQualifiedNameFilterKeyword = "NFQN";

        /// <summary>
        /// Condition, if expression is conditional expression.
        /// </summary>
        private Condition condition;

        /// <summary>
        /// Left operand, when expression is logical expression.
        /// </summary>
        private FilterExpression left;

        /// <summary>
        /// Right operand, when expression is logical expression.
        /// </summary>
        private FilterExpression right;

        /// <summary>
        /// If logical expression is using logical And ('&') operator.
        /// </summary>
        private bool areJoinedByAnd;

        private string fastFilterPropertyName;
        private HashSet<string> fastFilter;

        private bool UseFastFilter => fastFilter != null;

        #region Constructors

        private FilterExpression()
        {
        }

        private FilterExpression(Condition condition)
        {
            ValidateArg.NotNull(condition, "condition");
            this.condition = condition;
        }
        #endregion

        /// <summary>
        /// True, if filter expression is empty.
        /// </summary>
        internal bool IsEmpty 
            => (this.left == null && this.right == null && this.condition == null) || this.UseFastFilter;

        /// <summary>
        /// Create a new filter expression 'And'ing 'this' with 'filter'. 
        /// </summary>
        private FilterExpression And(FilterExpression filter)
        {
            if (this.IsEmpty)
            {
                return filter;
            }

            if (filter.IsEmpty)
            {
                return this;
            }

            var result = new FilterExpression();

            result.left = this;
            result.right = filter;
            result.areJoinedByAnd = true;
            return result;
        }

        /// <summary>
        /// Create a new filter expression 'Or'ing 'this' with 'filter'. 
        /// </summary>
        private FilterExpression Or(FilterExpression filter)
        {
            var result = this.And(filter);
            result.areJoinedByAnd = false;
            return result;
        }


        /// <summary>
        /// Process the given operator from the filterStack. 
        /// Puts back the result of operation back to filterStack.
        /// </summary>
        private static void ProcessOperator(Stack<FilterExpression> filterStack, Operator op)
        {
            if (op == Operator.And)
            {
                if (filterStack.Count < 2)
                {
                    throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingOperand));
                }

                var filterRight = filterStack.Pop();
                var filterLeft = filterStack.Pop();
                var result = filterLeft.And(filterRight);
                filterStack.Push(result);
            }
            else if (op == Operator.Or)
            {
                if (filterStack.Count < 2)
                {
                    throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingOperand));
                }

                var filterRight = filterStack.Pop();
                var filterLeft = filterStack.Pop();
                var result = filterLeft.Or(filterRight);
                filterStack.Push(result);
            }
            else if (op == Operator.OpenBrace)
            {
                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingCloseParenthesis));
            }
            else
            {
                Debug.Assert(false, "ProcessOperator called for Unexpected operator.");
                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, string.Empty));
            }
        }



        /// <summary>
        /// True, if filter is valid for given set of properties.
        /// When False, invalidProperties would contain properties making filter invalid.
        /// </summary>
        internal string[] ValidForProperties(IEnumerable<string> properties, Func<string, TestProperty> propertyProvider)
        {
            if (this.UseFastFilter)
            {
                // If the property name for fast filter is "NFQN", we will check if FQN is supported.
                var propertyName = fastFilterPropertyName.Equals(FilterExpression.NormalizedFullyQualifiedNameFilterKeyword, StringComparison.OrdinalIgnoreCase)
                    ? FilterExpression.FullyQualifiedNamePropertyName
                    : fastFilterPropertyName;

                return properties.Contains(propertyName, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : new[] { propertyName };
            }

            string[] invalidProperties = null;

            if (null == properties)
            {
                // if null, initialize to empty list so that invalid properties can be found.
                properties = Enumerable.Empty<string>();
            }

            bool valid = false;
            if (this.condition != null)
            {
                valid = this.condition.ValidForProperties(properties, propertyProvider);
                if (!valid)
                {
                    invalidProperties = new string[1] { this.condition.Name };
                }
            }
            else
            {
                invalidProperties = this.left.ValidForProperties(properties, propertyProvider);
                var invalidRight = this.right.ValidForProperties(properties, propertyProvider);
                if (null == invalidProperties)
                {
                    invalidProperties = invalidRight;
                }
                else if (null != invalidRight)
                {
                    invalidProperties = invalidProperties.Concat(invalidRight).ToArray();
                }
            }
            return invalidProperties;
        }


        /// <summary>
        /// Return FilterExpression after parsing the given filter expression.
        /// </summary>
        internal static FilterExpression Parse(string filterString)
        {
            ValidateArg.NotNull(filterString, "filterString");

            // below parsing doesn't error out on pattern (), so explicitly search for that (empty parethesis).
            var invalidInput = Regex.Match(filterString, @"\(\s*\)");
            if (invalidInput.Success)
            {
                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.EmptyParenthesis));
            }

            var tokens = Regex.Split(filterString, filterExpressionSeperatorString);
            var operatorStack = new Stack<Operator>();
            var filterStack = new Stack<FilterExpression>();

            // We try to build a hash table for an filter expression consists of only a single kind of property with 'equal' operation and '|' operator in parallel.
            // If by the time we finished parsing the entire expression and `isRunByFullyQualifiedName` is still true, then this hash table will be used instead
            // for "run by fully qualified name".
            var canUseFastFilter = true;
            string filterPropertyName = null;
            var filterHashSet = new HashSet<string>();

            // This is based on standard parsing of inorder expression using two stacks (operand stack and operator stack)
            // Predence(And) > Predence(Or)
            foreach (var inputToken in tokens)
            {
                var token = inputToken.Trim();
                if (string.IsNullOrEmpty(token))
                {
                    // ignore empty tokens
                    continue;
                }

                switch (token)
                {
                    case "&":
                    case "|":
                        if (token == "&")
                        {
                            canUseFastFilter = false;
                        }

                        Operator currentOperator = Operator.And;
                        if (string.Equals("|", token))
                        {
                            currentOperator = Operator.Or;
                        }

                        // Always put only higher priority operator on stack.
                        //  if lesser prioriy -- pop up the stack and process the operator to maintain operator precedence.
                        //  if equal priority -- pop up the stack and process the operator to maintain operator associativity.
                        //  OpenBrace is special condition. & or | can come on top of OpenBrace for case like ((a=b)&c=d)
                        while (true)
                        {
                            bool isEmpty = operatorStack.Count == 0;
                            Operator stackTopOperator = isEmpty ? Operator.None : operatorStack.Peek();
                            if (isEmpty || stackTopOperator == Operator.OpenBrace || stackTopOperator < currentOperator)
                            {
                                operatorStack.Push(currentOperator);
                                break;
                            }
                            stackTopOperator = operatorStack.Pop();
                            ProcessOperator(filterStack, stackTopOperator);
                        }
                        break;

                    case "(":
                        operatorStack.Push(Operator.OpenBrace);
                        break;

                    case ")":
                        // process operators from the stack till OpenBrace is found.
                        // If stack is empty at any time, than matching OpenBrace is missing from the expression.
                        if (operatorStack.Count == 0)
                        {
                            throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingOpenParenthesis));
                        }

                        Operator temp = operatorStack.Pop();
                        while (temp != Operator.OpenBrace)
                        {
                            ProcessOperator(filterStack, temp);
                            if (operatorStack.Count == 0)
                            {
                                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingOpenParenthesis));
                            }
                            temp = operatorStack.Pop();
                        }

                        break;

                    default:
                        // push the operand to the operand stack.
                        Condition condition = Condition.Parse(token);
                        FilterExpression filter = new FilterExpression(condition);
                        filterStack.Push(filter);

                        if (filterPropertyName == null)
                        {
                            filterPropertyName = condition.Name;
                        }

                        if (canUseFastFilter
                            && condition.Operation == Operation.Equal 
                            && condition.Name.Equals(filterPropertyName, StringComparison.OrdinalIgnoreCase))
                        {
                            filterHashSet.Add(condition.Value);
                        }
                        else
                        {
                            canUseFastFilter = false;
                        }
                        break;
                }
            }
            while (operatorStack.Count != 0)
            {
                Operator temp = operatorStack.Pop();
                ProcessOperator(filterStack, temp);
            }

            if (filterStack.Count != 1)
            {
                throw new FormatException(string.Format(CultureInfo.CurrentCulture, CommonResources.TestCaseFilterFormatException, CommonResources.MissingOperator));
            }

            if (canUseFastFilter)
            {
                var filterExpression = new FilterExpression();
                filterExpression.fastFilter = filterHashSet;
                filterExpression.fastFilterPropertyName = filterPropertyName;
                return filterExpression;
            }
            else
            {
                return filterStack.Pop();
            }
        }

        /// <summary>
        /// Evaluate filterExpression with given propertyValueProvider.
        /// </summary>
        /// <param name="propertyValueProvider"> The property Value Provider.</param>
        /// <param name="propertyValueRegexMatchOpt">Applies RegEx pattern on the property value</param>
        /// <returns> True if evaluation is successful. </returns>
        internal bool Evaluate(Func<string, Object> propertyValueProvider, Func<string, string> propertyValueRegexMatchOpt)
        {
            ValidateArg.NotNull(propertyValueProvider, "propertyValueProvider");

            if (this.UseFastFilter)
            {
                Debug.Assert(this.condition == null && this.left == null && this.right == null);

                if (fastFilterPropertyName.Equals(FilterExpression.NormalizedFullyQualifiedNameFilterKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return TryGetSinglePropertyValue(FilterExpression.FullyQualifiedNamePropertyName, propertyValueProvider, out var value)
                            && this.fastFilter.Contains(MakeNormalizedFQN(value));
                }
                else
                {
                    if (!TryGetSinglePropertyValue(fastFilterPropertyName, propertyValueProvider, out var value))
                    {
                        return false;
                    }

                    if (propertyValueRegexMatchOpt != null)
                    {
                        value = propertyValueRegexMatchOpt(value);
                        if (value == null)
                        {
                            return false;
                        }
                    }
                    return this.fastFilter.Contains(value);
                }
            }
            else
            {
                Debug.Assert(propertyValueRegexMatchOpt == null);
                return EvaluateRecursive(propertyValueProvider);
            }
        }

        private bool EvaluateRecursive(Func<string, Object> propertyValueProvider)
        {
            Debug.Assert(!this.IsEmpty, "Filter expression is empty.");

            bool filterResult = false;
            if (null != this.condition)
            {
                filterResult = this.condition.Evaluate(propertyValueProvider);
            }
            else
            {
                // & or | operator
                bool leftResult = this.left.EvaluateRecursive(propertyValueProvider);
                bool rightResult = this.right.EvaluateRecursive(propertyValueProvider);
                if (this.areJoinedByAnd)
                {
                    filterResult = leftResult && rightResult;
                }
                else
                {
                    filterResult = leftResult || rightResult;
                }
            }
            return filterResult;
        }

        private static string MakeNormalizedFQN(string value)
        {
            var indexOfSpace = value.IndexOf(" ");
            return indexOfSpace > 0 ? value.Substring(0, value.IndexOf(" ")) : value;
        }

        private static bool TryGetSinglePropertyValue(string name, Func<string, Object> propertyValueProvider, out string singleValue)
        {
            singleValue = propertyValueProvider(name) as string;
            return singleValue != null;
        }
    }
}
