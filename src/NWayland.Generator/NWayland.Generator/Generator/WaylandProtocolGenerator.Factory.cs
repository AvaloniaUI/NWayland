using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NWayland.Generator
{
    partial class WaylandProtocolGenerator
    {
        private ClassDeclarationSyntax WithFactory(ClassDeclarationSyntax cl, WaylandProtocolInterface @interface)
        {
            var factoryInterfaceType = ParseTypeName($"IBindFactory<{cl.Identifier.Text}>");
            var fac = ClassDeclaration("ProxyFactory")
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)))
                .AddBaseListTypes(SimpleBaseType(factoryInterfaceType))
                .AddMembers(MethodDeclaration(
                        ParseTypeName("WlInterface*"), "GetInterface")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                    .WithBody(Block(ReturnStatement(GetWlInterfaceAddressFor(@interface.Name))))
                )
                .AddMembers(MethodDeclaration(
                        ParseTypeName(cl.Identifier.Text), "Create")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                    .WithParameterList(ParameterList(SeparatedList(new[]
                    {
                        Parameter(Identifier("handle")).WithType(ParseTypeName("IntPtr")),
                        Parameter(Identifier("version")).WithType(ParseTypeName("int"))
                    })))
                    .WithBody(Block(ReturnStatement(
                        ObjectCreationExpression(ParseTypeName(cl.Identifier.Text))
                            .WithArgumentList(ArgumentList(SeparatedList(new[]
                            {
                                Argument(IdentifierName("handle")),
                                Argument(IdentifierName("version"))
                            })))
                    )))
                );

            cl = cl
                .AddMembers(fac)
                .AddMembers(PropertyDeclaration(factoryInterfaceType, "BindFactory")
                    .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword)))
                    .WithAccessorList(AccessorList(SingletonList(
                        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Semicolon()))))
                    .WithInitializer(EqualsValueClause(
                        ObjectCreationExpression(ParseTypeName("ProxyFactory"))
                            .WithArgumentList(ArgumentList())

                    )).WithSemicolonToken(Semicolon())
                );

            return cl;
        }
    }
}
