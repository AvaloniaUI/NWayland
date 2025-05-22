using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private static EnumDeclarationSyntax CreateEnum(WaylandProtocolEnum en)
        {
            var decl = EnumDeclaration(Pascalize($"{en.Name}Enum"))
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));
            if (en.IsBitField)
                decl = decl.AddAttributeLists(AttributeList(SingletonSeparatedList(Attribute(IdentifierName("Flags")))));
            decl = WithSummary(decl, en.Description);
            foreach (var entry in en.Entries)
            {
                var parsed = ParseExpression(entry.Value);

                // Hack for enum members named like '270'
                var name = Pascalize(entry.Name);
                if (char.IsDigit(name[0]))
                    name = "k_" + name;
                var member = EnumMemberDeclaration(name)
                    .WithEqualsValue(EqualsValueClause(parsed));
                member = WithSummary(member, entry.Summary);
                decl = decl.AddMembers(member);
            }

            return decl;
        }

        private static ClassDeclarationSyntax WithEnums(ClassDeclarationSyntax cl, WaylandProtocol protocol, WaylandProtocolInterface @interface)
        {
            if (@interface.Enums is null)
                return cl;
            foreach (var en in @interface.Enums)
                cl = cl.AddMembers(CreateEnum(en));
            return cl;
        }
    }
}
