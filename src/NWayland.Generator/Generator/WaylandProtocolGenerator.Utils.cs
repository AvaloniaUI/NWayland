using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        public static string Pascalize(string name, bool camel = false)
        {
            var upperizeNext = !camel;
            var sb = new StringBuilder(name.Length);
            foreach (var och in name)
            {
                var ch = och;
                if (ch == '_')
                {
                    upperizeNext = true;
                }
                else
                {
                    if (upperizeNext)
                    {
                        ch = char.ToUpperInvariant(ch);
                        upperizeNext = false;
                    }

                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private string ProtocolNamespace(string protocol) => _protocolNamespaces[protocol];

        private NameSyntax ProtocolNamespaceSyntax(string protocol)
            => IdentifierName(ProtocolNamespace(protocol));

        private static T WithSummary<T>(T member, WaylandProtocolDescription? description) where T : MemberDeclarationSyntax
            => WithSummary(member, description?.Value);

        private static T WithSummary<T>(T member, string? description) where T : MemberDeclarationSyntax
        {
            if (string.IsNullOrWhiteSpace(description))
                return member;

            IEnumerable<SyntaxToken> tokens = description
                .Replace("\r", "")
                .Split('\n')
                .Select(static line => XmlTextLiteral(line.TrimStart()))
                .SelectMany(static l => new[] { l, XmlTextNewLine("\n") })
                .SkipLast(1);

            XmlElementSyntax summary = XmlElement("summary",
                SingletonList<XmlNodeSyntax>(XmlText(TokenList(tokens))));

            return member.WithLeadingTrivia(
                Trivia(DocumentationComment(summary, XmlText("\r\n"))),
                Comment("//CRLF"), EndOfLine("\r\n"));
        }

        private static LiteralExpressionSyntax MakeLiteralExpression(string literal)
            => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(literal));

        private static LiteralExpressionSyntax MakeLiteralExpression(int literal)
            => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(literal));

        private static LiteralExpressionSyntax MakeLiteralExpression(bool literal)
            => LiteralExpression(literal ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);

        private static LiteralExpressionSyntax MakeNullLiteralExpression() =>
            LiteralExpression(SyntaxKind.NullLiteralExpression, Token(SyntaxKind.NullKeyword));

        private string GetWlInterfaceTypeName(string wlTypeName) => _protocolFullNames[wlTypeName];
        private string GetWlInterfaceTypeNameOrWlProxy(string? wlTypeName)
        {
            if (wlTypeName == null)
                return "global::NWayland.WlProxy";
            return _protocolFullNames[wlTypeName];
        }

        private RefExpressionSyntax GetWlInterfaceRefFor(string wlTypeName)
            => RefExpression(
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(GetWlInterfaceTypeName(wlTypeName)),
                    IdentifierName("WlInterface")));

        private InvocationExpressionSyntax GetWlInterfaceAddressFor(string wlTypeName)
            => InvocationExpression(MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("WlInterface"), IdentifierName("GeneratorAddressOf")),
                ArgumentList(SingletonSeparatedList(Argument(
                    GetWlInterfaceRefFor(wlTypeName)))));

        private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax expr, string identifier) =>
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expr, IdentifierName(identifier));

        private static SyntaxToken Semicolon() => Token(SyntaxKind.SemicolonToken);

        private static FieldDeclarationSyntax DeclareConstant(string type, string name, ExpressionSyntax value)
            => FieldDeclaration(
                    VariableDeclaration(ParseTypeName(type),
                        SingletonSeparatedList(
                            VariableDeclarator(name).WithInitializer(EqualsValueClause(value))
                        ))
                ).WithSemicolonToken(Semicolon())
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.ConstKeyword)));

        private string? TryGetEnumTypeReference(string protocol, string @interface, string message, string arg, string? en)
        {
            en = FindEnumTypeNameOverride(protocol, @interface, message, arg) ?? en;
            if (en is null)
                return null;

            static string GetName(string n) => $"{Pascalize(n)}Enum";

            if (!en.Contains('.'))
                return GetName(en);
            var sp = en.Split(new[] {'.'}, 2);
            return $"{GetWlInterfaceTypeName(sp[0])}.{GetName(sp[1])}";
        }

        private string? FindEnumTypeNameOverride(string protocol, string @interface, string message, string s) => null;

        private string GetTypeNameForArray(string protocolName, string interfaceName, string member, string argName)
        {
            foreach(var m in _arrayMappings)
                if (m.Protocol == protocolName && m.Interface == interfaceName && m.Member == member &&
                    m.Argument == argName)
                    return m.TypeName;
            //Console.Error.WriteLine($"Unknown array type for {protocol}:{@interface}:{message}:{argument}");
            return "byte";

        }


        private static InvocationExpressionSyntax InvokeMemberCrLf(ExpressionSyntax expr, string member,
            params ExpressionSyntax[] args) => InvokeMember(expr, member, args).CrLf();
        
        private static InvocationExpressionSyntax InvokeMember(ExpressionSyntax expr, string member,
            params ExpressionSyntax[] args)
        {
            var memberExpr = MemberAccess(expr, member);
            if (args.Length == 0)
                return InvocationExpression(memberExpr);
            var l = args.Length == 1 ? SingletonSeparatedList(Argument(args[0])) : SeparatedList(args.Select(Argument));

            return InvocationExpression(MemberAccess(expr, member), ArgumentList(l));
        }
    }
}
