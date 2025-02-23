using System;
using System.Collections.Generic;
using System.Linq;
using HotChocolate.Language;
using HotChocolate.Language.Visitors;
using HotChocolate.Resolvers;
using HotChocolate.Stitching.Utilities;
using HotChocolate.Types;
using HotChocolate.Utilities;

namespace HotChocolate.Stitching.Processing;

internal sealed partial class ExtractFieldQuerySyntaxRewriter
    : SyntaxRewriter<ExtractFieldQuerySyntaxRewriter.Context>
{
    private readonly ISchema _schema;
    private readonly FieldDependencyResolver _dependencyResolver;
    private readonly IQueryDelegationRewriter[] _rewriters;

    public ExtractFieldQuerySyntaxRewriter(
        ISchema schema,
        IEnumerable<IQueryDelegationRewriter> rewriters)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _dependencyResolver = new FieldDependencyResolver(schema);
        _rewriters = rewriters.ToArray();
    }

    public ExtractedField ExtractField(
        NameString sourceSchema,
        DocumentNode document,
        OperationDefinitionNode operation,
        IFieldSelection selection,
        INamedOutputType declaringType)
    {
        if (document == null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (selection == null)
        {
            throw new ArgumentNullException(nameof(selection));
        }

        if (declaringType == null)
        {
            throw new ArgumentNullException(nameof(declaringType));
        }

        sourceSchema.EnsureNotEmpty(nameof(sourceSchema));

        var context = new Context(
            sourceSchema, declaringType,
            document, operation);

        var syntaxNodes = new List<FieldNode>();

        foreach (FieldNode syntaxNode in selection.SyntaxNodes)
        {
            FieldNode field = RewriteField(syntaxNode, context);

            if (selection.Field.Type.NamedType().IsLeafType() ||
                field.SelectionSet?.Selections.Count > 0)
            {
                syntaxNodes.Add(field);
            }
        }

        return new ExtractedField(
            syntaxNodes,
            context.Variables.Values.ToList(),
            context.Fragments.Values.ToList());
    }

    public IValueNode RewriteValueNode(
        NameString sourceSchema,
        IInputType inputType,
        IValueNode value)
    {
        sourceSchema.EnsureNotEmpty(nameof(sourceSchema));

        return RewriteNode(
            value,
            new Context(sourceSchema, null, null, null)
            {
                InputType = inputType
            });
    }

    protected override FieldNode RewriteField(
        FieldNode node,
        Context context)
    {
        if (context.TypeContext is IComplexOutputType type &&
            type.Fields.TryGetField(node.Name.Value, out IOutputField? field))
        {
            Context cloned = context.Clone();
            cloned.OutputField = field;

            NameNode name = RewriteNode(node.Name, context);
            NameNode? alias = RewriteNodeOrDefault(node.Alias, context);
            INullabilityNode? required = RewriteNodeOrDefault(node.Required, context);
            IReadOnlyList<DirectiveNode> directives = RewriteList(node.Directives, context);
            IReadOnlyList<ArgumentNode> arguments = RewriteList(node.Arguments, context);
            SelectionSetNode? selectionSet = RewriteNodeOrDefault(node.SelectionSet, context);

            if (field.TryGetSourceDirective(context.Schema, out SourceDirective? sourceDirective))
            {
                alias ??= name;
                name = new NameNode(sourceDirective.Name);
            }

            if (!ReferenceEquals(name, node.Name) ||
                !ReferenceEquals(alias, node.Alias) ||
                !ReferenceEquals(required, node.Required) ||
                !ReferenceEquals(directives, node.Directives) ||
                !ReferenceEquals(arguments, node.Arguments) ||
                !ReferenceEquals(selectionSet, node.SelectionSet))
            {
                node = new FieldNode(
                    node.Location,
                    name,
                    alias,
                    required,
                    directives,
                    arguments,
                    selectionSet);

                node = OnRewriteField(node, cloned);
            }
        }

        return node;
    }

    /*
    private FieldNode RewriteFieldSelectionSet(
        FieldNode node,
        IOutputField field,
        Context context)
    {
        FieldNode current = node;

        if (current.SelectionSet != null
            && field.Type.NamedType() is INamedOutputType n)
        {
            Context cloned = context.Clone();
            cloned.TypeContext = n;

            current = Rewrite
            (
                current,
                current.SelectionSet,
                cloned,
                RewriteSelectionSet,
                current.WithSelectionSet
            );
        }

        return current;
    }
    */

    private FieldNode OnRewriteField(
        FieldNode node,
        Context context)
    {
        if (_rewriters.Length == 0)
        {
            return node;
        }

        FieldNode current = node;

        for (var i = 0; i < _rewriters.Length; i++)
        {
            current = _rewriters[i].OnRewriteField(
                context.Schema,
                context.TypeContext!,
                context.OutputField!,
                current);
        }

        return current;
    }

    protected override SelectionSetNode RewriteSelectionSet(
        SelectionSetNode node,
        Context context)
    {
        SelectionSetNode current = node;

        var selections = new List<ISelectionNode>(current.Selections);

        ISet<FieldDependency> dependencies =
            _dependencyResolver.GetFieldDependencies(
                context.Document!, current, context.TypeContext!);

        RemoveDelegationFields(current, context, selections);
        AddDependencies(context.TypeContext!, selections, dependencies);

        if (!selections.OfType<FieldNode>().Any(n => n.Name.Value == WellKnownFieldNames.TypeName))
        {
            selections.Add(CreateField(WellKnownFieldNames.TypeName));
        }

        current = current.WithSelections(selections);
        current = base.RewriteSelectionSet(current, context);
        current = OnRewriteSelectionSet(current, context);

        return current;
    }

    private SelectionSetNode OnRewriteSelectionSet(
        SelectionSetNode node,
        Context context)
    {
        if (_rewriters.Length == 0)
        {
            return node;
        }

        SelectionSetNode current = node;

        for (var i = 0; i < _rewriters.Length; i++)
        {
            current = _rewriters[i].OnRewriteSelectionSet(
                context.Schema,
                context.TypeContext!,
                context.OutputField!,
                current);
        }

        return current;
    }

    protected override ArgumentNode RewriteArgument(
        ArgumentNode node,
        Context context)
    {
        ArgumentNode current = node;

        if (context.OutputField != null
            && context.OutputField.Arguments.TryGetField(
                current.Name.Value,
                out IInputField? inputField))
        {
            Context cloned = context.Clone();
            cloned.InputField = inputField;
            cloned.InputType = inputField.Type;

            if (inputField.TryGetSourceDirective(
                context.Schema,
                out SourceDirective? sourceDirective)
                && !sourceDirective.Name.Equals(current.Name.Value))
            {
                current = current.WithName(
                    new NameNode(sourceDirective.Name));
            }

            return base.RewriteArgument(current, cloned);
        }

        return base.RewriteArgument(current, context);
    }

    protected override ObjectFieldNode RewriteObjectField(
        ObjectFieldNode node,
        Context context)
    {
        ObjectFieldNode current = node;

        if (context.InputType?.NamedType() is InputObjectType inputType &&
            inputType.Fields.TryGetField(current.Name.Value, out InputField? inputField))
        {
            Context cloned = context.Clone();
            cloned.InputField = inputField;
            cloned.InputType = inputField.Type;

            if (inputField.TryGetSourceDirective(context.Schema,
                out SourceDirective? sourceDirective)
                && !sourceDirective.Name.Equals(current.Name.Value))
            {
                current = current.WithName(
                    new NameNode(sourceDirective.Name));
            }

            Rewrite(node.Value, context);

            return base.RewriteObjectField(current, cloned);
        }

        return base.RewriteObjectField(current, context);
    }

    private static void RemoveDelegationFields(
        SelectionSetNode node,
        Context context,
        ICollection<ISelectionNode> selections)
    {
        if (context.TypeContext is IComplexOutputType type)
        {
            foreach (FieldNode selection in node.Selections.OfType<FieldNode>())
            {
                if (type.Fields.TryGetField(selection.Name.Value, out IOutputField? field)
                    && IsDelegationField(field.Directives))
                {
                    selections.Remove(selection);
                }
            }
        }
    }

    /*
    protected override DirectiveNode RewriteDirective(
        DirectiveNode node,
        Context context)
        => Rewrite(
            node,
            node.Arguments, context,
            (p, c) => RewriteMany(p, c, RewriteArgument),
            node.WithArguments);
    */

    private static bool IsDelegationField(IDirectiveCollection directives)
    {
        return directives.Contains(DirectiveNames.Delegate)
            || directives.Contains(DirectiveNames.Computed);
    }

    private static void AddDependencies(
        Types.IHasName typeContext,
        List<ISelectionNode> selections,
        IEnumerable<FieldDependency> dependencies)
    {
        foreach (IGrouping<NameString, FieldDependency>? typeGroup in dependencies.GroupBy(t => t.TypeName))
        {
            var fields = new List<FieldNode>();

            foreach (NameString fieldName in typeGroup.Select(t => t.FieldName))
            {
                fields.Add(CreateField(fieldName));
            }

            if (typeGroup.Key.Equals(typeContext.Name))
            {
                selections.AddRange(fields);
            }
            else
            {
                selections.Add(new InlineFragmentNode
                (
                    null,
                    new NamedTypeNode(null, new NameNode(typeGroup.Key)),
                    Array.Empty<DirectiveNode>(),
                    new SelectionSetNode(null, fields)
                ));
            }
        }
    }

    private static FieldNode CreateField(string fieldName)
    {
        return new FieldNode
        (
            null,
            new NameNode(fieldName),
            null,
            null,
            Array.Empty<DirectiveNode>(),
            Array.Empty<ArgumentNode>(),
            null
        );
    }

    protected override VariableNode RewriteVariable(
        VariableNode node,
        Context context)
    {
        if (!context.Variables.ContainsKey(node.Name.Value))
        {
            VariableDefinitionNode? variableDefinition =
                context.Operation!.VariableDefinitions
                    .First(t => t.Variable.Name.Value.EqualsOrdinal(node.Name.Value));
            context.Variables[node.Name.Value] = variableDefinition!;
        }

        return base.RewriteVariable(node, context);
    }

    protected override FragmentSpreadNode RewriteFragmentSpread(
        FragmentSpreadNode node,
        Context context)
    {
        var name = node.Name.Value;

        if (!context.Fragments.TryGetValue(name, out FragmentDefinitionNode? fragment))
        {
            fragment = context.Document!.Definitions
                .OfType<FragmentDefinitionNode>()
                .First(t => t.Name.Value.EqualsOrdinal(name));
            fragment = RewriteFragmentDefinition(fragment, context);
            context.Fragments[name] = fragment;
        }

        return base.RewriteFragmentSpread(node, context);
    }

    protected override FragmentDefinitionNode RewriteFragmentDefinition(
        FragmentDefinitionNode node,
        Context context)
    {
        Context currentContext = context;
        FragmentDefinitionNode current = node;

        if (currentContext.FragmentPath.Contains(current.Name.Value))
        {
            return node;
        }

        if (_schema.TryGetType<IComplexOutputType>(current.TypeCondition.Name.Value, out var type))
        {
            currentContext = currentContext.Clone();
            currentContext.TypeContext = type;
            currentContext.FragmentPath = currentContext.FragmentPath.Add(current.Name.Value);

            if (type.TryGetSourceDirective(context.Schema, out SourceDirective? sourceDirective))
            {
                current = current.WithTypeCondition(
                    current.TypeCondition.WithName(
                        new NameNode(sourceDirective.Name)));
            }
        }

        return base.RewriteFragmentDefinition(current, currentContext);
    }

    protected override InlineFragmentNode RewriteInlineFragment(
        InlineFragmentNode node,
        Context context)
    {
        Context currentContext = context;
        InlineFragmentNode current = node;

        if (_schema.TryGetType<IComplexOutputType>(current.TypeCondition!.Name.Value, out var type))
        {
            currentContext = currentContext.Clone();
            currentContext.TypeContext = type;

            if (type.TryGetSourceDirective(
                context.Schema,
                out SourceDirective? sourceDirective))
            {
                current = current.WithTypeCondition(
                    current.TypeCondition.WithName(
                        new NameNode(sourceDirective.Name)));
            }
        }

        return base.RewriteInlineFragment(current, currentContext);
    }
}
